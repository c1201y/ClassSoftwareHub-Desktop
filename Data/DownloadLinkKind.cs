using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ClassSoftwareHub.Desktop.Data;


public enum DownloadKind
{
    
    File,

    
    Store,

    
    Netdisk,

    
    Page,
}












public static class DownloadLinkKind
{
    
    private static readonly Regex StorePattern = new(
        @"^https?://(?:apps\.microsoft\.com|(?:www\.)?microsoft\.com/store|store\.microsoft\.com)/",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    
    private const string StoreScheme = "ms-windows-store:";

    
    
    
    private static readonly Regex NetdiskPattern = new(
        @"^https?://(?:[\w-]+\.)*(?:pan\.baidu\.com|pan\.quark\.cn|123pan\.com|123684\.com|123865\.com|123912\.com|lanzou[a-z]?\.com|lanzoui\.com|lanzoux\.com|aliyundrive\.com|alipan\.com|cloud\.189\.cn|weiyun\.com|cowtransfer\.com|drive\.uc\.cn|115\.com)/",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    
    
    
    
    
    
    
    
    
    
    private static readonly Regex FileExtPattern = new(
        @"\.(?:exe|msi|msix|msixbundle|appx|appxbundle|appinstaller|zip|7z|rar|tar|tar\.gz|tgz|tar\.xz|txz|tar\.bz2|tbz2|gz|bz2|xz|zst|dmg|pkg|apk|aab|deb|rpm|iso|img|cab|bin|jar|crx|appimage|flatpak|snap|nupkg)(?:[?#]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    
    private static readonly HashSet<string> DeclaredKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "file", "store", "netdisk", "page",
    };

    
    
    
    
    
    
    public static DownloadKind Of(string? url, string? declared = null)
    {
        var href = (url ?? "").Trim();
        var claim = (declared ?? "").Trim();

        if (claim.Length > 0 && DeclaredKinds.Contains(claim)) return Parse(claim);

        
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
