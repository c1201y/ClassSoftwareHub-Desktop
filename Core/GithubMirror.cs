using System;
using System.Text.RegularExpressions;

namespace ClassSoftwareHub.Desktop.Core;


public sealed record MirrorChannel(string Id, string Name, string Prefix);






public static class GithubMirror
{
    
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

    
    
}




















public static class GithubIpRelay
{
    
    public const string Id = "iprelay";

    public const string Name = "IP 直连反代";

    public const string Base = "https://209.33.174.187:8426";

    
    
    
    
    public static bool CanRelay(string? url)
    {
        if (!GithubMirror.IsMirrorableUrl(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;

        return string.Equals(u.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(u.Host, "www.github.com", StringComparison.OrdinalIgnoreCase);
    }

    
    public static string Relay(string url) => Base + new Uri(url).PathAndQuery;
}
