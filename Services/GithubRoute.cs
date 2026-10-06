using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// <summary>自动：下载前各探一下两边速度，挑快的那条。</summary>
    public const string Auto = "auto";

    /// <summary>自建加速服务（实验性）：GitHub 链接一律交由社区自建节点中转（节点就绪前等价于 GitHub 源）。</summary>
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
/// 下载前把「原始链接」翻译成「实际要抓的链接」—— 用户级的**全局默认下载路径**。
///
/// 覆盖范围：**软件下载**（<see cref="DownloadService"/>）与**更新包下载**
/// （<c>UpdateService.DownloadAndVerifyAsync</c>）两条链路都走这里，用户不必分别在两处设一次。
///
/// 和详情页那条「加速通道」的分工：
///   · 详情页 = 逐条**手动**挑公益镜像（<see cref="Core.GithubMirror.Channels"/>），一次一条链接；
///   · 这里   = 用户在设置里定**一次**，之后所有 GitHub 下载都按它走。
///   两者不冲突：手动挑过之后链接的域名已经不是 github.com 了，<see cref="Core.GithubMirror.IsMirrorableUrl"/>
///   认不出来，所以不会再被改写一遍（前缀拼接天然幂等）。
///
/// ⚠️ 自建加速服务（站点鸣谢里那位「凭舟吟」提供的）**远端还在架设中** ⇒ <see cref="AcceleratorReady"/> 现在是 false：
///    三个模式**一律走 GitHub 源**，既不拼接、也不探测；设置页选中「自建加速服务」时显示一条红色警告。
///    拼接方式（就绪后）跟公益镜像完全一样：`&lt;前缀&gt;&lt;完整原始链接&gt;`；服务端自带落盘缓存 ——
///    首次回源慢、之后命中缓存快得多（响应头 <c>X-Cache: MISS/HIT</c>）。
///    更新包走加速也不影响安全性：<c>UpdateService</c> 下载完照样按 MD5 / SHA256 校验，改包会被当场拦下。
/// </summary>
public static class GithubRoute
{
    /// <summary>
    /// 自建加速服务**是否已经架设好**。⚠️ 远端节点未就绪 ⇒ 现在恒为 false：
    /// 选「自建加速服务」等价于 GitHub 源，设置页显示红色警告「当前加速服务不可用」。
    /// 🔑 远端就绪后：把下面的 <see cref="AcceleratorPrefix"/> 填回真实地址，再把这里改成 true —— 只改这两处。
    /// </summary>
    public const bool AcceleratorReady = false;

    /// <summary>
    /// 自建加速服务的链接前缀，形如 <c>http://&lt;主机&gt;:&lt;端口&gt;/</c>（**末尾必须带 /**）。
    /// ⚠️ 服务由社区自建、远端正在架设中，地址尚未固定 ⇒ 这里**不写死地址**，就绪后再填。
    /// </summary>
    public const string AcceleratorPrefix = "";

    /// <summary>探测窗口：到点就断开（不等它下完），拿这段时间的平均速度当排名依据。</summary>
    private static readonly TimeSpan ProbeWindow = TimeSpan.FromSeconds(2.5);

    /// <summary>探测硬超时（比窗口宽一点，兜住"连上了但一个字节都不给"的情况）。</summary>
    private static readonly TimeSpan ProbeHardTimeout = TimeSpan.FromSeconds(8);

    /// <summary>探测读到的字节数低于此值 → 这条基本不能用。</summary>
    private const int ProbeMinBytes = 64 * 1024;

    /// <summary>探测最多读这么多（Range 上界，别为了测速真把大包拖下来）。</summary>
    private const int ProbeMaxBytes = 512 * 1024;

    /// <summary>自动模式这次挑的结果能信多久 —— 网络会变，过期重探。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private static readonly HttpClient ProbeHttp = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,     // 靠下面的 CancellationToken 控制
    };

    private static readonly object Gate = new();
    private static string? _autoWinner;
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
    /// 非 GitHub 文件链接、以及已经是加速链接的，原样返回一条 —— 绝不改写不该动的链接。
    /// </summary>
    public static async Task<IReadOnlyList<string>> ResolveAsync(string url, CancellationToken ct = default)
    {
        // 只有「GitHub 上的文件地址」才谈得上换路（官网直链、网盘、商店页一律不动）
        if (!Core.GithubMirror.IsMirrorableUrl(url))
            return new[] { url };

        // 加速节点还没就绪（远端在架设中）：一律走 GitHub 源 —— 不拼接、不探测、不做任何改写。
        if (!AcceleratorReady || AcceleratorPrefix.Length == 0)
            return new[] { url };

        var accelerated = AcceleratorPrefix + url;

        switch (Current)
        {
            case GithubRoutes.Official:
                return new[] { url };

            case GithubRoutes.SelfHosted:
                // 自建加速失败也回落到官方直链：实验性节点不该把下载堵死
                Core.AppLog.Info("download-route", $"自建加速服务：{accelerated}");
                return new[] { accelerated, url };

            default:
                return await AutoAsync(url, accelerated, ct).ConfigureAwait(false);
        }
    }

    /// <summary>自动模式：探速 → 记住结果（10 分钟）→ 给出「首选 + 备选」两条。</summary>
    private static async Task<IReadOnlyList<string>> AutoAsync(string official, string accelerated, CancellationToken ct)
    {
        var cached = CachedWinner();
        if (cached is not null)
            return Order(cached, official, accelerated);

        // 两条同时探，互不等待
        var officialProbe = ProbeAsync(official, ct);
        var acceleratedProbe = ProbeAsync(accelerated, ct);
        var both = await Task.WhenAll(officialProbe, acceleratedProbe).ConfigureAwait(false);

        var preferAccelerated = PreferAccelerated(both[0], both[1]);
        Remember(preferAccelerated ? GithubRoutes.SelfHosted : GithubRoutes.Official);

        Core.AppLog.Info("download-route",
            $"自动选路 → {(preferAccelerated ? "自建加速服务" : "GitHub 源")}；" +
            $"GitHub 源 {Describe(both[0])}，自建加速 {Describe(both[1])}");

        return Order(preferAccelerated ? GithubRoutes.SelfHosted : GithubRoutes.Official, official, accelerated);
    }

    /// <summary>按选中的那条排候选：首选在前，另一条兜底。</summary>
    private static IReadOnlyList<string> Order(string preferred, string official, string accelerated) =>
        preferred == GithubRoutes.SelfHosted
            ? new[] { accelerated, official }
            : new[] { official, accelerated };

    /// <summary>
    /// 谁更快。都不行时一律回到 GitHub 源（官方直链永远是保底，别把希望押在实验性节点上）。
    /// 快不过 10% 也算平手 —— 免得两条速度接近时来回横跳。
    /// </summary>
    private static bool PreferAccelerated(ProbeResult official, ProbeResult accelerated)
    {
        if (!accelerated.Usable) return false;
        if (!official.Usable) return true;
        return accelerated.BytesPerSecond > official.BytesPerSecond * 1.1;
    }

    private sealed record ProbeResult(bool Ok, long Bytes, double BytesPerSecond, string? Error)
    {
        /// <summary>探到手了、而且读到的量够说明问题。</summary>
        public bool Usable => Ok && Bytes >= ProbeMinBytes;
    }

    /// <summary>只读一小段（Range）+ 只读一小会儿，据平均速度排名。失败不抛异常，包在结果里。</summary>
    private static async Task<ProbeResult> ProbeAsync(string url, CancellationToken outer)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outer);
        linked.CancelAfter(ProbeHardTimeout);
        var ct = linked.Token;

        var watch = Stopwatch.StartNew();
        long received = 0;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, ProbeMaxBytes - 1);

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

    private static string? CachedWinner()
    {
        lock (Gate)
        {
            if (_autoWinner is null) return null;
            if (DateTime.UtcNow - _autoAt > CacheTtl)
            {
                _autoWinner = null;
                return null;
            }
            return _autoWinner;
        }
    }

    private static void Remember(string route)
    {
        lock (Gate)
        {
            _autoWinner = route;
            _autoAt = DateTime.UtcNow;
        }
    }

    /// <summary>设置里刚改了路径 → 把自动模式的缓存清掉（否则最多要等 10 分钟才认新设置）。</summary>
    public static void InvalidateCache()
    {
        lock (Gate)
        {
            _autoWinner = null;
        }
    }
}
