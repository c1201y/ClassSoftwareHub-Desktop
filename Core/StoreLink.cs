using System;
using System.Text.RegularExpressions;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// Microsoft Store 链接转换：站点数据里的商店链接通常是网页地址
/// （https://apps.microsoft.com/detail/9wzdncrfjbmp?hl=zh-CN&amp;gl=CN），
/// 直接丢给系统只会开浏览器 —— 要转成商店协议 ms-windows-store:// 才会拉起「微软商店」应用。
/// </summary>
public static class StoreLink
{
    private static readonly Regex AppsDetail = new(
        @"apps\.microsoft\.com/(?:[A-Za-z-]+/)?detail/([A-Za-z0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LegacyStore = new(
        @"(?:www\.)?microsoft\.com/store/(?:apps|detail|pdp)/([A-Za-z0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 能认出来就返回商店协议的 URI（ms-windows-store://pdp/?productid=xxx），认不出来返回 null。
    /// </summary>
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
