using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services;





public static class GithubRoutes
{
    
    public const string Auto = "auto";

    
    public const string SelfHosted = "selfhosted";

    
    public const string Official = "github";

    
    public static string Normalize(string? value) => value switch
    {
        SelfHosted => SelfHosted,
        Auto => Auto,
        _ => Official,
    };
}








public sealed record DownloadCandidate(string Url, string? Referer, string? ApiKey = null)


{
    public static DownloadCandidate Direct(string url) => new(url, null);

    
    
    
    
    public void ApplyTo(System.Net.Http.HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(Referer))
            request.Headers.Referrer = new Uri(Referer);
        if (!string.IsNullOrEmpty(ApiKey))
            request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);
    }

    
    public override string ToString() => Url;
}


























public static class GithubRoute
{
    
    private const string AcceleratorId = "acc";

    
    private const string OfficialId = "github";

    
    private static readonly TimeSpan ProbeWindow = TimeSpan.FromSeconds(1.5);

    
    private static readonly TimeSpan ProbeHardTimeout = TimeSpan.FromSeconds(2);

    
    private const int ProbeMinBytes = 64 * 1024;

    
    private const int ProbeMaxBytes = 512 * 1024;

    
    
    
    
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(8);

    private static readonly HttpClient ProbeHttp = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,     
    };

    private static readonly object Gate = new();
    private static string[]? _autoOrder;
    private static DateTime _autoAt;

    
    public static string Current => GithubRoutes.Normalize(App.Settings.Current.GithubDownloadRoute);

    
    public static string DisplayName(string route) => GithubRoutes.Normalize(route) switch
    {
        GithubRoutes.SelfHosted => "自建加速服务",
        GithubRoutes.Official => "GitHub 源",
        _ => "自动",
    };

    
    
    
    
    public static async Task<IReadOnlyList<DownloadCandidate>> ResolveAsync(string url, CancellationToken ct = default)
    {
        
        if (!Core.GithubMirror.IsMirrorableUrl(url))
        {
            Core.AppLog.Step("download-route", "非 GitHub 链接 → 原样直连（不改写）");
            return new[] { DownloadCandidate.Direct(url) };
        }

        var official = DownloadCandidate.Direct(url);
        if (Current == GithubRoutes.Official)
        {
            Core.AppLog.Step("download-route", "线路=GitHub 源 → 直连，不探测不改写");
            return new[] { official };
        }

        var list = Current == GithubRoutes.SelfHosted
            ? await SelfHostedAsync(url, official, ct).ConfigureAwait(false)
            : await AutoAsync(url, official, ct).ConfigureAwait(false);

        
        
        Core.AppLog.Step("download-route",
            $"线路={DisplayName(Current)} · 候选 {list.Count} 条 → "
            + string.Join(" > ", list.Select(c => HostOf(c.Url))));

        return list;
    }

    
    
    
    
    
    public static async Task<IReadOnlyList<string>> ResolveAcceleratedAsync(
        string url, CancellationToken ct = default)
    {
        var candidates = await ResolveAsync(url, ct).ConfigureAwait(false);
        return candidates.Select(c => c.Url).ToList();
    }

    private static string HostOf(string url)
    {
        try { return new Uri(url).Host; } catch { return url; }
    }

    
    
    
    
    private static async Task<IReadOnlyList<DownloadCandidate>> SelfHostedAsync(
        string url, DownloadCandidate official, CancellationToken ct)
    {
        var list = new List<DownloadCandidate>();

        var signed = await MirrorSign.GetSignedUrlAsync(url, ct).ConfigureAwait(false);

        
        
        
        
        
        if (signed is null)
        {
            Core.AppLog.Warn("download-route", "自建加速服务：未取得签名，按设置直接失败（不回退）");

            throw new InvalidOperationException(
                "自建加速服务未能取得下载签名，本次下载未开始。\n\n"
                + "若需继续下载，请在「设置 → 网络」中将下载路径改为「自动」，\n"
                + "程序将在各条可用线路之间测速并选择最优线路。\n\n"
                + "具体原因已记录于应用日志。");
        }

        
        return new[] { MirrorSign.Candidate(signed) };
    }


    
    
    
    
    private static async Task<IReadOnlyList<DownloadCandidate>> AutoAsync(
        string url, DownloadCandidate official, CancellationToken ct, bool includeOfficial = true)
    {
        
        var cached = includeOfficial ? CachedOrder() : null;
        if (cached is not null)
            return await FromOrderAsync(cached, url, official, ct).ConfigureAwait(false);

        
        var items = new List<Item>();
        foreach (var channel in Core.GithubMirror.Channels)
            items.Add(new Item(channel.Id, DownloadCandidate.Direct(Core.GithubMirror.MirrorUrl(url, channel))));

        
        
        if (Core.GithubIpRelay.CanRelay(url))
            items.Add(new Item(Core.GithubIpRelay.Id,
                DownloadCandidate.Direct(Core.GithubIpRelay.Relay(url))));

        
        if (includeOfficial) items.Add(new Item(OfficialId, official));

        var probes = items.ToDictionary(item => item.Id, item => ProbeAsync(item.Candidate, ct));

        var signed = await MirrorSign.GetSignedUrlAsync(url, ct).ConfigureAwait(false);
        if (signed is not null)
        {
            var accelerator = MirrorSign.Candidate(signed);
            items.Insert(0, new Item(AcceleratorId, accelerator));   
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

    
    private static async Task<IReadOnlyList<DownloadCandidate>> FromOrderAsync(
        string[] order, string url, DownloadCandidate official, CancellationToken ct)
    {
        
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
        Add(OfficialId);                                     
        foreach (var channel in Core.GithubMirror.Channels) Add(channel.Id);
        Add(Core.GithubIpRelay.Id);              

        return list;
    }

    
    private static DownloadCandidate? Build(string id, string url, DownloadCandidate official, string? signed)
    {
        if (id == AcceleratorId)
            return signed is null ? null : MirrorSign.Candidate(signed);
        if (id == OfficialId)
            return official;

        if (id == Core.GithubIpRelay.Id)
            return Core.GithubIpRelay.CanRelay(url)
                ? DownloadCandidate.Direct(Core.GithubIpRelay.Relay(url))
                : null;

        var channel = Core.GithubMirror.Channels.FirstOrDefault(item => item.Id == id);
        return channel is null ? null : DownloadCandidate.Direct(Core.GithubMirror.MirrorUrl(url, channel));
    }

    private sealed record Item(string Id, DownloadCandidate Candidate);

    private static string Label(string id) => id switch
    {
        AcceleratorId => "自建加速",
        OfficialId => "GitHub 源",
        Core.GithubIpRelay.Id => Core.GithubIpRelay.Name,
        _ => Core.GithubMirror.Channels.FirstOrDefault(item => item.Id == id)?.Name ?? id,
    };

    private sealed record ProbeResult(bool Ok, long Bytes, double BytesPerSecond, string? Error)
    {
        
        public bool Usable => Ok && Bytes >= ProbeMinBytes;
    }

    
    
    
    
    
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
            
            watch.Stop();
            return new ProbeResult(received > 0, received, Speed(received, watch.Elapsed), "探测超时");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeResult(false, 0, 0, ex.Message);
        }
    }

    
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

    
    public static void InvalidateCache()
    {
        lock (Gate)
        {
            _autoOrder = null;
        }
    }










    
    public const string AcceleratorHost = "download.classsoftwarehub.cn";

    
    public const string AcceleratorReferer = "https://classsoftwarehub.us.ci/";

    
    public static bool IsAcceleratedUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        return Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            && parsed.Host.Equals(AcceleratorHost, StringComparison.OrdinalIgnoreCase);
    }
}
