using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>下载进度快照（给界面用；Total 为 0 表示服务端没给长度 → 界面走不确定进度）。</summary>
public sealed record DownloadProgress(long Received, long Total, double BytesPerSecond)
{
    public bool Indeterminate => Total <= 0;

    public double Percent => Total > 0 ? Math.Clamp(Received * 100.0 / Total, 0, 100) : 0;

    public string SizeText => Total > 0
        ? $"{Size(Received)} / {Size(Total)}"
        : Size(Received);

    public string SpeedText => BytesPerSecond >= 1 ? Size((long)BytesPerSecond) + "/s" : "";

    /// <summary>「12.3 MB」这种人类可读体积。</summary>
    public static string Size(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{value:0} {units[unit]}"
            : $"{value:0.#} {units[unit]}";
    }
}

/// <summary>
/// 单次下载的路由选择。详情页按钮按语义显式指定，其余调用点使用默认值 <see cref="Setting"/>。
/// </summary>
public enum DownloadRoute
{
    /// <summary>跟随设置页「GitHub 应用更新加速源」的全局选择（更新包等自动下载走这条）。</summary>
    Setting,

    /// <summary>强制 GitHub 官方直链，不做任何加速改写（详情页「下载」按钮）。</summary>
    Official,

    /// <summary>强制加速链：自建优先 → 公益镜像测速择优 → 官方直链回退（详情页「加速下载」按钮）。</summary>
    Accelerated,
}

/// <summary>下载结果。</summary>
public sealed record DownloadedFile(string Path, string FileName, long Bytes)
{
    public string SizeText => DownloadProgress.Size(Bytes);
}

/// <summary>
/// 原生下载服务：流式写盘，先落 .part 再改名，支持取消、进度与测速。
///
/// 大文件采用多路并行（阈值见 <see cref="ParallelThreshold"/>）：
/// 单条 TCP 流的吞吐受接收窗口与往返时延限制，串行读写期间链路空转，
/// 在高时延的自建节点上单流速度很低。自建节点与各公益镜像均支持
/// <c>Accept-Ranges: bytes</c>（实测返回 206），因此先探测区间支持情况，
/// 再按区间分多路并行下载；探测失败或分片被拒时自动退回单流，保证下载始终可用。
/// </summary>
public static class DownloadService
{
    /// <summary>
    /// 默认目录为系统「下载」文件夹（读取注册表 known folder，用户自定义位置同样生效）；
    /// 不自行另建 Downloads 目录。
    /// </summary>
    public static string DefaultDir => _defaultDir ??= ResolveDownloadsDir();

    private static string? _defaultDir;

    private static string ResolveDownloadsDir()
    {
        // 系统「下载」文件夹的 known folder GUID
        const string id = "{374DE290-123F-4565-9164-39C4925E467B}";

        foreach (var sub in new[]
                 {
                     @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
                     @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders",
                 })
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(sub);
                if (key?.GetValue(id) is string raw && !string.IsNullOrWhiteSpace(raw))
                {
                    var path = Environment.ExpandEnvironmentVariables(raw);
                    if (path.Length > 0) return path;
                }
            }
            catch { /* 读不到就试下一个 */ }
        }

        // 回退方案：注册表全部读取失败时使用
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    /// <summary>在资源管理器中打开「下载」文件夹（任务列表底部按钮使用）。</summary>
    public static void OpenDownloadsFolder()
    {
        try
        {
            Directory.CreateDirectory(DefaultDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = DefaultDir,
                UseShellExecute = true,      // 经 shell 打开，由系统复用已有的资源管理器窗口
            });
        }
        catch
        {
            // 打开失败时静默处理，不提示用户
        }
    }

    // ────────────────────────── 并行下载的可调参数 ──────────────────────────

    /// <summary>小于这个大小不分片 —— 多开连接的开销（握手 + 收尾）比省下的时间还多。</summary>
    private const long ParallelThreshold = 8L * 1024 * 1024;

    /// <summary>每片的目标大小，据此推算分片数。</summary>
    private const long SegmentTargetBytes = 16L * 1024 * 1024;

    /// <summary>分片数上限：过多会加重服务端负载，也容易触发限速。</summary>
    private const int MaxSegments = 8;

    /// <summary>分片/单流的读缓冲大小：256 KB。</summary>
    private const int StreamBufferBytes = 256 * 1024;

    /// <summary>单个分片最多试几次（含首次）。</summary>
    private const int SegmentAttempts = 3;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            // 并行分片需要同时建立多条连接，显式设置上限（默认不限制）
            MaxConnectionsPerServer = 16,
        };
        var client = new HttpClient(handler)
        {
            // 大文件下载耗时可超过默认 100 秒超时，禁用总超时，改由 CancellationToken 控制
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ClassSoftwareHub/1.2");
        return client;
    }

    /// <summary>
    /// 下载到 <paramref name="directory"/>（默认「下载」文件夹）。
    /// 文件名优先用 <paramref name="suggestedName"/>，否则从 URL 猜；重名自动加 (1)(2)。
    /// </summary>
    public static async Task<DownloadedFile> DownloadAsync(
        string url,
        string? suggestedName = null,
        string? directory = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default,
        DownloadRoute route = DownloadRoute.Setting)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("下载地址为空。", nameof(url));

        var dir = string.IsNullOrWhiteSpace(directory) ? DefaultDir : directory!;
        Directory.CreateDirectory(dir);

        var fileName = ResolveFileName(url, suggestedName);

        // ── 链路日志（对齐上游 2026-10-11）：下载是用户最常撞坑、也最难自证的一条链路。
        //    以前失败时只把 ex.Message 弹到界面上，日志里一个字都没有；现在每一步都留痕。 ──
        var label = string.IsNullOrWhiteSpace(suggestedName) ? fileName : suggestedName!.Trim();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Core.AppLog.Begin("download", $"下载「{label}」开始 · 线路={route}");
        Core.AppLog.Step("download", $"原始链接 {url}");

        // 将原始链接翻译为候选链接，按序尝试，一条失败切换下一条（路由语义见 DownloadRoute）。
        IReadOnlyList<string> candidates = route switch
        {
            DownloadRoute.Official => new[] { url },
            DownloadRoute.Accelerated => await GithubRoute.ResolveAcceleratedAsync(url, ct).ConfigureAwait(false),
            // 默认（Setting）走全局设置，但这里要的是纯字符串候选，所以抖一层
            _ => (await GithubRoute.ResolveAsync(url, ct).ConfigureAwait(false)).Select(c => c.Url).ToList(),
        };

        Exception? lastError = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            string? target = null;
            string? part = null;
            try
            {
                // 探测请求（Range: bytes=0-0）：获取总长度、区间支持情况与服务端建议文件名。
                // 探测响应随即释放，不占用连接。
                var probe = await ProbeAsync(candidate, ct).ConfigureAwait(false);

                if (!HasKnownExtension(fileName) && !string.IsNullOrWhiteSpace(probe.ServerFileName))
                {
                    var better = Sanitize(probe.ServerFileName!.Trim('"'));
                    if (better.Length > 0) fileName = better;
                }

                // ⚠️ 占名字必须用 FileMode.CreateNew「锁」住（见 ReserveUniquePath 注释）
                target = ReserveUniquePath(dir, fileName);
                part = target + ".part";

                var received = await FetchAsync(candidate, part, probe, progress, ct).ConfigureAwait(false);

                File.Move(part, target, overwrite: true);
                progress?.Report(new DownloadProgress(received, received, 0));
                Core.AppLog.Done("download",
                    $"下载「{label}」完成 · {Core.AppLog.Size(received)} · {target}", sw.Elapsed);
                return new DownloadedFile(target, Path.GetFileName(target), received);
            }
            catch (OperationCanceledException)
            {
                // 用户主动取消不算失败，也不写失败日志
                TryDelete(part);
                if (target is not null) ReleaseIfEmpty(target);
                throw;
            }
            catch (Exception ex)
            {
                // 单条候选失败：删除未完成的 .part，继续下一条（空占位文件待全部失败后清理）
                lastError = ex;
                TryDelete(part);
                if (target is not null) ReleaseIfEmpty(target);
                if (i < candidates.Count - 1)
                {
                    // ⚠️ 客气地叫「警告」而不是「错误」：换路是**设计内的正常降级**，
                    //    只有全都试完还不行才是真失败（见循环外那条 Fail）。
                    Core.AppLog.Warn("download", $"候选 {i + 1}/{candidates.Count} 不可用（{ex.Message}）→ 换下一条");
                    progress?.Report(new DownloadProgress(0, 0, 0));
                }
                continue;
            }
        }

        // 所有候选都失败：抛出最后一条错误
        var final = lastError ?? new InvalidOperationException("下载失败。");
        Core.AppLog.Fail("download", final, $"下载「{label}」失败（{candidates.Count} 条候选全部试过）");
        throw final;
    }

    // ────────────────────────── 下载本体 ──────────────────────────

    /// <summary>
    /// 将 URL 下载到指定文件（覆盖写）：先探测能力，支持区间则并行分片，否则单流。
    /// 更新包下载（<c>UpdateService</c>）同样经由本方法，两条链路共用同一套策略。
    /// </summary>
    public static async Task<long> DownloadToFileAsync(
        string url, string path, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var probe = await ProbeAsync(url, ct).ConfigureAwait(false);
        return await FetchAsync(url, path, probe, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 探测后按能力选路：支持区间则多路并行，否则单流；分片失败时退回单流。
    ///
    /// ⚠️ 速度计时从开始接收数据时启动：代签、探测等前置往返不计入下载速度。
    /// </summary>
    private static async Task<long> FetchAsync(
        string url, string part, ProbeInfo probe, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var total = probe.Total;
        var segments = SegmentCountFor(total);

        if (probe.SupportsRange && segments > 1)
        {
            Core.AppLog.Info("download", $"并行 {segments} 路 · {DownloadProgress.Size(total)} · {url}");
            try
            {
                return await DownloadParallelAsync(
                        url, part, total, segments, progress, ct, Stopwatch.StartNew())
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 分片失败（服务端限制并发或返回非 206）时，同一候选退回单流继续下载。
                Core.AppLog.Info("download", "分片失败，退回单流：" + ex.Message);
                TryDelete(part);
            }
        }

        Core.AppLog.Info("download", $"单流 · {DownloadProgress.Size(total)} · {url}");
        return await DownloadSingleAsync(url, part, total, progress, ct, Stopwatch.StartNew())
            .ConfigureAwait(false);
    }

    /// <summary>探测结果：总长度 / 是否支持区间请求 / 服务端给的文件名。</summary>
    private sealed record ProbeInfo(long Total, bool SupportsRange, string? ServerFileName);

    /// <summary>
    /// 先要一个字节看看：总多大、支不支持 <c>Range</c>、以及 <c>Content-Disposition</c> 里的文件名。
    /// <c>HttpCompletionOption.ResponseHeadersRead</c> + 立刻 Dispose ⇒ 只握手、不拉正文。
    /// </summary>
    private static async Task<ProbeInfo> ProbeAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        if (GithubRoute.IsAcceleratedUrl(url))
            request.Headers.Referrer = new Uri(GithubRoute.AcceleratorReferer);

        using var response = await Http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long total;
        var supportsRange = response.StatusCode == HttpStatusCode.PartialContent;
        if (supportsRange)
            total = response.Content.Headers.ContentRange?.Length ?? -1;
        else
            total = response.Content.Headers.ContentLength ?? -1;

        // 部分服务端对 1 字节区间不返回 206，但声明 Accept-Ranges: bytes 时同样视为支持区间
        if (!supportsRange && total > 0 && response.Headers.AcceptRanges.Contains("bytes"))
            supportsRange = true;

        var name = response.Content.Headers.ContentDisposition?.FileNameStar
                   ?? response.Content.Headers.ContentDisposition?.FileName;

        return new ProbeInfo(total, supportsRange, name);
    }

    /// <summary>这条总长度该切几路（1 = 不分片）。</summary>
    private static int SegmentCountFor(long total)
    {
        if (total < ParallelThreshold) return 1;
        var bySize = (int)Math.Ceiling(total / (double)SegmentTargetBytes);
        return Math.Clamp(bySize, 2, MaxSegments);
    }

    /// <summary>单流下载：顺序读取写入。服务端不支持区间请求时使用。</summary>
    private static async Task<long> DownloadSingleAsync(
        string url, string part, long total,
        IProgress<DownloadProgress>? progress, CancellationToken ct, Stopwatch watch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (GithubRoute.IsAcceleratedUrl(url))
            request.Headers.Referrer = new Uri(GithubRoute.AcceleratorReferer);

        using var response = await Http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (total <= 0) total = response.Content.Headers.ContentLength ?? -1;

        var pump = new ProgressPump(progress, total, watch);
        pump.Reset();

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var sink = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None,
            StreamBufferBytes, true);

        var buffer = new byte[StreamBufferBytes];
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) break;

            await sink.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            pump.Add(read);
        }

        pump.Complete();
        return pump.Received;
    }

    /// <summary>
    /// 多路并行：将文件按区间切分，各分片同时下载并按偏移写入同一个 .part。
    /// 每个分片持有独立的 <see cref="FileStream"/>，写入区间互不重叠，无需加锁。
    /// </summary>
    private static async Task<long> DownloadParallelAsync(
        string url, string part, long total, int segments,
        IProgress<DownloadProgress>? progress, CancellationToken ct, Stopwatch watch)
    {
        // 预先将 .part 扩展到最终大小，各分片按偏移直接写入
        using (var seed = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 1, false))
            seed.SetLength(total);

        var pump = new ProgressPump(progress, total, watch);
        pump.Reset();

        var tasks = new Task[segments];
        for (var i = 0; i < segments; i++)
        {
            // 按乘除切分区间：无遗漏，最后一片自动包含余数
            var start = total * i / segments;
            var end = total * (i + 1) / segments - 1;
            tasks[i] = DownloadSegmentAsync(url, part, start, end, pump, ct);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        pump.Complete();
        return pump.Received;
    }

    /// <summary>下载指定区间并写入 .part 的对应偏移；失败时按 <see cref="SegmentAttempts"/> 重试。</summary>
    private static async Task DownloadSegmentAsync(
        string url, string part, long start, long end, ProgressPump pump, CancellationToken ct)
    {
        var want = end - start + 1;
        Exception? last = null;

        for (var attempt = 1; attempt <= SegmentAttempts; attempt++)
        {
            // 重试将重写本分片：先回退已计入进度的字节数，避免进度偏高
            long billed = 0;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(start, end);
                if (GithubRoute.IsAcceleratedUrl(url))
                    request.Headers.Referrer = new Uri(GithubRoute.AcceleratorReferer);

                using var response = await Http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.PartialContent)
                    throw new IOException($"服务端没按区间返回（HTTP {(int)response.StatusCode}）");

                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var sink = new FileStream(part, FileMode.Open, FileAccess.Write, FileShare.ReadWrite,
                    0, true);
                sink.Seek(start, SeekOrigin.Begin);

                var buffer = new byte[StreamBufferBytes];
                long got = 0;
                while (got < want)
                {
                    var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read <= 0) break;
                    if (read > want - got) read = (int)(want - got);

                    await sink.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    pump.Add(read);
                    billed += read;
                    got += read;
                }

                if (got != want)
                    throw new IOException($"区间 {start}-{end} 只收到 {got}/{want} 字节");
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                if (billed > 0) pump.Add(-billed);
                if (attempt < SegmentAttempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct).ConfigureAwait(false);
            }
        }

        throw last ?? new IOException("分片下载失败。");
    }

    /// <summary>
    /// 进度上报器：多分片线程并发计数，使用 <see cref="Interlocked"/> 保证原子性；
    /// 上报限流约 8 次/秒，避免 UI 线程过载。
    /// </summary>
    private sealed class ProgressPump
    {
        private readonly IProgress<DownloadProgress>? _sink;
        private readonly long _total;
        private readonly Stopwatch _watch;
        private readonly object _gate = new();
        private long _received;
        private long _lastMs = long.MinValue / 2;

        public ProgressPump(IProgress<DownloadProgress>? sink, long total, Stopwatch watch)
        {
            _sink = sink;
            _total = total;
            _watch = watch;
        }

        public long Received => Interlocked.Read(ref _received);

        /// <summary>开始时先上报 0%，使界面立即切换到进度条显示。</summary>
        public void Reset()
        {
            lock (_gate) _lastMs = 0;
            _sink?.Report(new DownloadProgress(0, _total, 0));
        }

        public void Add(long bytes)
        {
            var now = Interlocked.Add(ref _received, bytes);
            var ms = _watch.ElapsedMilliseconds;

            lock (_gate)
            {
                if (ms - _lastMs < 120 && now < _total) return;
                _lastMs = ms;
            }

            var seconds = _watch.Elapsed.TotalSeconds;
            _sink?.Report(new DownloadProgress(now, _total,
                seconds > 0 && now > 0 ? now / seconds : 0));
        }

        /// <summary>结束时上报完成进度；总长度未知时以实际接收字节数为准。</summary>
        public void Complete()
            => _sink?.Report(new DownloadProgress(_total > 0 ? _total : Received, _total, 0));
    }

    // ────────────────────────── 文件名解析 ──────────────────────────

    /// <summary>
    /// 「这是不是一个能下载的文件」的判定已统一到 <see cref="Data.DownloadLinkKind"/>（与站点
    /// src/gallery/downloadLink.ts 同源，含 kind 显式声明、商店页、网盘页三类）。
    /// 这里原有一份只看扩展名的 IsDirectFileUrl，两套判据会漂移，已删除（2026-10-18 对齐上游 1.2.0）——
    /// 判定请调 <c>DownloadLinkKind.Of(url) == DownloadKind.File</c>。
    /// </summary>

    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".appx", ".appxbundle", ".msixbundle",
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".xz", ".bz2", ".zst",
        ".cab", ".iso", ".img", ".bin", ".dmg", ".pkg", ".deb", ".rpm", ".apk",
        ".apks", ".jar", ".whl", ".crx",
    };

    /// <summary>
    /// 文件名确定规则：
    ///   1) 优先使用链接中的原始文件名；
    ///   2) 链接文件名不可靠（无扩展名、hash 命名、通用占位名）时，使用调用方提供的建议名称；
    ///   3) 扩展名按白名单识别，避免将「…v0.2.8」中的「.8」误判为扩展名。
    /// </summary>
    public static string ResolveFileName(string url, string? suggestedName)
    {
        var fromUrl = Sanitize(FileNameFromUrl(url));
        var urlHasRealExt = HasKnownExtension(fromUrl);

        if (fromUrl.Length > 0 && urlHasRealExt && !LooksLikeJunk(fromUrl))
            return fromUrl;

        if (!string.IsNullOrWhiteSpace(suggestedName))
        {
            var name = Sanitize(suggestedName!);
            if (name.Length > 0)
                // ⚠️ 必须传完整 url：WithKnownExtension 内部执行 new Uri(source).AbsolutePath，
                //    传入裸文件名会抛 UriFormatException 并被上层吞掉，导致扩展名无法补全，
                //    最终保存的文件缺少 .zip/.exe 等扩展名。
                return WithKnownExtension(name, url);
        }

        return fromUrl.Length > 0 ? fromUrl : "download.bin";
    }

    /// <summary>URL 末段（URL 解码后）当文件名。</summary>
    private static string FileNameFromUrl(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath.TrimEnd('/');
            var last = path.Split('/')[^1];
            return Uri.UnescapeDataString(last);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>判断扩展名是否在白名单内。</summary>
    public static bool HasKnownExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var ext = Path.GetExtension(fileName);
        return ext.Length > 1 && FileExtensions.Contains(ext);
    }

    /// <summary>文件名缺少已知扩展名时，从 URL 或原始文件名中补全。</summary>
    private static string WithKnownExtension(string name, string source)
    {
        if (HasKnownExtension(name)) return name;

        var ext = Path.GetExtension(FileNameFromUrl(source));
        return ext.Length > 1 && FileExtensions.Contains(ext) ? name + ext : name;
    }

    /// <summary>判断文件名是否为 hash 命名或通用占位名。</summary>
    private static bool LooksLikeJunk(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length < 2) return true;
        if (stem.Equals("download", StringComparison.OrdinalIgnoreCase)) return true;
        if (stem.Equals("file", StringComparison.OrdinalIgnoreCase)) return true;
        if (stem.Equals("setup", StringComparison.OrdinalIgnoreCase)) return false;

        // 长十六进制/数字串，通常是 CDN 或 Release 的 hash 文件名
        return stem.Length >= 24 && stem.All(c => Uri.IsHexDigit(c) || c == '-' || c == '_');
    }

    private static string Sanitize(string name)
    {
        name = name.Trim().Trim('"');
        foreach (var bad in Path.GetInvalidFileNameChars())
            name = name.Replace(bad, '_');
        return name.Trim(' ', '.');
    }

    /// <summary>
    /// 取一个可用文件名并立即创建 0 字节占位文件，返回完整路径。
    ///
    /// ⚠️ 不能只做存在性检查：目标文件要等下载结束才由 File.Move 产生，
    ///    两次调用之间隔着网络往返；并发下载同一文件名时会得到同一个 .part，
    ///    第二个独占打开的 FileStream 将抛 IOException。
    ///    FileMode.CreateNew 独占创建可保证文件名只被首个任务占用。
    /// </summary>
    private static string ReserveUniquePath(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        if (TryReserve(path)) return path;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (TryReserve(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    private static bool TryReserve(string path)
    {
        try
        {
            using var slot = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return true;
        }
        catch (IOException) { return false; }          // 文件已存在或被占用
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>删除仍为空的占位文件（下载失败或文件名变更时调用）；有内容的文件不做处理。</summary>
    private static void ReleaseIfEmpty(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 0) return;
            File.Delete(path);
        }
        catch { /* 删不掉就留个空文件，不影响功能 */ }
    }

    /// <summary>删除未完成的 .part 文件（下载失败重试时调用）。</summary>
    private static void TryDelete(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
        }
        catch { /* 删除失败时忽略 */ }
    }
}
