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

/// <summary>下载下来的包校验不过。</summary>
public sealed class ChecksumMismatchException : Exception
{
    public ChecksumMismatchException(string algorithm, string expected, string actual)
        : base($"{algorithm} 校验不通过：期望 {expected}，实际 {actual}") { }
}

/// <summary>下载完成后的结果（含实际算出来的校验值）。</summary>
public sealed record DownloadedPackage(string FilePath, string Md5, string Sha256, bool Verified, string VerifyNote);

/// <summary>
/// 更新流程编排：查 → 下 → 校验 → 装。
/// 只管"怎么更新"，不管"从哪儿拿"（那是 IUpdateSource 的事）。
/// </summary>
public sealed class UpdateService
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,   // 下载大文件不设总超时，靠 CancellationToken 控制
    };

    public IUpdateSource Source { get; }

    public UpdateService(IUpdateSource source) => Source = source;

    /// <summary>建一个默认更新源（GitHub，仓库地址从 ShellConfig 读；没配的话 IsConfigured = false）。</summary>
    public static UpdateService CreateDefault() => new(new GitHubReleaseSource(
        ShellConfig.UpdateRepoOwner,
        ShellConfig.UpdateRepoName,
        ShellConfig.UpdateToken));

    // ── 查 ────────────────────────────────────────────────────

    public async Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, string currentVersion, CancellationToken ct = default)
    {
        if (!Source.IsConfigured)
            return UpdateCheckResult.None("尚未配置更新源，暂时无法检查更新。");

        var releases = await Source.GetReleasesAsync(channel, 10, ct);
        if (releases.Count == 0)
            return UpdateCheckResult.None($"{UpdateChannels.ToDisplay(channel)} 通道下暂时还没有发布。");

        var comparer = new VersionTextComparer();
        var newest = releases.OrderByDescending(r => r.Version, comparer).First();

        if (!VersionCompare.IsNewer(newest.Version, currentVersion))
            return UpdateCheckResult.None($"已是最新（{UpdateChannels.ToDisplay(channel)} 通道）。");

        return new UpdateCheckResult(true, newest,
            $"发现新版本 {newest.Tag}（{UpdateChannels.ToDisplay(channel)} 通道）");
    }

    /// <summary>
    /// 拉历史版本（新的在前），给「回滚」用，最多 max 条；<b>按用户所在通道过滤</b>：
    /// 正式版通道只列正式版，预览版通道才连预览版（Beta）一起列。
    /// 回滚不校验版本高低，用户点哪个装哪个。
    /// </summary>
    public async Task<IReadOnlyList<UpdateRelease>> GetHistoryAsync(UpdateChannel channel, int max = 20, CancellationToken ct = default)
    {
        if (!Source.IsConfigured) return Array.Empty<UpdateRelease>();

        // Source 那边已经按通道过滤过：Stable 会跳过 prerelease，Insider 则两种都收。
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

    // ── 下 + 校验 ─────────────────────────────────────────────

    /// <summary>
    /// 下载安装包并校验。校验优先级：MD5（你要求的）→ SHA256（GitHub 自带的 digest）→ 都没有就只报"未校验"。
    /// 校验不过会删掉文件并抛 ChecksumMismatchException。
    /// </summary>
    public async Task<DownloadedPackage> DownloadAndVerifyAsync(
        UpdatePackage package, string destinationFile, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
        var temp = destinationFile + ".part";
        if (File.Exists(temp)) File.Delete(temp);

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        using (var req = new HttpRequestMessage(HttpMethod.Get, package.Url))
        using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? package.Size;

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(temp);

            var buffer = new byte[128 * 1024];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                md5.AppendData(buffer, 0, read);
                sha.AppendData(buffer, 0, read);
                done += read;
                if (total > 0) progress?.Report(Math.Min(1.0, done / (double)total));
            }
        }

        var actualMd5 = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
        var actualSha = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();

        // 期望值：MD5 可能是 "<安装包>.md5 / checksums.md5" 这种校验文件，需要现拉
        var expectedMd5 = await ResolveMd5Async(package, ct);
        var expectedSha = package.Sha256;

        var verified = false;
        var note = "未提供校验值（已跳过校验）";

        if (expectedMd5.Length > 0)
        {
            if (!string.Equals(expectedMd5, actualMd5, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(temp);
                throw new ChecksumMismatchException("MD5", expectedMd5, actualMd5);
            }
            verified = true;
            note = "MD5 已校验通过";
            // 顺手把 SHA256 也比一下（有的话）
            if (expectedSha.Length > 0 && !string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(temp);
                throw new ChecksumMismatchException("SHA256", expectedSha, actualSha);
            }
        }
        else if (expectedSha.Length > 0)
        {
            if (!string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(temp);
                throw new ChecksumMismatchException("SHA256", expectedSha, actualSha);
            }
            verified = true;
            note = "SHA256 已校验通过（该发布没提供 MD5）";
        }

        if (File.Exists(destinationFile)) File.Delete(destinationFile);
        File.Move(temp, destinationFile);

        return new DownloadedPackage(destinationFile, actualMd5, actualSha, verified, note);
    }

    /// <summary>把 package.Md5 里的 "asset:URL" 解析成真正的 32 位十六进制 MD5。</summary>
    private static async Task<string> ResolveMd5Async(UpdatePackage package, CancellationToken ct)
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

            // 校验文件里可能是 "d41d8... *ClassSoftwareHub-Setup-dv1.0.0.exe" 这种格式
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

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ── 装 ────────────────────────────────────────────────────

    /// <summary>
    /// 静默跑安装包（Inno Setup 的 /SILENT 那套；.msi 走 msiexec）。
    /// Inno 会按同一个 AppId 识别出已安装 → 原地升级，**保留用户当初选的安装目录**。
    /// 调用方（一般是设置页）随后应该让应用自己退出，别跟安装程序抢文件。
    /// </summary>
    public static void RunInstaller(string installerPath)
    {
        var ext = Path.GetExtension(installerPath).ToLowerInvariant();
        ProcessStartInfo psi;
        if (ext == ".msi")
        {
            psi = new ProcessStartInfo("msiexec.exe", $"/i \"{installerPath}\" /qb /norestart");
        }
        else
        {
            // Inno Setup 静默参数：/SP- 不显示"准备安装"提示，/CLOSEAPPLICATIONS 自动关掉占用的应用，
            // /TASKS="desktopicon" 保证升级后桌面快捷方式还在
            psi = new ProcessStartInfo(installerPath)
            {
                Arguments = "/SP- /SILENT /NORESTART /CLOSEAPPLICATIONS /SUPPRESSMSGBOXES /TASKS=\"desktopicon\"",
            };
        }
        psi.UseShellExecute = true;
        Process.Start(psi);
    }

    /// <summary>更新包的落地目录：%LOCALAPPDATA%\ClassSoftwareHub\updates</summary>
    public static string UpdatesDir => Path.Combine(AppPaths.DataDir, "updates");
}

/// <summary>按 SemVer 比版本字符串。</summary>
public sealed class VersionTextComparer : IComparer<string>
{
    public int Compare(string? x, string? y) => VersionCompare.Compare(x, y);
}

internal static class StringHelper
{
    public static bool IsEmpty(this string? s) => string.IsNullOrEmpty(s);
}
