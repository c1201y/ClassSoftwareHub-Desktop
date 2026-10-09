using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 「GitHub 下载体验优化」的三个取值（设置页「软件内容」）。存进 settings.json 的就是这三个字面量。
/// ⚠️ 这是**存档格式**的一部分：只能加新值，不能改已有字面量。
/// </summary>
public static class GithubRoutes
{
    /// <summary>自动：下载前把自建节点、各条公益镜像、GitHub 源都探一下，按快慢排。</summary>
    public const string Auto = "auto";

    /// <summary>自建加速服务：固定优先社区自建节点（向节点换限时签名链接），取不到签名退公益镜像。</summary>
    public const string SelfHosted = "selfhosted";

    /// <summary>GitHub 源：原样直连 github.com，不做任何改写。</summary>
    public const string Official = "github";

    /// <summary>把存档里的字符串收敛到三个合法值（认不出的一律当 GitHub 源 —— 官方直连永远最保底）。</summary>
    public static string Normalize(string? value) => value switch
    {
        SelfHosted => SelfHosted,
        Auto => Auto,
        _ => Official,
    };
}

/// <summary>
/// 一条候选下载链接，外加「抓它时必须一起发的头」。
///
/// <see cref="Referer"/> 与 <see cref="ApiKey"/> 都是**自建加速节点**的硬要求：
/// 节点按 Referer 白名单放行（原生客户端默认不发该头），签名若绑定了密钥还要 <c>X-Api-Key</c>。
/// 二者跟链接绑在一起走，才不会出现「探速带了、真下载忘了带」这类漏发。
/// </summary>
public sealed record DownloadCandidate(string Url, string? Referer, string? ApiKey = null)
{
    public static DownloadCandidate Direct(string url) => new(url, null);

    /// <summary>
    /// 把「抓它时必须带的头」加到请求上。探速与真下载**共用这一个方法**，
    /// 免得以后又出现「探速带了 Referer、真下载忘了带」这种把好路误判成不可用的漏发。
    /// </summary>
    public void ApplyTo(System.Net.Http.HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(Referer))
            request.Headers.Referrer = new Uri(Referer);
        if (!string.IsNullOrEmpty(ApiKey))
            request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);
    }

    /// <summary>⚠️ 只打印链接：日志里绝不能带密钥。</summary>
    public override string ToString() => Url;
}

/// <summary>
/// 下载前把「原始链接」翻译成「候选链接列表」—— 用户级的**全局默认下载路径**。
///
/// 覆盖范围：**软件下载**（<see cref="DownloadService"/>）与**更新包下载**
/// （<c>UpdateService.DownloadAndVerifyAsync</c>）两条链路都走这里，用户不必分别在两处设一次。
///
/// 三个模式（2026-10-09 起，对齐 gh-stream 接口说明）：
///   · GitHub 源  —— 原样直连，一条候选，不探测不改写；
///   · 自建加速服务 —— 先向自建节点换一条**限时签名链接**（<see cref="MirrorSign"/>，抓取时必须带 Referer），
///                     固定首选；取不到签名就往后退到公益镜像（通道清单原序）→ GitHub 源；
///   · 自动       —— 自建节点 / 各条公益镜像 / GitHub 源**并行测速**，按快慢排候选，结果缓存。
///
/// ⚠️ 自建节点**不支持前缀拼接**（<c>节点/&lt;原始链接&gt;</c> 对任何 Referer 都 403），
///    只能用它自己签发的 <c>/d?u=…&amp;e=…&amp;s=…</c> 链接 —— 所以候选必须能携带 Referer（和可能的 X-Api-Key）。
/// ⚠️ 换签名**直连节点**（<c>download.classsoftwarehub.cn/sign</c>），不再经站点后端中转：
///    实测本机网络下节点通、而站点后端域名不通，走中转等于白等。
///
/// 和详情页那条「加速通道」的分工：
///   · 详情页 = 逐条**手动**挑公益镜像（<see cref="Core.GithubMirror.Channels"/>），一次一条链接；
///   · 这里   = 用户在设置里定**一次**，之后所有 GitHub 下载都按它走。
///   两者不冲突：手动挑过之后链接的域名已经不是 github.com 了，<see cref="Core.GithubMirror.IsMirrorableUrl"/>
///   认不出来，所以不会再被改写一遍（前缀拼接天然幂等）。
///
///    更新包走加速也不影响安全性：<c>UpdateService</c> 下载完照样按 MD5 / SHA256 校验，改包会被当场拦下。
/// </summary>
public static class GithubRoute
{
    /// <summary>候选池里「自建加速节点」这条的标识（缓存顺序时用它，不落盘）。</summary>
    private const string AcceleratorId = "acc";

    /// <summary>候选池里 GitHub 官方直连这条的标识。</summary>
    private const string OfficialId = "github";

    /// <summary>探测窗口：到点就断开（不等它下完），拿这段时间的平均速度当排名依据。</summary>
    private static readonly TimeSpan ProbeWindow = TimeSpan.FromSeconds(2.5);

    /// <summary>探测硬超时（比窗口宽一点，兜住"连上了但一个字节都不给"的情况）。</summary>
    private static readonly TimeSpan ProbeHardTimeout = TimeSpan.FromSeconds(8);

    /// <summary>探测读到的字节数低于此值 → 这条基本不能用。</summary>
    private const int ProbeMinBytes = 64 * 1024;

    /// <summary>探测最多读这么多（Range 上界，别为了测速真把大包拖下来）。</summary>
    private const int ProbeMaxBytes = 512 * 1024;

    /// <summary>
    /// 自动模式这次排出来的顺序能信多久 —— 网络会变，过期重探。
    /// 取 8 分钟是因为候选里可能含签名链接（节点侧 15 分钟过期），要留足余量。
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(8);

    private static readonly HttpClient ProbeHttp = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,     // 靠下面的 CancellationToken 控制
    };

    private static readonly object Gate = new();
    private static string[]? _autoOrder;
    private static DateTime _autoAt;

    /// <summary>当前设置（认不出的值当 GitHub 源）。</summary>
    public static string Current => GithubRoutes.Normalize(App.Settings.Current.GithubDownloadRoute);

    /// <summary>三条路径的中文名，界面上显示/写日志都用它。</summary>
    public static string DisplayName(string route) => GithubRoutes.Normalize(route) switch
    {
        GithubRoutes.SelfHosted => "自建加速服务",
        GithubRoutes.Official => "GitHub 源",
        _ => "自动",
    };

    /// <summary>
    /// 原始链接 → **候选链接**（按优先级排：前一条失败了就试下一条）。
    /// 非 GitHub 文件链接一律原样返回一条 —— 绝不改写不该动的链接。
    /// </summary>
    public static async Task<IReadOnlyList<DownloadCandidate>> ResolveAsync(string url, CancellationToken ct = default)
    {
        // 只有「GitHub 上的文件地址」才谈得上换路（官网直链、网盘、商店页一律不动）
        if (!Core.GithubMirror.IsMirrorableUrl(url))
            return new[] { DownloadCandidate.Direct(url) };

        var official = DownloadCandidate.Direct(url);
        if (Current == GithubRoutes.Official)
            return new[] { official };

        return Current == GithubRoutes.SelfHosted
            ? await SelfHostedAsync(url, official, ct).ConfigureAwait(false)
            : await AutoAsync(url, official, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 自建加速服务：固定首选自建节点；取不到签名 → 公益镜像（通道清单原序）→ GitHub 源。
    /// 这里**只换签名、不做测速** —— 用户明确选了自建节点，没必要每次都先花 2.5 秒探一遍公益镜像。
    /// </summary>
    private static async Task<IReadOnlyList<DownloadCandidate>> SelfHostedAsync(
        string url, DownloadCandidate official, CancellationToken ct)
    {
        var list = new List<DownloadCandidate>();

        var signed = await MirrorSign.GetSignedUrlAsync(url, ct).ConfigureAwait(false);
        if (signed is not null)
            list.Add(MirrorSign.Candidate(signed));
        else
            Core.AppLog.Info("download-route", "自建加速服务：未取得签名，改用公益镜像");

        foreach (var channel in Core.GithubMirror.Channels)
            list.Add(DownloadCandidate.Direct(Core.GithubMirror.MirrorUrl(url, channel)));

        list.Add(official);                                 // 兜底：谁都不行还有官方直连
        return list;
    }

    /// <summary>
    /// 自动：把自建节点、各条公益镜像、GitHub 源一起探速，按快慢排候选；结果缓存 <see cref="CacheTtl"/>。
    /// 缓存里存的是**顺序标识**而不是链接本身 —— 签名链接是逐条目标签发的，直接缓存链接会串。
    /// </summary>
    private static async Task<IReadOnlyList<DownloadCandidate>> AutoAsync(
        string url, DownloadCandidate official, CancellationToken ct)
    {
        var cached = CachedOrder();
        if (cached is not null)
            return await FromOrderAsync(cached, url, official, ct).ConfigureAwait(false);

        // 冷启动：先铺好公益镜像与官方（探速立刻发起），再并行等签名，别让签名请求白等 2.5 秒
        var items = new List<Item>();
        foreach (var channel in Core.GithubMirror.Channels)
            items.Add(new Item(channel.Id, DownloadCandidate.Direct(Core.GithubMirror.MirrorUrl(url, channel))));
        items.Add(new Item(OfficialId, official));

        var probes = items.ToDictionary(item => item.Id, item => ProbeAsync(item.Candidate, ct));

        var signed = await MirrorSign.GetSignedUrlAsync(url, ct).ConfigureAwait(false);
        if (signed is not null)
        {
            var accelerator = MirrorSign.Candidate(signed);
            items.Insert(0, new Item(AcceleratorId, accelerator));   // 同速时自建节点优先
            probes[AcceleratorId] = ProbeAsync(accelerator, ct);
        }

        await Task.WhenAll(probes.Values).ConfigureAwait(false);

        var ranked = items
            .OrderByDescending(item => probes[item.Id].Result.Usable)
            .ThenByDescending(item => probes[item.Id].Result.BytesPerSecond)
            .ToList();

        Remember(ranked.Select(item => item.Id).ToArray());

        Core.AppLog.Info("download-route",
            "自动选路 → " + string.Join(" > ", ranked.Select(item => $"{Label(item.Id)} {Describe(probes[item.Id].Result)}")));

        return ranked.Select(item => item.Candidate).ToList();
    }

    /// <summary>命中缓存：按记下来的顺序重建候选（签名链接现换，其余是确定性的拼接）。</summary>
    private static async Task<IReadOnlyList<DownloadCandidate>> FromOrderAsync(
        string[] order, string url, DownloadCandidate official, CancellationToken ct)
    {
        // 上次排出来有自建节点才需要再换一次签名；否则连这次请求都省了
        var signed = order.Contains(AcceleratorId)
            ? await MirrorSign.GetSignedUrlAsync(url, ct).ConfigureAwait(false)
            : null;

        var list = new List<DownloadCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string id)
        {
            if (!seen.Add(id)) return;
            var candidate = Build(id, url, official, signed);
            if (candidate is not null) list.Add(candidate);
        }

        foreach (var id in order) Add(id);
        Add(OfficialId);                                     // 补齐缓存里没有的（比如清单新增了通道）
        foreach (var channel in Core.GithubMirror.Channels) Add(channel.Id);

        return list;
    }

    /// <summary>把一个顺序标识还原成候选链接；还原不出来（通道已下线 / 签名没取到）返回 null。</summary>
    private static DownloadCandidate? Build(string id, string url, DownloadCandidate official, string? signed)
    {
        if (id == AcceleratorId)
            return signed is null ? null : MirrorSign.Candidate(signed);
        if (id == OfficialId)
            return official;

        var channel = Core.GithubMirror.Channels.FirstOrDefault(item => item.Id == id);
        return channel is null ? null : DownloadCandidate.Direct(Core.GithubMirror.MirrorUrl(url, channel));
    }

    private sealed record Item(string Id, DownloadCandidate Candidate);

    private static string Label(string id) => id switch
    {
        AcceleratorId => "自建加速",
        OfficialId => "GitHub 源",
        _ => Core.GithubMirror.Channels.FirstOrDefault(item => item.Id == id)?.Name ?? id,
    };

    private sealed record ProbeResult(bool Ok, long Bytes, double BytesPerSecond, string? Error)
    {
        /// <summary>探到手了、而且读到的量够说明问题。</summary>
        public bool Usable => Ok && Bytes >= ProbeMinBytes;
    }

    /// <summary>
    /// 只读一小段（Range）+ 只读一小会儿，据平均速度排名。失败不抛异常，包在结果里。
    /// ⚠️ 候选该带的头（Referer / X-Api-Key）必须一起带上 —— 自建节点漏了它直接 403，
    ///    会把好路误判成不可用。
    /// </summary>
    private static async Task<ProbeResult> ProbeAsync(DownloadCandidate candidate, CancellationToken outer)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outer);
        linked.CancelAfter(ProbeHardTimeout);
        var ct = linked.Token;

        var watch = Stopwatch.StartNew();
        long received = 0;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, candidate.Url);
            request.Headers.Range = new RangeHeaderValue(0, ProbeMaxBytes - 1);
            candidate.ApplyTo(request);

            using var response = await ProbeHttp
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return new ProbeResult(false, 0, 0, $"HTTP {(int)response.StatusCode}");

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[32 * 1024];
            while (received < ProbeMaxBytes && watch.Elapsed < ProbeWindow)
            {
                var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read <= 0) break;
                received += read;
            }
            watch.Stop();
            return new ProbeResult(true, received, Speed(received, watch.Elapsed), null);
        }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested)
        {
            // 只是探测超时（用户没取消）：已经读到的部分照样算数，够 64 KB 就还能用
            watch.Stop();
            return new ProbeResult(received > 0, received, Speed(received, watch.Elapsed), "探测超时");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeResult(false, 0, 0, ex.Message);
        }
    }

    /// <summary>平均速度（B/s）。分母兜个底，别让"连上就到"除出一个天文数字。</summary>
    private static double Speed(long bytes, TimeSpan elapsed) =>
        bytes <= 0 ? 0 : bytes / Math.Max(elapsed.TotalSeconds, 0.05);

    private static string Describe(ProbeResult r) => r switch
    {
        { Ok: false } => $"不可用（{r.Error}）",
        { Bytes: 0 } => "无数据",
        _ => $"{r.BytesPerSecond / 1024:0} KB/s（{r.Bytes / 1024} KB）",
    };

    private static string[]? CachedOrder()
    {
        lock (Gate)
        {
            if (_autoOrder is null) return null;
            if (DateTime.UtcNow - _autoAt > CacheTtl)
            {
                _autoOrder = null;
                return null;
            }
            return _autoOrder;
        }
    }

    private static void Remember(string[] order)
    {
        lock (Gate)
        {
            _autoOrder = order;
            _autoAt = DateTime.UtcNow;
        }
    }

    /// <summary>设置里刚改了路径 → 把自动模式的缓存清掉（否则最多要等 8 分钟才认新设置）。</summary>
    public static void InvalidateCache()
    {
        lock (Gate)
        {
            _autoOrder = null;
        }
    }
}
