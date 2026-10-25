using System;
using System.Text.RegularExpressions;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>一条加速通道（与站点 src/gallery/githubMirror.ts 同源）。</summary>
public sealed record MirrorChannel(string Id, string Name, string Prefix);

/// <summary>
/// GitHub 下载链接的「国内加速」通道。
/// 原理：把完整原始链接接在镜像域名后面（ghfast.top/https://github.com/...）。
/// ⚠️ 第三方公益镜像，只做链接拼接，不中转不缓存；清单与站点保持一致。
/// </summary>
public static class GithubMirror
{
    /// <summary>按推荐顺序排，第一个是默认值。</summary>
    public static readonly MirrorChannel[] Channels =
    {
        new("ghfast", "ghfast.top", "https://ghfast.top/"),
        new("ghproxy", "gh-proxy.com", "https://gh-proxy.com/"),
        new("ghproxy-net", "ghproxy.net", "https://ghproxy.net/"),
        new("ddlc", "gh.ddlc.top", "https://gh.ddlc.top/"),
    };

    public static string MirrorUrl(string url, MirrorChannel channel) => channel.Prefix + url;

    private static readonly Regex GithubPath = new(
        @"^/[^/]+/[^/]+/(releases/download|archive)/", RegexOptions.Compiled);

    /// <summary>只有「GitHub 上的文件地址」才给加速入口（官网直链、仓库主页不给）。</summary>
    public static bool IsMirrorableUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;

        var host = u.Host.ToLowerInvariant();
        if (host is "github.com" or "www.github.com")
            return GithubPath.IsMatch(u.AbsolutePath);

        return host is "objects.githubusercontent.com" or "github-releases.githubusercontent.com"
            or "codeload.github.com" or "raw.githubusercontent.com";
    }

    // 「这是不是 Microsoft Store 应用页」原先在这里另有一份 StoreUrl 正则；分类判定已统一到
    // Data/DownloadLinkKind.cs（与站点 downloadLink.ts 同源），这份平行判据已删除。
}

/// <summary>
/// IP 直连反代（2026-10-25 由凭舟吟提供并实测接入）。
/// <para>
/// 存在的理由：上面那套公益镜像全是**域名**，域名一旦被线路按 SNI 拦掉就全部失效
/// ——福建这边三条 API 域名（分属三家服务商）同时在 TLS 阶段被 RST，公益镜像同理。
/// 这条走的是 **IP + 非标准端口**，不放行任何 GitHub 域名，所以那条拦截路径够不着它。
/// </para>
/// <para>
/// ⛔ 拼法与公益镜像**不同**，别混用：公益镜像是「前缀 + 完整 URL」
/// （<c>https://ghfast.top/https://github.com/…</c>），这条是**只换 host、路径原样**
/// （<c>…:8426/owner/repo/releases/download/…</c>）。实测前缀式返回 404。
/// </para>
/// <para>
/// 证书：Let's Encrypt 签发，SAN 里直接写着 <c>IP Address:209.33.174.187</c>，
/// 所以 <see cref="System.Net.Http.HttpClient"/> 的默认校验能过，**不需要关掉证书验证**（实测）。
/// 只代理 github.com 的路径；raw / codeload / api 路径会被它 302 到自己的 <c>/gh/</c> 下再取，
/// 但直接拼 raw、codeload 的 host 一律 404（实测），故 <see cref="CanRelay"/> 只认 github.com。
/// </para>
/// </summary>
public static class GithubIpRelay
{
    /// <summary>候选顺序里的标识（会写进自动模式的缓存，改动会导致老缓存里这条失效一次）。</summary>
    public const string Id = "iprelay";

    public const string Name = "IP 直连反代";

    public const string Base = "https://209.33.174.187:8426";

    /// <summary>
    /// 能不能走这条：必须是 <see cref="GithubMirror.IsMirrorableUrl"/> 认可的文件地址，
    /// 且 host 就是 github.com（其它几个 host 实测 404，硬拼过去只会白等一次超时）。
    /// </summary>
    public static bool CanRelay(string? url)
    {
        if (!GithubMirror.IsMirrorableUrl(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;

        return string.Equals(u.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(u.Host, "www.github.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>换掉 host，路径与查询串原样带过去。</summary>
    public static string Relay(string url) => Base + new Uri(url).PathAndQuery;
}
