using System;
using System.Text.RegularExpressions;

namespace ClassSoftwareHub.Desktop.Core;






public static class StoreLink
{
    private static readonly Regex AppsDetail = new(
        @"apps\.microsoft\.com/(?:[A-Za-z-]+/)?detail/([A-Za-z0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LegacyStore = new(
        @"(?:www\.)?microsoft\.com/store/(?:apps|detail|pdp)/([A-Za-z0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    
    
    
    public static string? ToStoreUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var text = url.Trim();

        if (text.StartsWith("ms-windows-store:", StringComparison.OrdinalIgnoreCase))
            return text;

        var match = AppsDetail.Match(text);
        if (!match.Success) match = LegacyStore.Match(text);
        if (!match.Success) return null;

        var id = match.Groups[1].Value;
        return id.Length == 0 ? null : "ms-windows-store://pdp/?productid=" + id;
    }
}
