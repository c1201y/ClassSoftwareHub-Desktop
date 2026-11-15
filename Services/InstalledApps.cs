using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Services;








public enum AppCategory
{
    
    Desktop = 0,

    
    Runtime = 1,

    
    Store = 2,

    
    
    
    
    System = 3,
}

















public sealed record InstalledApp(
    string Name,
    string Version,
    string Publisher,
    string Location,
    string UninstallString = "",
    string QuietUninstallString = "",
    bool IsStore = false,
    string PackageFullName = "",
    AppCategory Category = AppCategory.Desktop,
    string IconPath = "");


















public static class InstalledApps
{
    
    public static List<InstalledApp> Enumerate()
    {
        var found = new List<InstalledApp>();

        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Registry64),
                     (RegistryHive.CurrentUser, RegistryView.Registry32),
                 })
        {
            ReadHive(hive, view, found);
        }

        
        
        
        return found
            .GroupBy(a => (a.Name.ToLowerInvariant(), a.Version.ToLowerInvariant()))
            .Select(g => g.OrderByDescending(a => a.UninstallString.Length > 0).First())
            .OrderBy(a => a.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    public static List<InstalledApp> EnumerateStorePackages()
    {
        try
        {
            return EnumerateStoreFromPackageManager();
        }
        catch (Exception ex)
        {
            
            AppLog.Exception("uninstall", ex, "读商店应用（PackageManager）");
            return EnumerateStoreFromRegistry();
        }
    }

    private static List<InstalledApp> EnumerateStoreFromPackageManager()
    {
        var found = new List<InstalledApp>();

        var pm = new Windows.Management.Deployment.PackageManager();

        
        foreach (var pkg in pm.FindPackagesForUser(""))
        {
            try
            {
                if (pkg.IsFramework || pkg.IsResourcePackage || pkg.IsBundle) continue;

                var id = pkg.Id;
                var fullName = id.FullName;

                var name = CleanDisplayName(pkg.DisplayName, fullName);
                if (name.Length == 0) continue;

                
                var location = "";
                try { location = pkg.InstalledLocation?.Path ?? ""; } catch {  }

                var icon = "";
                try { icon = pkg.Logo?.LocalPath ?? ""; } catch {  }

                var publisher = "";
                try { publisher = pkg.PublisherDisplayName?.Trim() ?? ""; } catch {  }

                
                
                var isSystem = pkg.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System;

                
                
                
                var ver = id.Version;
                var version = $"{ver.Major}.{ver.Minor}.{ver.Build}.{ver.Revision}";

                found.Add(new InstalledApp(
                    name,
                    version,
                    publisher,
                    location,
                    IsStore: true,
                    PackageFullName: fullName,
                    Category: isSystem ? AppCategory.System : AppCategory.Store,
                    IconPath: icon));
            }
            catch
            {
                
            }
        }

        
        return found
            .GroupBy(a => DedupKey(a.Name))
            .Where(g => g.Key.Length > 0)
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    
    
    
    
    
    
    
    
    
    
    
    private static string DedupKey(string? name) => (name ?? "").Trim().ToLowerInvariant();

    
    
    
    
    private static string CleanDisplayName(string? raw, string fullName)
    {
        var s = raw?.Trim() ?? "";

        if (s.Length > 0 && s[0] != '@' &&
            !s.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            return s;

        var resolved = s.Length > 0 ? IndirectString.Resolve(s) : "";
        return resolved.Length > 0 ? resolved : LastNameSegment(fullName);
    }

    
    
    
    
    private static List<InstalledApp> EnumerateStoreFromRegistry()
    {
        var found = new List<InstalledApp>();

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                using var repo = baseKey.OpenSubKey(
                    @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
                if (repo is null) continue;

                foreach (var fullName in repo.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = repo.OpenSubKey(fullName);
                        var app = ReadPackage(sub, fullName);
                        if (app is not null) found.Add(app);
                    }
                    catch
                    {
                        
                    }
                }
            }
            catch
            {
                
            }
        }

        
        return found
            .GroupBy(a => DedupKey(a.Name))
            .Where(g => g.Key.Length > 0)
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    private static InstalledApp? ReadPackage(RegistryKey? k, string fullName)
    {
        if (k is null) return null;

        
        
        
        if (k.GetValue("Framework") is int fw && fw == 1) return null;
        
        if (k.GetValue("IsResourcePackage") is int rp && rp == 1) return null;

        
        
        
        var name = ResolvePackageName(k, fullName);
        if (name.Length == 0) return null;

        var location = (k.GetValue("PackageRootFolder") as string)?.Trim() ?? "";
        var publisher = (k.GetValue("PublisherDisplayName") as string)?.Trim() ?? "";

        
        
        return new InstalledApp(name, VersionOf(fullName), publisher, location,
                                IsStore: true, PackageFullName: fullName,
                                Category: AppCategory.Store,
                                IconPath: ResolveStoreLogo(location));
    }

    
    
    
    
    
    
    
    
    
    
    
    private static string ResolvePackageName(RegistryKey k, string fullName)
    {
        var raw = (k.GetValue("DisplayName") as string)?.Trim() ?? "";

        if (raw.Length > 0 && raw[0] != '@' &&
            !raw.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            return raw;

        if (raw.Length > 0 && raw[0] == '@')
        {
            var resolved = IndirectString.Resolve(raw);
            if (resolved.Length > 0 && resolved[0] != '@') return resolved;
        }

        return LastNameSegment(fullName);
    }

    
    
    
    
    
    
    private static string ResolveStoreLogo(string packageRoot)
    {
        if (packageRoot.Length == 0) return "";

        foreach (var dir in new[] { Path.Combine(packageRoot, "Assets"), packageRoot })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;

                
                foreach (var pattern in new[] { "Square44x44Logo*.png", "Square150x150Logo*.png", "StoreLogo*.png" })
                {
                    foreach (var hit in Directory.EnumerateFiles(dir, pattern)
                                                 .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    {
                        return hit;
                    }
                }
            }
            catch
            {
                
            }
        }

        return "";
    }

    
    private static string LastNameSegment(string fullName)
    {
        var name = fullName.Split('_')[0];
        var dot = name.LastIndexOf('.');
        return dot >= 0 && dot + 1 < name.Length ? name[(dot + 1)..] : name;
    }

    
    private static string VersionOf(string fullName)
    {
        var parts = fullName.Split('_');
        return parts.Length > 1 ? parts[1] : "";
    }

    private static void ReadHive(RegistryHive hive, RegistryView view, List<InstalledApp> sink)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) return;

            foreach (var subName in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var sub = uninstall.OpenSubKey(subName);
                    var app = ReadEntry(sub);
                    if (app is not null) sink.Add(app);
                }
                catch
                {
                    
                }
            }
        }
        catch
        {
            
        }
    }

    private static InstalledApp? ReadEntry(RegistryKey? k)
    {
        if (k is null) return null;

        var name = (k.GetValue("DisplayName") as string)?.Trim() ?? "";
        if (name.Length == 0) return null;

        
        var isRuntime = LooksLikeRuntime(name);

        
        
        
        
        
        
        if (k.GetValue("SystemComponent") is int sc && sc == 1 && !isRuntime) return null;
        
        if (!string.IsNullOrWhiteSpace(k.GetValue("ParentKeyName") as string)) return null;
        
        var releaseType = (k.GetValue("ReleaseType") as string)?.Trim() ?? "";
        if (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
            releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase) ||
            releaseType.Contains("ServicePack", StringComparison.OrdinalIgnoreCase))
            return null;
        
        if (k.GetValue("NoDisplay") is int nd && nd == 1) return null;

        var version = (k.GetValue("DisplayVersion") as string)?.Trim() ?? "";
        var publisher = (k.GetValue("Publisher") as string)?.Trim() ?? "";
        var location = (k.GetValue("InstallLocation") as string)?.Trim() ?? "";

        
        
        
        var uninstall = (k.GetValue("UninstallString") as string)?.Trim() ?? "";
        var quiet = (k.GetValue("QuietUninstallString") as string)?.Trim() ?? "";

        return new InstalledApp(name, version, publisher, location, uninstall, quiet,
                                Category: isRuntime ? AppCategory.Runtime : AppCategory.Desktop,
                                IconPath: ResolveIconPath(k, location, uninstall));
    }

    

    
    
    
    
    
    
    
    
    private static readonly string[] RuntimeHints =
    {
        "redistributable", "vcredist", "visual c++", "运行库", "运行环境",
        ".net framework", ".net runtime", ".net desktop runtime", ".net host", "asp.net core",
        "windows app runtime", "windows desktop runtime",
        "directx", "webview2 runtime", "microsoft edge runtime",
        "java", "jre", "jdk", "openal", "physx", "vulkan",
        "msxml", "sql server native client", "odbc driver", "microsoft visual c++",
    };

    
    internal static bool LooksLikeRuntime(string name)
    {
        if (name.Length == 0) return false;
        var n = name.ToLowerInvariant();
        foreach (var hint in RuntimeHints)
            if (n.Contains(hint, StringComparison.Ordinal)) return true;
        return false;
    }

    

    
    
    
    
    
    
    
    
    
    
    private static string ResolveIconPath(RegistryKey? k, string location, string uninstall)
    {
        var fromDisplay = CleanIconValue((k?.GetValue("DisplayIcon") as string)?.Trim() ?? "");
        if (fromDisplay.Length > 0) return fromDisplay;

        var fromLocation = FindMainExecutable(location);
        if (fromLocation.Length > 0) return fromLocation;

        try
        {
            var (exe, _) = AppUninstaller.SplitCommand(uninstall);
            if (exe.Length > 0)
            {
                
                
                var fromUninstallerDir = FindMainExecutable(Path.GetDirectoryName(exe) ?? "");
                if (fromUninstallerDir.Length > 0) return fromUninstallerDir;
                if (File.Exists(exe)) return exe;
            }
        }
        catch
        {
            
        }

        return "";
    }

    
    
    
    
    
    private static string CleanIconValue(string raw)
    {
        if (raw.Length == 0) return "";
        var s = raw.Trim().Trim('"');

        
        var comma = s.LastIndexOf(',');
        if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), out _)) s = s[..comma].Trim().Trim('"');

        if (s.Length == 0 || !File.Exists(s)) return "";
        return Path.GetExtension(s).ToLowerInvariant() switch
        {
            ".exe" or ".ico" or ".dll" or ".png" => s,
            _ => "",
        };
    }

    
    
    
    
    
    
    
    
    private static string FindMainExecutable(string location)
    {
        if (location.Length == 0) return "";

        var hit = PickExecutable(location);
        if (hit.Length > 0) return hit;

        try
        {
            if (!Directory.Exists(location)) return "";

            foreach (var sub in Directory.EnumerateDirectories(location))
            {
                var stem = Path.GetFileName(sub).ToLowerInvariant();
                
                if (stem.StartsWith("unins", StringComparison.Ordinal) || stem.Contains("uninst") ||
                    stem.Contains("cache") || stem.Contains("log") || stem.Contains("temp") ||
                    stem.Contains("update") || stem.Contains("plugin") || stem.Contains("resource"))
                    continue;

                hit = PickExecutable(sub);
                if (hit.Length > 0) return hit;
            }
        }
        catch
        {
            
        }

        return "";
    }

    
    private static string PickExecutable(string dir)
    {
        if (dir.Length == 0) return "";

        try
        {
            if (!Directory.Exists(dir)) return "";

            foreach (var f in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                var stem = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                if (stem.StartsWith("unins", StringComparison.Ordinal) ||
                    stem.Contains("uninst") || stem.Contains("卸载") ||
                    stem.Contains("setup") || stem.Contains("install") ||
                    stem.Contains("update") || stem.Contains("upgrade") ||
                    stem.Contains("helper") || stem.Contains("service") ||
                    stem.Contains("crash") || stem.Contains("report") ||
                    stem.StartsWith("vc_redist", StringComparison.Ordinal) ||
                    stem.StartsWith("vcredist", StringComparison.Ordinal))
                    continue;

                return f;
            }
        }
        catch
        {
            
        }

        return "";
    }

    
    
    
    
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        
        
        
        var cleaned = StripParenthetical(raw);

        var sb = new StringBuilder(cleaned.Length);
        foreach (var ch in cleaned)
        {
            
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }

        var s = sb.ToString();

        
        
        
        foreach (var noise in new[]
                 {
                     
                     "64bit", "32bit", "64位", "32位", "x64", "x86", "amd64", "arm64",
                     
                     "forwindows", "windows版", "中文版",
                     
                     "测试版", "正式版", "极速版", "免费版", "精简版", "便携版",
                     "绿色版", "安装版", "企业版", "专业版", "个人版", "完整版",
                     "增强版", "经典版", "网络版", "抢先版", "体验版", "最新版",
                 })
        {
            s = s.Replace(noise, "");
        }

        
        var cut = s.Length;
        while (cut > 0)
        {
            var c = s[cut - 1];
            if (char.IsDigit(c) || c == '.' || c == 'v') cut--;
            else break;
        }
        
        if (cut > 0 && cut < s.Length && (s.Length - cut) <= Math.Max(4, s.Length / 3))
            s = s[..cut];

        return s;
    }

    
    private static string StripParenthetical(string s)
    {
        var sb = new StringBuilder(s.Length);
        var depth = 0;

        foreach (var ch in s)
        {
            if (ch is '（' or '(')
            {
                depth++;
                continue;
            }

            if (ch is '）' or ')')
            {
                if (depth > 0) depth--;
                continue;
            }

            if (depth == 0) sb.Append(ch);
        }

        return sb.ToString();
    }
}
