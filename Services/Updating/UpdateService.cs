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
    /// <summary>哪个算法没过（"MD5" / "SHA256"）。</summary>
    public string Kind { get; }

    /// <summary>发布方给出的期望值。</summary>
    public string Expected { get; }

    /// <summary>这条线路上实际下到的值。</summary>
    public string Actual { get; }

    /// <param name="hint">可选：给用户看的一句处置建议（接在冒号那句后面）。</param>
    public ChecksumMismatchException(string algorithm, string expected, string actual, string? hint = null)
        : base($"{algorithm} 校验不通过：期望 {expected}，实际 {actual}"
               + (string.IsNullOrEmpty(hint) ? "" : $"。{hint}"))
    {
        Kind = algorithm;
        Expected = expected;
        Actual = actual;
    }
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
    ///
    /// ⚠️⚠️ <b>校验不过 ⇒ 换下一条线路重下，不是"更新失败"</b>（2026-10-11 定案）。
    ///    起因：自建节点的 spool 缓存把大包写坏了 —— 字节数一样、内容块顺序错乱，
    ///    而带 Range 的探速请求走的是一条**不经过 spool** 的路、完全正常 ⇒
    ///    「自动」选路每次都精准挑中这条唯一会发坏包的线路，用户端表现为「MD5 一直不过」。
    ///    老逻辑在这里直接抛异常，明明候选列表末尾还挂着 GitHub 官方直连，更新却死了。
    ///
    /// ⚠️ 走哪条链接由「GitHub 下载体验优化」设置决定（见 <see cref="GithubRoute"/>）——
    ///    更新包与普通下载共用同一份设置与同一份探速结果；自建节点取不到签名会依次回落到公益镜像、官方直链。
    ///    （只加速安装包本体；那个几百字节的 .md5 校验文件仍直接问 GitHub，不值得为它换路。）
    /// </summary>
    public async Task<DownloadedPackage> DownloadAndVerifyAsync(
        UpdatePackage package, string destinationFile, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
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

        // 期望值与线路无关 ⇒ 循环外只取一次
        // （原来是"先把包下完、回头再取校验值"：一条线路发坏包，整轮更新就死在这儿）
        var expectedMd5 = await ResolveMd5Async(package, ct);
        var expectedSha = package.Sha256;

        if (expectedMd5.Length == 0 && expectedSha.Length == 0)
            AppLog.Warn("update", $"更新包「{package.Name}」既未提供 MD5 也未提供 SHA256，本次无法校验（已跳过）");

        var candidates = await GithubRoute.ResolveAsync(package.Url.ToString(), ct);

        // 上一轮发过坏包的线路排到最后：那种包重下第二遍纯属白费流量（一次 60+ MB）
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
                    progress?.Report(new DownloadProgress(0, package.Size, 0));
                    AppLog.Warn("update",
                        $"更新包候选 {i + 1}/{candidates.Count}（{HostOf(candidate.Url)}）不可用（{ex.Message}）→ 换下一条");
                }
                continue;
            }

            var actualMd5 = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
            var actualSha = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();

            // ⚠️ 校验不过 = **这条线路给的包是坏的**，不是"更新失败" ⇒ 换下一条重下。
            //    候选列表末尾永远挂着 GitHub 官方直连兜底，所以正常情况下总能拿到好包。
            //    （2026-10-11：自建节点 spool 把大包写坏，老逻辑在这儿直接抛 ⇒ 用户看到"MD5 一直不过"。）
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

        // 候选全部试完：能留下校验现场就报现场（诊断价值更高），否则报最后那条下载错误
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

    /// <summary>
    /// 校验口径集中在这一处：对不上就返回那个异常，全对返回 null。
    /// 两个算法都给时都要过（SHA256 是 GitHub 自己算的 digest，白送的保险）。
    /// </summary>
    private static ChecksumMismatchException? Verify(string expectedMd5, string expectedSha, string actualMd5, string actualSha)
    {
        if (expectedMd5.Length > 0 && !string.Equals(expectedMd5, actualMd5, StringComparison.OrdinalIgnoreCase))
            return new ChecksumMismatchException("MD5", expectedMd5, actualMd5);
        if (expectedSha.Length > 0 && !string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
            return new ChecksumMismatchException("SHA256", expectedSha, actualSha);
        return null;
    }

    // ── "这条线路最近发过坏包"的记账（进程内，不落盘）──────────────────────────
    //
    // 为什么要有：坏包是**节点侧**的问题（2026-10-11：自建节点的 spool 把大包写坏，
    // 字节数一样、内容块顺序错乱），同一条线路重下第二遍几乎必然还是坏包 ——
    // 一个包 60+ MB，白下一遍纯属浪费流量和时间。
    // 所以校验不过的那条线路，本次进程内**排到候选队尾**（不是删掉：别的都挂了它还能兜底）。
    //
    // ⚠️ 只按**主机名**记，不按完整 URL：坏的是那台机器，不是那一个文件。
    // ⚠️ 不落盘 + 有 TTL（重启或半小时后自动忘掉）：节点修好了不该被永久拉黑；
    //    而"永久黑名单"一旦误判（比如其实是我们自己取错了期望值）就再也没法自愈。

    private static readonly Dictionary<string, DateTime> _badRouteUntil = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan BadRouteTtl = TimeSpan.FromMinutes(30);

    /// <summary>日志里说"哪条线路"时用主机名 —— 完整 URL 太长，还带着签名参数。</summary>
    private static string HostOf(string url)
    {
        try { return new Uri(url).Host; }
        catch { return url.Length > 48 ? url[..48] + "…" : url; }
    }

    private static bool IsRecentlyBad(string url) =>
        _badRouteUntil.TryGetValue(HostOf(url), out var until) && DateTime.Now < until;

    private static void MarkRecentlyBad(string url) =>
        _badRouteUntil[HostOf(url)] = DateTime.Now.Add(BadRouteTtl);

    /// <summary>
    /// 一次性抓一条链接到 <paramref name="temp"/>，边下边喂两个哈希。
    /// 一次只试一条；失败/取消都把 .part 删掉再往外抛（重试由调用方换下一条候选）。
    /// 候选自带的头（自建节点的 Referer / X-Api-Key）必须原样发出去 —— 漏了直接 403。
    ///
    /// ⚠️ 进度报的是 <see cref="DownloadProgress"/>（含已下载字节 / 总字节 / 速度），不是光秃秃的百分比 ——
    ///    更新下载的进度要显示在「任务进行」页的任务卡片上，那里要跟普通下载是同一种快照。
    ///    上报按 **120 毫秒节流**：<c>Progress&lt;T&gt;</c> 每次 Report 都会 Post 到 UI 线程，
    ///    128 KB 一块、60 MB 的包就是 480 次，不节流会把 UI 线程喂撑（卡片反而卡）。
    /// </summary>
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
        // File.Create 是截断式的 —— 换下一条候选时天然从头写，不会把两段拼在一起
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

        // 少一截就别往下走了：说了多少就得拿到多少。
        // 在这里拦住，报的是"下载不完整"（比后面那句"校验不通过"好懂），也不会把半截包当成"这条线路给的包"。
        if (total > 0 && done != total)
            throw new IOException($"下载不完整：拿到 {done} 字节，应为 {total} 字节");

        if (total > 0) progress?.Report(new DownloadProgress(done, total, SpeedOf(done, clock)));
    }

    /// <summary>这一轮传输的平均速度（字节/秒）。样本太短就返回 0 —— 别拿头几毫秒除出一个吓人的数字。</summary>
    private static double SpeedOf(long done, System.Diagnostics.Stopwatch clock)
    {
        var seconds = clock.Elapsed.TotalSeconds;
        return seconds >= 0.15 ? done / seconds : 0;
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

        // Inno Setup 静默参数：/SP- 不显示"准备安装"提示，/CLOSEAPPLICATIONS 自动关掉占用的应用。
        // ⚠️ 桌面快捷方式（desktopicon 任务）**不再强制勾选**：升级时尊重用户当前的选择 ——
        //   桌面上还留着旧图标就带上 /TASKS="desktopicon" 保留它；用户之前删过就别再建回来。
        //   （手动重跑安装包走交互式「附加任务」页时，.iss 的 InitializeWizard 会按同一逻辑预填。）
        var desktopLnk = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "ClassSoftwareHub.lnk");
        var tasksArg = File.Exists(desktopLnk) ? "/TASKS=\"desktopicon\"" : "";
        var installerArgs =
            $"/SP- /SILENT /NORESTART /CLOSEAPPLICATIONS /SUPPRESSMSGBOXES {tasksArg}".Trim();

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
