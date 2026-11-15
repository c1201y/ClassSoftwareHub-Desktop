using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.Updating;


public sealed class ChecksumMismatchException : Exception
{
    
    public string Kind { get; }

    
    public string Expected { get; }

    
    public string Actual { get; }

    
    public ChecksumMismatchException(string algorithm, string expected, string actual, string? hint = null)
        : base($"{algorithm} 校验不通过：期望 {expected}，实际 {actual}"
               + (string.IsNullOrEmpty(hint) ? "" : $"。{hint}"))
    {
        Kind = algorithm;
        Expected = expected;
        Actual = actual;
    }
}


public sealed record DownloadedPackage(string FilePath, string Md5, string Sha256, bool Verified, string VerifyNote);





public sealed class UpdateService
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,   
    };

    public IUpdateSource Source { get; }

    public UpdateService(IUpdateSource source) => Source = source;

    
    public static UpdateService CreateDefault() => new(new GitHubReleaseSource(
        ShellConfig.UpdateRepoOwner,
        ShellConfig.UpdateRepoName,
        ShellConfig.UpdateToken));

    

    public async Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, string currentVersion, CancellationToken ct = default)
    {
        if (!Source.IsConfigured)
        {
            AppLog.Warn("update", "检查更新：更新源未配置，直接放弃");
            return UpdateCheckResult.None("尚未配置更新源，暂时无法检查更新。");
        }

        var ch = UpdateChannels.ToDisplay(channel);
        AppLog.Info("update", $"▶ 检查更新 · 通道={ch} · 当前版本 {currentVersion}");

        
        
        IReadOnlyList<UpdateRelease> releases;
        try
        {
            releases = await Source.GetReleasesAsync(channel, 10, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Fail("update", ex, $"检查更新失败（通道={ch}）");
            throw;
        }

        if (releases.Count == 0)
        {
            AppLog.Step("update", $"{ch} 通道下暂时没有发布");
            return UpdateCheckResult.None($"{ch} 通道下暂时还没有发布。");
        }

        var comparer = new VersionTextComparer();
        var newest = releases.OrderByDescending(r => r.Version, comparer).First();

        if (!VersionCompare.IsNewer(newest.Version, currentVersion))
        {
            AppLog.Step("update", $"已是最新（最新发布 {newest.Tag}，通道内共 {releases.Count} 条）");
            return UpdateCheckResult.None($"已是最新（{ch} 通道）。");
        }

        AppLog.Done("update", $"发现新版本 {newest.Tag}（{ch}），当前 {currentVersion}");
        return new UpdateCheckResult(true, newest, $"发现新版本 {newest.Tag}（{ch} 通道）");
    }

    
    
    
    
    
    public async Task<IReadOnlyList<UpdateRelease>> GetHistoryAsync(UpdateChannel channel, int max = 20, CancellationToken ct = default)
    {
        if (!Source.IsConfigured) return Array.Empty<UpdateRelease>();

        
        var releases = await Source.GetReleasesAsync(channel, max, ct);

        var comparer = new VersionTextComparer();
        return releases
            .GroupBy(r => r.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(r => r.Version, comparer)
            .ThenByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue)
            .Take(max)
            .ToList();
    }

    

    
    
    
    
    
    
    
    
    
    
    
    
    
    public async Task<DownloadedPackage> DownloadAndVerifyAsync(
        UpdatePackage package, string destinationFile, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
        var temp = destinationFile + ".part";
        if (File.Exists(temp)) File.Delete(temp);

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        
        
        var sw = System.Diagnostics.Stopwatch.StartNew();
        AppLog.Begin("update", $"下载安装包「{package.Name}」· {package.Url}");

        
        
        var expectedMd5 = await ResolveMd5Async(package, ct);
        var expectedSha = package.Sha256;

        if (expectedMd5.Length == 0 && expectedSha.Length == 0)
            AppLog.Warn("update", $"更新包「{package.Name}」既未提供 MD5 也未提供 SHA256，本次无法校验（已跳过）");

        var candidates = await GithubRoute.ResolveAsync(package.Url.ToString(), ct);

        
        var pending = new List<DownloadCandidate>();
        var demoted = new List<DownloadCandidate>();
        foreach (var c in candidates)
            (IsRecentlyBad(c.Url) ? demoted : pending).Add(c);
        pending.AddRange(demoted);
        candidates = pending;
        if (demoted.Count > 0)
            AppLog.Step("update", $"最近发过坏包的线路排到最后：{string.Join("、", demoted.Select(c => HostOf(c.Url)))}");

        AppLog.Step("update", $"线路候选 {candidates.Count} 条 · 落盘 {destinationFile}");

        Exception? lastError = null;
        ChecksumMismatchException? lastMismatch = null;

        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = candidates[i];
            try
            {
                await FetchPackageAsync(candidate, package, temp, md5, sha, progress, ct);
                lastError = null;
            }
            catch (OperationCanceledException)
            {
                TryDelete(temp);
                throw;                                   
            }
            catch (Exception ex)
            {
                lastError = ex;
                TryDelete(temp);
                
                md5.GetHashAndReset();
                sha.GetHashAndReset();
                if (i < candidates.Count - 1)
                {
                    progress?.Report(new DownloadProgress(0, package.Size, 0));
                    AppLog.Warn("update",
                        $"更新包候选 {i + 1}/{candidates.Count}（{HostOf(candidate.Url)}）不可用（{ex.Message}）→ 换下一条");
                }
                continue;
            }

            var actualMd5 = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
            var actualSha = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();

            
            
            
            var mismatch = Verify(expectedMd5, expectedSha, actualMd5, actualSha);
            if (mismatch is not null)
            {
                lastMismatch = mismatch;
                MarkRecentlyBad(candidate.Url);
                TryDelete(temp);
                if (i < candidates.Count - 1)
                {
                    progress?.Report(new DownloadProgress(0, package.Size, 0));
                    AppLog.Warn("update",
                        $"更新包候选 {i + 1}/{candidates.Count}（{HostOf(candidate.Url)}）给的包校验不过："
                        + $"{mismatch.Message} → 换下一条重下");
                }
                continue;
            }

            var verified = expectedMd5.Length > 0 || expectedSha.Length > 0;
            var note = expectedMd5.Length > 0
                ? "MD5 已校验通过"
                : expectedSha.Length > 0
                    ? "SHA256 已校验通过（该发布未提供 MD5）"
                    : "未提供校验值（已跳过校验）";

            if (File.Exists(destinationFile)) File.Delete(destinationFile);
            File.Move(temp, destinationFile);

            long finalSize = 0;
            try { finalSize = new FileInfo(destinationFile).Length; } catch { }
            AppLog.Done("update",
                $"更新包「{package.Name}」就绪 · {Core.AppLog.Size(finalSize)} · {note}"
                + (i > 0 ? $"（第 {i + 1} 条线路）" : ""), sw.Elapsed);

            return new DownloadedPackage(destinationFile, actualMd5, actualSha, verified, note);
        }

        
        if (lastMismatch is not null)
        {
            var final = new ChecksumMismatchException(
                lastMismatch.Kind, lastMismatch.Expected, lastMismatch.Actual,
                $"{candidates.Count} 条下载线路给的包都不可信；可在「设置 → 软件内容」把下载路径换成 GitHub 源后重试");
            AppLog.Fail("update", final,
                $"更新包「{package.Name}」校验不通过（{final.Kind}）—— {candidates.Count} 条线路全部试过");
            throw final;
        }

        lastError ??= new IOException("没有可用的下载线路");
        AppLog.Fail("update", lastError, $"更新包「{package.Name}」下载失败（{candidates.Count} 条候选全部试过）");
        throw lastError;
    }

    
    
    
    
    private static ChecksumMismatchException? Verify(string expectedMd5, string expectedSha, string actualMd5, string actualSha)
    {
        if (expectedMd5.Length > 0 && !string.Equals(expectedMd5, actualMd5, StringComparison.OrdinalIgnoreCase))
            return new ChecksumMismatchException("MD5", expectedMd5, actualMd5);
        if (expectedSha.Length > 0 && !string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
            return new ChecksumMismatchException("SHA256", expectedSha, actualSha);
        return null;
    }

    
    
    
    
    
    
    
    
    
    

    private static readonly Dictionary<string, DateTime> _badRouteUntil = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan BadRouteTtl = TimeSpan.FromMinutes(30);

    
    private static string HostOf(string url)
    {
        try { return new Uri(url).Host; }
        catch { return url.Length > 48 ? url[..48] + "…" : url; }
    }

    private static bool IsRecentlyBad(string url) =>
        _badRouteUntil.TryGetValue(HostOf(url), out var until) && DateTime.Now < until;

    private static void MarkRecentlyBad(string url) =>
        _badRouteUntil[HostOf(url)] = DateTime.Now.Add(BadRouteTtl);

    
    
    
    
    
    
    
    
    
    
    private static async Task FetchPackageAsync(
        DownloadCandidate candidate, UpdatePackage package, string temp,
        IncrementalHash md5, IncrementalHash sha, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, candidate.Url);
        candidate.ApplyTo(req);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? package.Size;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        
        await using var dst = File.Create(temp);

        var buffer = new byte[128 * 1024];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long done = 0;
        long lastTick = -200;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            md5.AppendData(buffer, 0, read);
            sha.AppendData(buffer, 0, read);
            done += read;

            if (total > 0 && clock.ElapsedMilliseconds - lastTick >= 120)
            {
                lastTick = clock.ElapsedMilliseconds;
                progress?.Report(new DownloadProgress(done, total, SpeedOf(done, clock)));
            }
        }

        
        
        if (total > 0 && done != total)
            throw new IOException($"下载不完整：拿到 {done} 字节，应为 {total} 字节");

        if (total > 0) progress?.Report(new DownloadProgress(done, total, SpeedOf(done, clock)));
    }

    
    private static double SpeedOf(long done, System.Diagnostics.Stopwatch clock)
    {
        var seconds = clock.Elapsed.TotalSeconds;
        return seconds >= 0.15 ? done / seconds : 0;
    }

    
    public static async Task<string> ResolveMd5Async(UpdatePackage package, CancellationToken ct)
    {
        var raw = package.Md5 ?? "";
        if (raw.Length == 0) return "";
        if (!raw.StartsWith("asset:", StringComparison.OrdinalIgnoreCase))
            return NormalizeMd5(raw);

        var url = raw[6..];
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return "";
            var text = await resp.Content.ReadAsStringAsync(ct);

            
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                if (!package.Name.IsEmpty() && trimmed.Contains(package.Name, StringComparison.OrdinalIgnoreCase))
                {
                    var hit = NormalizeMd5(trimmed);
                    if (hit.Length > 0) return hit;
                }
            }
            return NormalizeMd5(text);
        }
        catch
        {
            return "";
        }
    }

    private static string NormalizeMd5(string text)
    {
        var m = Regex.Match(text, @"\b[0-9a-fA-F]{32}\b");
        return m.Success ? m.Value.ToLowerInvariant() : "";
    }

    
    
    
    
    public static async Task<(string Md5, string Sha256)> HashFileAsync(string path, bool withSha256, CancellationToken ct)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha = withSha256 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;

        await using var fs = File.OpenRead(path);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = await fs.ReadAsync(buffer, ct)) > 0)
        {
            md5.AppendData(buffer, 0, read);
            sha?.AppendData(buffer, 0, read);
        }

        return (Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant(),
                sha is null ? "" : Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant());
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    

    
    
    
    
    
    
    
    
    
    
    
    
    
    public static void RunInstaller(string installerPath)
    {
        var ext = Path.GetExtension(installerPath).ToLowerInvariant();

        
        
        var sizeText = "";
        try
        {
            sizeText = File.Exists(installerPath)
                ? $"（{Core.AppLog.Size(new FileInfo(installerPath).Length)}）"
                : "（⚠ 文件不存在，安装必然失败）";
        }
        catch { }
        AppLog.Info("update", $"▶ 拉起安装程序 · {installerPath}{sizeText}");

        if (ext == ".msi")
        {
            var msi = new ProcessStartInfo("msiexec.exe", $"/i \"{installerPath}\" /qb /norestart")
            {
                UseShellExecute = true,
            };
            Process.Start(msi);
            AppLog.Info("update", "msiexec 已拉起，应用即将退出");
            return;
        }

        
        
        
        
        var desktopLnk = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "ClassSoftwareHub.lnk");

        
        
        
        
        
        var tasksArg = File.Exists(desktopLnk)
            ? "/MERGETASKS=\"desktopicon\""
            : "/MERGETASKS=\"!desktopicon\"";
        var installerArgs =
            $"/SP- /SILENT /NORESTART /CLOSEAPPLICATIONS /SUPPRESSMSGBOXES {tasksArg}".Trim();

        
        
        
        var psi = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c ping -n 4 127.0.0.1 >nul & \"{installerPath}\" {installerArgs}",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process.Start(psi);
        AppLog.Info("update", "安装程序已排队（延迟约 3 秒启动，留给应用退出的窗口）");
    }

    
    public static string UpdatesDir => Path.Combine(AppPaths.DataDir, "updates");
}


public sealed class VersionTextComparer : IComparer<string>
{
    public int Compare(string? x, string? y) => VersionCompare.Compare(x, y);
}

internal static class StringHelper
{
    public static bool IsEmpty(this string? s) => string.IsNullOrEmpty(s);
}
