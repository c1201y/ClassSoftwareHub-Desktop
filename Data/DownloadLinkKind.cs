using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ClassSoftwareHub.Desktop.Data;

/// <summary>一条下载项的落地方式。</summary>
public enum DownloadKind
{
    /// <summary>文件直链：本程序直接下载。</summary>
    File,

    /// <summary>Microsoft Store 应用页：由「商店卡」处理，不进下载列表。</summary>
    Store,

    /// <summary>第三方网盘分享页：只能跳转（要密码 / 登录 / 客户端，物理上做不到直下）。</summary>
    Netdisk,

    /// <summary>官网下载页：只能跳转（链接由 JS 生成或带签名，程序取不到）。</summary>
    Page,
}

/// <summary>
/// 下载链接分类 —— 与站点 <c>src/gallery/downloadLink.ts</c> **逐条对齐**，改一处必须改两处。
///
/// 为什么需要它：站点 220 条下载项里，183 条是文件直链，另有 22 条官网下载页、5 条网盘
/// 分享页、10 条商店页。桌面端过去对这些一律写「下载」，用户按下去才发现被带进了浏览器
/// —— 按钮文案与行为都必须跟着类型走。
///
/// 判定顺序是「数据里显式声明 → 再按域名 / 后缀推断」，不是反过来：Geogebra 的包地址
/// 不带后缀（<c>/package/win-autoupdate</c>）、UU 远程是 <c>/api/v1/release/dl/1</c>，
/// 光看 URL 推不出来，必须在 JSON 里写 <c>"kind": "file"</c>。
/// </summary>
public static class DownloadLinkKind
{
    /// <summary>Microsoft Store 应用页。与站点 STORE_RE 逐字一致。</summary>
    private static readonly Regex StorePattern = new(
        @"^https?://(?:apps\.microsoft\.com|(?:www\.)?microsoft\.com/store|store\.microsoft\.com)/",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>商店协议链接（站点在 kindOf 里单独判，这里同样单列）。</summary>
    private const string StoreScheme = "ms-windows-store:";

    /// <summary>
    /// 网盘分享页 —— 一律是「跳转」而不是「下载」。与站点 NETDISK_RE 逐字一致。
    /// </summary>
    private static readonly Regex NetdiskPattern = new(
        @"^https?://(?:[\w-]+\.)*(?:pan\.baidu\.com|pan\.quark\.cn|123pan\.com|123684\.com|123865\.com|123912\.com|lanzou[a-z]?\.com|lanzoui\.com|lanzoux\.com|aliyundrive\.com|alipan\.com|cloud\.189\.cn|weiyun\.com|cowtransfer\.com|drive\.uc\.cn|115\.com)/",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 「确定是安装包 / 压缩包」的后缀 —— 与站点 FILE_EXT_RE 逐字一致，扩充必须两边同时改。
    ///
    /// ⚠️ 是**对整条 URL 匹配**，不是只看路径：希沃那批签名直链的路径是不带后缀的
    /// `/seewo-report_xxx`，文件名只出现在 query 的 <c>response-content-disposition</c> 里，
    /// 只看路径会漏判成「官网页面」。站点用的就是这个口径，桌面端必须跟齐。
    ///
    /// 表里只放「不会返回网页」的后缀。两边的代价不对称：漏判只是「明明是文件却要跳转」，
    /// 误判是**把网页当文件存下来**，所以 .php / .html / .asp 这类一律不收。
    /// </summary>
    private static readonly Regex FileExtPattern = new(
        @"\.(?:exe|msi|msix|msixbundle|appx|appxbundle|appinstaller|zip|7z|rar|tar|tar\.gz|tgz|tar\.xz|txz|tar\.bz2|tbz2|gz|bz2|xz|zst|dmg|pkg|apk|aab|deb|rpm|iso|img|cab|bin|jar|crx|appimage|flatpak|snap|nupkg)(?:[?#]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>JSON 里 kind 字段的合法取值（与站点 DECLARED 一致）。</summary>
    private static readonly HashSet<string> DeclaredKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "file", "store", "netdisk", "page",
    };

    /// <summary>
    /// 一条下载项该怎么落地。
    ///
    /// <paramref name="declared"/> 是数据里显式写的 kind：取值合法就**优先采信**，
    /// 因为只有维护者知道那个不带后缀的地址到底是不是文件。
    /// </summary>
    public static DownloadKind Of(string? url, string? declared = null)
    {
        var href = (url ?? "").Trim();
        var claim = (declared ?? "").Trim();

        if (claim.Length > 0 && DeclaredKinds.Contains(claim)) return Parse(claim);

        // 没有 url 的项（维护者删剩的壳）当作网页处理，跳转时也不会出事
        if (href.Length == 0) return DownloadKind.Page;

        if (StorePattern.IsMatch(href) || href.StartsWith(StoreScheme, StringComparison.OrdinalIgnoreCase))
            return DownloadKind.Store;
        if (NetdiskPattern.IsMatch(href)) return DownloadKind.Netdisk;
        if (FileExtPattern.IsMatch(href)) return DownloadKind.File;
        return DownloadKind.Page;
    }

    private static DownloadKind Parse(string declared) => declared.ToLowerInvariant() switch
    {
        "store" => DownloadKind.Store,
        "netdisk" => DownloadKind.Netdisk,
        "file" => DownloadKind.File,
        _ => DownloadKind.Page,
    };
}
