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
        {
            AppLog.Warn("update", "检查更新：更新源未配置，直接放弃");
            return UpdateCheckResult.None("尚未配置更新源，暂时无法检查更新。");
        }

        var ch = UpdateChannels.ToDisplay(channel);
        AppLog.Info("update", $"▶ 检查更新 · 通道={ch} · 当前版本 {currentVersion}");

        // ⚠️ 记完日志**原样抛出**（不改这里的行为）：调用方本来就靠异常来区分
        //    "网络不通"和"已是最新"，把它吞成 None 会让界面误报"已是最新"。
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
    ///
    /// ⚠️ 走哪条链接由「GitHub 下载体验优化」设置决定（见 <see cref="GithubRoute"/>）——
    ///    更新包与普通下载共用同一份设置与同一份探速结果；自建节点取不到签名会依次回落到公益镜像、官方直链。
    ///    走加速不影响安全：下面的 MD5 / SHA256 校验照常执行，改包会被当场拦下。
    ///    （只加速安装包本体；那个几百字节的 .md5 校验文件仍直接问 GitHub，不值得为它换路。）
    /// </summary>
    public async Task<DownloadedPackage> DownloadAndVerifyAsync(
        UpdatePackage package, string destinationFile, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
        var temp = destinationFile + ".part";
        if (File.Exists(temp)) File.Delete(temp);

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // ── 链路日志（2026-10-11）：更新是"最不能出错"的一条链路 ——
        //    以前它失败时界面上只有一句人话，日志里什么都查不到。 ──
        var sw = System.Diagnostics.Stopwatch.StartNew();
        AppLog.Begin("update", $"下载安装包「{package.Name}」· {package.Url}");

        var candidates = await GithubRoute.ResolveAsync(package.Url.ToString(), ct);
        AppLog.Step("update", $"线路候选 {candidates.Count} 条 · 落盘 {destinationFile}");

        Exception? lastError = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await FetchPackageAsync(candidates[i], package, temp, md5, sha, progress, ct);
                lastError = null;
                break;
            }
            catch (OperationCanceledException)
            {
                TryDelete(temp);
                throw;                                   // 用户自己取消的，不是这条路不行
            }
            catch (Exception ex)
            {
                lastError = ex;
                TryDelete(temp);
                // 半截数据的哈希必须清掉，否则第二次尝试算出来的是两段拼起来的摘要
                md5.GetHashAndReset();
                sha.GetHashAndReset();
                if (i < candidates.Count - 1)
                {
                    progress?.Report(0);
                    AppLog.Warn("update", $"更新包候选 {i + 1}/{candidates.Count} 不可用（{ex.Message}）→ 换下一条");
                }
            }
        }

        if (lastError is not null)
        {
            AppLog.Fail("update", lastError, $"更新包「{package.Name}」下载失败（{candidates.Count} 条候选全部试过）");
            throw lastError;
        }

        var actualMd5 = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
        var actualSha = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();

        // 期望值：MD5 可能是 "<安装包>.md5 / checksums.md5" 这种校验文件，需要现拉
        var expectedMd5 = await ResolveMd5Async(package, ct);
        var expectedSha = package.Sha256;

        var verified = false;
        var note = "未提供校验值（已跳过校验）";

        // 校验不过就把现场记全（期望值 / 实际值 / 哪个算法）—— 这类问题必须能事后复盘，
        // 只往界面弹一句"校验失败"是查不出所以然的。
        ChecksumMismatchException Mismatch(string kind, string expected, string actual)
        {
            var bad = new ChecksumMismatchException(kind, expected, actual);
            TryDelete(temp);
            AppLog.Fail("update", bad, $"更新包「{package.Name}」校验不通过（{kind}）");
            return bad;
        }

        if (expectedMd5.Length > 0)
        {
            if (!string.Equals(expectedMd5, actualMd5, StringComparison.OrdinalIgnoreCase))
                throw Mismatch("MD5", expectedMd5, actualMd5);
            verified = true;
            note = "MD5 已校验通过";
            // 顺手把 SHA256 也比一下（有的话）
            if (expectedSha.Length > 0 && !string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
                throw Mismatch("SHA256", expectedSha, actualSha);
        }
        else if (expectedSha.Length > 0)
        {
            if (!string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
                throw Mismatch("SHA256", expectedSha, actualSha);
            verified = true;
            note = "SHA256 已校验通过（该发布未提供 MD5）";
        }
        else
        {
            // 没校验值 = 走加速换过包也发现不了，属于"能用但不该这样"，记警告而不是错误
            AppLog.Warn("update", $"更新包「{package.Name}」既未提供 MD5 也未提供 SHA256，本次无法校验（已跳过）");
        }

        if (File.Exists(destinationFile)) File.Delete(destinationFile);
        File.Move(temp, destinationFile);

        long finalSize = 0;
        try { finalSize = new FileInfo(destinationFile).Length; } catch { }
        AppLog.Done("update",
            $"更新包「{package.Name}」就绪 · {Core.AppLog.Size(finalSize)} · {note}", sw.Elapsed);

        return new DownloadedPackage(destinationFile, actualMd5, actualSha, verified, note);
    }

    /// <summary>
    /// 一次性抓一条链接到 <paramref name="temp"/>，边下边喂两个哈希。
    /// 一次只试一条；失败/取消都把 .part 删掉再往外抛（重试由调用方换下一条候选）。
    /// 候选自带的头（自建节点的 Referer / X-Api-Key）必须原样发出去 —— 漏了直接 403。
    /// </summary>
    private static async Task FetchPackageAsync(
        DownloadCandidate candidate, UpdatePackage package, string temp,
        IncrementalHash md5, IncrementalHash sha, IProgress<double>? progress, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, candidate.Url);
        candidate.ApplyTo(req);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? package.Size;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        // File.Create 是截断式的 —— 换下一条候选时天然从头写，不会把两段拼在一起
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

    /// <summary>把 package.Md5 里的 "asset:URL" 解析成真正的 32 位十六进制 MD5。</summary>
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

    /// <summary>
    /// 给「本地已有安装包，复用前校验」用的：整文件流式算 MD5（+可选 SHA256）。
    /// 与下载路径同一套算法，保证校验口径一致。
    /// </summary>
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

    // ── 装 ────────────────────────────────────────────────────

    /// <summary>
    /// 静默跑安装包（Inno Setup 的 /SILENT 那套；.msi 走 msiexec）。
    /// Inno 会按同一个 AppId 识别出已安装 → 原地升级，**保留用户当初选的安装目录**。
    ///
    /// ⚠️⚠️ 对 .exe（Inno）**必须延迟启动**，不能直接拉起来：
    ///    Inno 一启动（实测 16 毫秒内）就检查 .iss 里的 <c>AppMutex</c>，那正是本应用的单实例 Mutex。
    ///    查到应用还在跑 → 弹「安装程序检测到 ClassSoftwareHub 当前正在运行，请先关闭…」，
    ///    而这个框会被 /SUPPRESSMSGBOXES **自动按「取消」处理** → 安装静默失败（退出码 1），
    ///    用户端看不到任何提示，只表现为"版本没变"。
    ///    2026-10-01 实测结论：应用在跑时安装 **100% 失败**；关掉应用再装，11 秒成功。
    ///    所以这里借 cmd 的 ping 拖约 3 秒，给调用方留出"先退出应用"的窗口。
    ///    调用方随后应**立即**退出，不要等（见 UpdateFlow.RunAsync / InstallPendingNowAsync）。
    /// </summary>
    public static void RunInstaller(string installerPath)
    {
        var ext = Path.GetExtension(installerPath).ToLowerInvariant();

        // 记下"要装哪个包、包在不在" —— 应用**紧接着就会退出**，日志到此为止，
        // 安装程序自己的行为不归我们管。所以这行是"点了更新之后什么都没发生"的唯一线索。
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

        // Inno Setup 静默参数：/SP- 不显示"准备安装"提示，/CLOSEAPPLICATIONS 自动关掉占用的应用，
        // /TASKS="desktopicon" 保证升级后桌面快捷方式还在
        const string installerArgs =
            "/SP- /SILENT /NORESTART /CLOSEAPPLICATIONS /SUPPRESSMSGBOXES /TASKS=\"desktopicon\"";

        // ⚠️ 计时用 ping 而不是 timeout：timeout 在没有控制台（CreateNoWindow=true）时会报
        //    "输入重定向不受支持，立即退出"，等于根本没等。
        //    ping -n 4 = 3 次间隔 ≈ 3 秒。
        var psi = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c ping -n 4 127.0.0.1 >nul & \"{installerPath}\" {installerArgs}",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process.Start(psi);
        AppLog.Info("update", "安装程序已排队（延迟约 3 秒启动，留给应用退出的窗口）");
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
