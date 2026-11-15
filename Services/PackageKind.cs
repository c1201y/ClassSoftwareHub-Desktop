using System;
using System.Collections.Generic;
using System.IO;

namespace ClassSoftwareHub.Desktop.Services;


public enum PackageAction
{
    
    None,

    
    Install,

    
    Extract,
}














public static class PackageKind
{
    
    
    
    
    
    private static readonly HashSet<string> Installers = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".appinstaller", ".msu",
    };

    
    
    
    
    
    
    
    
    
    private static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar",
        ".gz", ".tgz", ".xz", ".txz", ".bz2", ".tbz2", ".tbz",
        ".zst", ".tzst", ".lzma", ".lz",
    };

    
    public static PackageAction Of(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return PackageAction.None;

        var ext = Path.GetExtension(path);
        if (ext.Length < 2) return PackageAction.None;

        if (Installers.Contains(ext)) return PackageAction.Install;
        if (Archives.Contains(ext)) return PackageAction.Extract;
        return PackageAction.None;
    }

    public static bool IsInstaller(string? path) => Of(path) == PackageAction.Install;

    public static bool IsArchive(string? path) => Of(path) == PackageAction.Extract;

    
    public static string ActionText(PackageAction action) => action switch
    {
        PackageAction.Install => "安装",
        PackageAction.Extract => "解压",
        _ => "",
    };
}
