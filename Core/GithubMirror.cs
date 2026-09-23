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

    private static readonly Regex StoreUrl = new(
        @"^https?://(apps\.microsoft\.com|www\.microsoft\.com/store|store\.microsoft\.com)|^ms-windows-store:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>是不是 Microsoft Store 的应用页（详情页要把这类链接单独提成商店卡）。</summary>
    public static bool IsStoreUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && StoreUrl.IsMatch(url);
}
