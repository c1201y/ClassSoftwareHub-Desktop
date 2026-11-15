using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Services;









public enum LeftoverEvidence
{
    
    Weak = 0,

    
    Medium = 1,

    
    Strong = 2,
}


public sealed class LeftoverItem
{
    public LeftoverItem(FootprintNode node)
    {
        Kind = node.Kind;
        Probe = node.Probe;
        Path = node.Path;
        Name = node.Name;
        ValueName = node.ValueName;
    }

    public FootprintKind Kind { get; }
    public string Probe { get; }
    public string Path { get; }
    public string Name { get; }
    public string ValueName { get; }

    
    public LeftoverEvidence Evidence { get; set; } = LeftoverEvidence.Weak;

    
    public string Reason { get; set; } = "";

    
    public bool Selected { get; set; }

    
    public long SizeBytes { get; set; } = -1;

    
    public int FileCount { get; set; }

    
    public bool StillThere { get; set; } = true;

    public string DisplayPath => Compress(Path, ValueName);

    
    
    
    
    public string DisplayParent
    {
        get
        {
            try
            {
                return Kind switch
                {
                    FootprintKind.Path => Compress(System.IO.Path.GetDirectoryName(Path) ?? "", ""),
                    FootprintKind.RegistryKey => Compress(ParentKeyPath(Path), ""),
                    _ => Compress(Path, ""),
                };
            }
            catch
            {
                return Path;
            }
        }
    }

    
    public string DisplayLeaf
    {
        get
        {
            if (Kind == FootprintKind.RegistryValue) return ValueName;

            var i = Path.LastIndexOf('\\');
            var leaf = i >= 0 ? Path[(i + 1)..] : Path;
            return leaf.Length > 0 ? leaf : Path;
        }
    }

    private static string ParentKeyPath(string keyPath)
    {
        var i = keyPath.LastIndexOf('\\');
        return i > 0 ? keyPath[..i] : keyPath;
    }

    public string EvidenceText => Evidence switch
    {
        LeftoverEvidence.Strong => "确定",
        LeftoverEvidence.Medium => "很可能",
        _ => "不确定",
    };

    public string SizeText => Kind != FootprintKind.Path || SizeBytes < 0
        ? ""
        : SizeBytes == 0
            ? "空"
            : HumanSize(SizeBytes);

    private static string HumanSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.##} GB",
    };

    
    public string KindText => Kind switch
    {
        FootprintKind.Path => Directory.Exists(Path) ? "文件夹" : "文件",
        FootprintKind.RegistryKey => "注册表项",
        _ => "注册表值",
    };

    
    public int SortRank => Kind switch
    {
        FootprintKind.Path => Directory.Exists(Path) ? 0 : 1,
        FootprintKind.RegistryValue => 2,
        _ => 3,
    };

    
    
    
    
    private static string Compress(string path, string valueName)
    {
        var s = path;

        foreach (var (full, token) in ShortPrefixes)
        {
            if (s.StartsWith(full, StringComparison.OrdinalIgnoreCase))
            {
                s = token + s[full.Length..];
                break;
            }
        }

        return valueName.Length > 0 ? s + "  ▸  " + valueName : s;
    }

    private static readonly (string Full, string Token)[] ShortPrefixes = BuildShortPrefixes();

    private static (string, string)[] BuildShortPrefixes()
    {
        var list = new List<(string, string)>
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "%PROGRAMDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "%ProgramFiles%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "%ProgramFiles(x86)%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%"),
        };

        
        
        return list.Where(x => x.Item1.Length > 0)
                   .OrderByDescending(x => x.Item1.Length)
                   .ToArray();
    }
}




























public static class LeftoverResolver
{
    
    
    
    
    
    
    public static List<LeftoverItem> Resolve(FootprintSnapshot before, FootprintSnapshot after, InstalledApp app)
    {
        var sw = Stopwatch.StartNew();

        var profile = new AppProfile(app);
        var items = new List<LeftoverItem>();

        var truncated = before.Truncated || after.Truncated;

        foreach (var node in after.Nodes.Values)
        {
            if (IsForbidden(node)) continue;

            var isNew = !before.Has(node);

            var item = new LeftoverItem(node);
            Grade(item, profile, isNew, truncated);

            
            if (item.Evidence == LeftoverEvidence.Weak && !isNew) continue;

            items.Add(item);
        }

        Measure(items);

        var sorted = items
            .OrderByDescending(i => (int)i.Evidence)
            .ThenBy(i => i.SortRank)
            .ThenBy(i => i.DisplayPath, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        sw.Stop();
        AppLog.Info("leftover",
            $"残留判定（{app.Name}）：差集 {sorted.Count} 条" +
            $"（确定 {sorted.Count(i => i.Evidence == LeftoverEvidence.Strong)}、" +
            $"很可能 {sorted.Count(i => i.Evidence == LeftoverEvidence.Medium)}、" +
            $"不确定 {sorted.Count(i => i.Evidence == LeftoverEvidence.Weak)}），" +
            $"用时 {sw.ElapsedMilliseconds} ms");

        return sorted;
    }

    
    
    

    
    
    
    
    
    
    
    private static void Grade(LeftoverItem item, AppProfile profile, bool isNew, bool truncated)
    {
        
        if (item.Kind == FootprintKind.RegistryValue)
        {
            GradeRegistryValue(item, profile, isNew, truncated);
            return;
        }

        var leaf = LeafName(item);

        
        if (profile.Location.Length > 0 && IsInsideOrEqual(item.Path, profile.Location))
        {
            Set(item, LeftoverEvidence.Strong, "位置位于该软件的安装目录之内");
            return;
        }

        var n = InstalledApps.Normalize(leaf);

        
        
        
        var nStem = InstalledApps.Normalize(Path.GetFileNameWithoutExtension(leaf));

        
        
        
        
        
        if (Exact(n, profile.NameKey, 4) || Exact(nStem, profile.NameKey, 4))
        {
            Set(item, LeftoverEvidence.Strong, "名称与软件名一致");
            return;
        }

        if (Exact(n, profile.FolderKey, 3) || Exact(nStem, profile.FolderKey, 3) ||
            Exact(n, profile.ExeKey, 3) || Exact(nStem, profile.ExeKey, 3))
        {
            Set(item, LeftoverEvidence.Strong, "名称与程序目录名一致");
            return;
        }

        
        
        
        
        
        
        
        if (item.Kind == FootprintKind.RegistryKey && IsAssociationProbe(item.Probe))
        {
            var target = TryReadDefaultValue(item.Path);
            if (target.Length > 0)
            {
                if (profile.Location.Length > 0 &&
                    target.Contains(profile.Location, StringComparison.OrdinalIgnoreCase))
                {
                    Set(item, LeftoverEvidence.Strong, "关联目标为该软件的安装目录");
                    return;
                }

                foreach (var part in TargetNameParts(target))
                {
                    if (Exact(part, profile.NameKey, 4) || Exact(part, profile.ExeKey, 3))
                    {
                        Set(item, LeftoverEvidence.Strong, "关联目标为该软件");
                        return;
                    }
                }
            }
        }

        
        if (Contains(n, profile.NameKey, 0.4, 4) || Contains(nStem, profile.NameKey, 0.4, 4) ||
            Contains(n, profile.FolderKey, 0.5, 4) || Contains(nStem, profile.FolderKey, 0.5, 4))
        {
            Set(item, LeftoverEvidence.Medium, "名称与软件名相似");
            return;
        }

        foreach (var token in profile.Tokens)
        {
            if (Contains(n, token, 0.5, 5) || Contains(nStem, token, 0.5, 5))
            {
                Set(item, LeftoverEvidence.Medium, $"名称含「{token}」");
                return;
            }
        }

        
        
        
        Set(item, LeftoverEvidence.Weak,
            isNew ? "卸载过程中新增，名称不符" : "名称不符");

        
        if (truncated) item.Selected = false;
    }

    
    
    
    
    
    
    private static void GradeRegistryValue(LeftoverItem item, AppProfile profile, bool isNew, bool truncated)
    {
        
        if (item.Probe.StartsWith("共享组件", StringComparison.Ordinal))
        {
            if (profile.Location.Length > 0 && IsInsideOrEqual(item.ValueName, profile.Location))
            {
                Set(item, LeftoverEvidence.Strong, "登记的组件位于该软件的安装目录之内");
                return;
            }

            var dllName = Path.GetFileName(item.ValueName);
            var dllKey = InstalledApps.Normalize(dllName);
            var dllStem = InstalledApps.Normalize(Path.GetFileNameWithoutExtension(dllName));

            if (Exact(dllKey, profile.NameKey, 4) || Exact(dllStem, profile.NameKey, 4) ||
                Exact(dllKey, profile.ExeKey, 3) || Exact(dllStem, profile.ExeKey, 3))
            {
                Set(item, LeftoverEvidence.Strong, "登记的组件名与该程序一致");
                return;
            }
        }

        
        var data = TryReadValueData(item);
        if (data.Length > 0)
        {
            if (profile.Location.Length > 0 &&
                data.Contains(profile.Location, StringComparison.OrdinalIgnoreCase))
            {
                Set(item, LeftoverEvidence.Strong, "该值内容指向此软件的安装目录");
                return;
            }

            
            
            var dataStem = InstalledApps.Normalize(
                Path.GetFileNameWithoutExtension(data.Trim().Trim('"').Trim()));
            if (Exact(dataStem, profile.NameKey, 4) || Exact(dataStem, profile.ExeKey, 3))
            {
                Set(item, LeftoverEvidence.Strong, "该值内容指向此软件");
                return;
            }
        }

        
        var n = InstalledApps.Normalize(item.ValueName);
        var nStem = InstalledApps.Normalize(Path.GetFileNameWithoutExtension(item.ValueName));

        if (Exact(n, profile.NameKey, 4) || Exact(nStem, profile.NameKey, 4) ||
            Exact(n, profile.ExeKey, 3) || Exact(nStem, profile.ExeKey, 3))
        {
            Set(item, LeftoverEvidence.Strong, "名称与软件名一致");
            return;
        }

        if (Contains(n, profile.NameKey, 0.4, 4) || Contains(nStem, profile.NameKey, 0.4, 4))
        {
            Set(item, LeftoverEvidence.Medium, "值名称与软件名相似");
            return;
        }

        foreach (var token in profile.Tokens)
        {
            if (Contains(n, token, 0.5, 5) || Contains(nStem, token, 0.5, 5))
            {
                Set(item, LeftoverEvidence.Medium, $"名称含「{token}」");
                return;
            }
        }

        Set(item, LeftoverEvidence.Weak, isNew ? "卸载过程中新增，名称不符" : "名称不符");
        if (truncated) item.Selected = false;
    }

    private static string TryReadValueData(LeftoverItem item)
    {
        try
        {
            if (!RegistryPath.TryParse(item.Path, out var hive, out var subPath)) return "";
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = root.OpenSubKey(subPath, writable: false);
            return key?.GetValue(item.ValueName) as string ?? "";
        }
        catch (Exception ex)
        {
            AppLog.Info("leftover", $"读注册表值内容失败（{item.Path} ▸ {item.ValueName}）：{ex.GetType().Name}");
            return "";
        }
    }

    
    
    
    
    
    
    private static string TryReadDefaultValue(string path)
    {
        try
        {
            if (!RegistryPath.TryParse(path, out var hive, out var subPath)) return "";
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = root.OpenSubKey(subPath, writable: false);
            return key?.GetValue("") as string ?? "";
        }
        catch (Exception ex)
        {
            AppLog.Info("leftover", $"读注册表默认值失败（{path}）：{ex.GetType().Name}");
            return "";
        }
    }

    
    private static bool IsAssociationProbe(string probe)
        => probe.StartsWith("文件夹联", StringComparison.Ordinal)
        || probe.StartsWith("程序关联", StringComparison.Ordinal)
        || probe.StartsWith("路径登记", StringComparison.Ordinal);

    
    
    
    
    
    
    private static IEnumerable<string> TargetNameParts(string target)
    {
        var t = target.Trim().Trim('"').Trim();
        if (t.Length == 0) yield break;

        if (t.Contains('\\') || t.Contains('/') || t.Contains(' '))
        {
            yield return InstalledApps.Normalize(Path.GetFileNameWithoutExtension(t));
            yield break;
        }

        yield return InstalledApps.Normalize(t);

        var dot = t.IndexOf('.');
        if (dot > 0) yield return InstalledApps.Normalize(t[..dot]);
    }

    
    
    
    
    private static bool Exact(string candidate, string key, int require)
        => candidate.Length >= require && key.Length >= require &&
           candidate.Equals(key, StringComparison.Ordinal);

    
    
    
    
    
    private static bool Contains(string candidate, string key, double ratio, int require)
    {
        if (candidate.Length < require || key.Length < require) return false;
        if (candidate.Equals(key, StringComparison.Ordinal)) return true;

        if (candidate.Contains(key, StringComparison.Ordinal))
            return key.Length >= candidate.Length * ratio;

        if (key.Contains(candidate, StringComparison.Ordinal))
            return candidate.Length >= key.Length * ratio;

        return false;
    }

    private static string LeafName(LeftoverItem item)
    {
        if (item.Kind == FootprintKind.RegistryKey)
        {
            var i = item.Path.LastIndexOf('\\');
            return i >= 0 ? item.Path[(i + 1)..] : item.Path;
        }

        try
        {
            var leaf = Path.GetFileName(item.Path);
            return leaf.Length > 0 ? leaf : item.Path;
        }
        catch
        {
            return item.Name;
        }
    }

    
    
    
    
    
    
    
    private static void Set(LeftoverItem item, LeftoverEvidence level, string reason)
    {
        item.Evidence = level;
        item.Reason = reason;
        item.Selected = level == LeftoverEvidence.Strong;
    }

    
    
    

    
    
    
    
    
    private static bool IsForbidden(FootprintNode node)
    {
        if (node.Kind == FootprintKind.Path) return IsForbiddenPath(node.Path);
        if (node.Kind == FootprintKind.RegistryKey) return IsForbiddenRegistryKey(node.Probe, node.Path);
        return IsForbiddenRegistryValue(node);
    }

    
    
    
    
    public static bool IsForbiddenPathPublic(string path) => IsForbiddenPath(path);

    
    
    
    
    
    
    
    public static bool IsForbiddenRegistryPublic(string probe, string path, string valueName)
        => IsForbidden(new FootprintNode(FootprintKind.RegistryValue, probe, path, path, valueName));

    
    public static bool IsForbiddenKeyPublic(string probe, string path)
        => IsForbidden(new FootprintNode(FootprintKind.RegistryKey, probe, path, path, ""));

    private static bool IsForbiddenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;

        string full;
        try { full = Path.GetFullPath(path); }
        catch { return true; }

        
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length > 0 && IsInsideOrEqual(full, windows)) return true;

        
        foreach (var prefix in ForbiddenPathPrefixes)
            if (prefix.Length > 0 && IsInsideOrEqual(full, prefix)) return true;

        
        var leaf = Path.GetFileName(full.TrimEnd('\\'));
        if (leaf.Length == 0) return true;
        if (ForbiddenLeafNames.Contains(leaf)) return true;

        
        
        
        if (leaf.StartsWith('.')) return true;

        return false;
    }

    
    
    
    
    
    
    
    
    
    
    private static bool IsForbiddenRegistryKey(string probe, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;

        var i = path.LastIndexOf('\\');
        var leaf = i >= 0 ? path[(i + 1)..] : path;
        if (ForbiddenRegistryLeafNames.Contains(leaf)) return true;

        if (!GenericSoftwareKeyProbes.Contains(probe)) return false;

        foreach (var prefix in ForbiddenRegistryPrefixes)
            if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    
    private static readonly HashSet<string> GenericSoftwareKeyProbes = new(StringComparer.Ordinal)
    {
        "用户软件键", "机器软件键", "机器软件键(32位)",
        "文件夹联(机器)", "文件夹联(当前用户)",
    };

    
    
    
    
    private static readonly string[] ForbiddenRegistryPrefixes =
    {
        @"HKCU\SOFTWARE\Microsoft",
        @"HKLM\SOFTWARE\Microsoft",
        @"HKLM\SOFTWARE\WOW6432Node\Microsoft",
        @"HKCU\SOFTWARE\Policies",
        @"HKLM\SOFTWARE\Policies",
        @"HKLM\SOFTWARE\WOW6432Node\Policies",
        @"HKCU\SOFTWARE\Classes\CLSID",
        @"HKLM\SOFTWARE\Classes\CLSID",
        @"HKLM\SOFTWARE\WOW6432Node\Classes\CLSID",
    };

    private static bool IsForbiddenRegistryValue(FootprintNode node)
    {
        
        if (node.Probe.StartsWith("开机自启", StringComparison.Ordinal) ||
            node.Probe.StartsWith("一次性自启", StringComparison.Ordinal))
        {
            return ForbiddenRunValueNames.Contains(node.ValueName)
                || node.ValueName.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);
        }

        
        
        if (node.Probe.StartsWith("共享组件", StringComparison.Ordinal))
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (windows.Length > 0 && node.ValueName.Length > 0
                && IsInsideOrEqual(node.ValueName, windows))
                return true;
        }

        return false;
    }

    private static string[] ForbiddenPathPrefixes => _forbiddenPathPrefixes ??= BuildForbiddenPathPrefixes();

    private static string[]? _forbiddenPathPrefixes;

    private static string[] BuildForbiddenPathPrefixes()
    {
        var la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var ra = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var list = new List<string>
        {
            
            Path.Combine(pf, "Common Files"),
            Path.Combine(pf, "WindowsApps"),
            Path.Combine(pf, "Windows Defender"),
            Path.Combine(pf, "Windows Defender Advanced Threat Protection"),
            Path.Combine(pf, "Windows NT"),
            Path.Combine(pf, "WindowsPowerShell"),
            Path.Combine(pf, "Windows Mail"),
            Path.Combine(pf, "Windows Media Player"),
            Path.Combine(pf, "Windows Multimedia Platform"),
            Path.Combine(pf, "Windows Photo Viewer"),
            Path.Combine(pf, "Windows Portable Devices"),
            Path.Combine(pf, "Windows Sidebar"),
            Path.Combine(pf, "Windows System Assessment Tool"),
            Path.Combine(pf, "Internet Explorer"),
            Path.Combine(pf, "ModifiableWindowsApps"),
            Path.Combine(pf, "Reference Assemblies"),
            Path.Combine(pf, "MSBuild"),
            Path.Combine(pf, "dotnet"),
            Path.Combine(pf, "Microsoft"),

            
            Path.Combine(pd, "Microsoft"),
            Path.Combine(pd, "Package Cache"),
            Path.Combine(pd, "Packages"),
            Path.Combine(pd, "Windows"),
            Path.Combine(pd, "Windows Defender"),
            Path.Combine(pd, "Desktop") ,

            
            Path.Combine(la, "Microsoft"),
            Path.Combine(la, "Packages"),
            Path.Combine(la, "Temp"),
            Path.Combine(la, "ClassSoftwareHub"),   
            Path.Combine(la, "ConnectedDevicesPlatform"),
            Path.Combine(la, "CrashDumps"),
            Path.Combine(la, "D3DSCache"),
            Path.Combine(la, "GroupPolicy"),
            Path.Combine(la, "History"),
            Path.Combine(la, "INetCache"),
            Path.Combine(la, "INetCookies"),
            Path.Combine(la, "PlaceholderTileLogoFolder"),
            Path.Combine(la, "Publishers"),
            Path.Combine(la, "VirtualStore"),
            Path.Combine(la, "ElevatedDiagnostics"),
            Path.Combine(la, "Programs", "Common"),

            
            
            
            
            
            
            Path.Combine(ra, "ClassSoftwareHub"),

            
            
            
            
            
            
            Path.Combine(up, "Desktop"),
            Path.Combine(up, "Documents"),
            Path.Combine(up, "Downloads"),
            Path.Combine(up, "Music"),
            Path.Combine(up, "Pictures"),
            Path.Combine(up, "Videos"),
            Path.Combine(up, "Favorites"),
            Path.Combine(up, "Links"),
            Path.Combine(up, "Contacts"),
            Path.Combine(up, "Saved Games"),
            Path.Combine(up, "Searches"),
            Path.Combine(up, "3D Objects"),
            Path.Combine(up, "OneDrive"),
            Path.Combine(up, "OneDrive - Personal"),
        };

        return list.Where(p => p.Length > 3).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static HashSet<string>? _forbiddenLeafNames;

    private static HashSet<string> ForbiddenLeafNames => _forbiddenLeafNames ??= new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Microsoft", "Common Files", "WindowsApps", "WinSxS",
        "System32", "SysWOW64", "System", "Assembly", "Fonts", "Inf",
        "AppData", "ProgramData", "Users", "Program Files", "Program Files (x86)",
        "ClassSoftwareHub", "Package Cache", "Packages", "Temp", "Tmp",
        "desktop.ini", "ntuser.dat", "ntuser.ini", "NTUSER.DAT.LOG1", "NTUSER.DAT.LOG2",
        "bootmgr", "BOOTNXT", "pagefile.sys", "swapfile.sys", "hiberfil.sys", "DumpStack.log.tmp",
        "$Recycle.Bin", "System Volume Information", "Recovery", "PerfLogs",
        "config.msi", "$PatchCache$", "$WinREAgent", "$SysReset", "Intel", "AMD", "NVIDIA",
        "Realtek", "Common", "InstallShield", "Windows Kits", "Application Verifier",
    };

    private static HashSet<string>? _forbiddenRegistryLeafNames;

    
    
    
    
    
    private static HashSet<string> ForbiddenRegistryLeafNames => _forbiddenRegistryLeafNames ??= new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "CLSID", "AppID", "Interface", "TypeLib", "Wow6432Node", "Local Settings",
        "Installer", "Component Categories", "Extensions", "MIME", "PackagedCom",
        "ActivatableClasses", "Media Type", "VirtualStore", "Licenses", "CLSID\\{00000000-0000-0000-0000-000000000000}",
        "Microsoft", "Policies", "Windows", "Windows NT", "Classes", "RegisteredApplications",
        "OEM", "Setup", "SystemCertificates", "Cryptography", "ProtectedStorage",
        "CurrentVersion", "Explorer", "Run", "RunOnce", "App Paths", "Uninstall",
        "SharedDLLs", "FileExts", "Applications", "StartupApproved", "Shell Extensions",
        "*", ".exe", ".dll", ".com", ".bat", ".cmd", ".ps1",
    };

    
    private static HashSet<string>? _forbiddenRunValueNames;

    private static HashSet<string> ForbiddenRunValueNames => _forbiddenRunValueNames ??= new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "SecurityHealth", "SecurityHealthSystray", "ctfmon", "OneDrive",
        "OneDriveSetup", "MicrosoftEdgeAutoLaunch", "WindowsDefender",
        "RTHDVCPL", "RtHDVBg", "IAStorIcon", "igfxTray", "Persistence",
        "Steam Client Bootstrapper", "RtkAudUService64_BG",
    };

    
    
    

    
    private const int MeasureBudgetMs = 1500;

    private static void Measure(List<LeftoverItem> items)
    {
        foreach (var item in items)
        {
            if (item.Kind != FootprintKind.Path) { item.SizeBytes = 0; continue; }

            var sw = Stopwatch.StartNew();
            long bytes = 0;
            var files = 0;

            try
            {
                if (File.Exists(item.Path))
                {
                    bytes = new FileInfo(item.Path).Length;
                    files = 1;
                }
                else if (Directory.Exists(item.Path))
                {
                    foreach (var f in Directory.EnumerateFiles(item.Path, "*", SearchOption.AllDirectories))
                    {
                        if (sw.ElapsedMilliseconds > MeasureBudgetMs) break;
                        try { bytes += new FileInfo(f).Length; files++; }
                        catch {  }
                    }
                }
                else
                {
                    item.SizeBytes = -1;   
                    continue;
                }
            }
            catch (Exception ex)
            {
                AppLog.Info("leftover", $"测算体积失败（{item.Path}）：{ex.GetType().Name}");
                item.SizeBytes = -1;
                continue;
            }

            item.SizeBytes = bytes;
            item.FileCount = files;
        }
    }

    
    
    

    
    
    
    
    internal static bool IsInsideOrEqual(string child, string parent)
    {
        if (child.Length == 0 || parent.Length == 0) return false;

        string c, p;
        try
        {
            c = Path.GetFullPath(child).TrimEnd('\\');
            p = Path.GetFullPath(parent).TrimEnd('\\');
        }
        catch
        {
            return false;
        }

        if (c.Equals(p, StringComparison.OrdinalIgnoreCase)) return true;
        return c.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase);
    }

    
    private sealed class AppProfile
    {
        public AppProfile(InstalledApp app)
        {
            NameKey = InstalledApps.Normalize(app.Name);
            FolderKey = app.Location.Length > 0
                ? InstalledApps.Normalize(Path.GetFileName(app.Location.TrimEnd('\\')))
                : "";
            Location = app.Location;

            
            
            
            
            
            

            
            
            
            
            var exeKey = "";
            try
            {
                var raw = app.QuietUninstallString.Length > 0 ? app.QuietUninstallString : app.UninstallString;
                var (exe, _) = AppUninstaller.SplitCommand(raw);
                var stem = exe.Length > 0 ? Path.GetFileNameWithoutExtension(exe) : "";

                if (!LooksLikeUninstaller(stem)) exeKey = InstalledApps.Normalize(stem);
            }
            catch
            {
                
            }

            ExeKey = exeKey;

            
            var tokens = new List<string>();
            foreach (var part in app.Name.Split(
                         new[] { ' ', '-', '_', '·', '（', '）', '(', ')', '/', '\\', '.', ',' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var t = InstalledApps.Normalize(part);
                if (t.Length >= 5 && !tokens.Contains(t)) tokens.Add(t);
            }

            Tokens = tokens;
        }

        public string NameKey { get; }
        public string FolderKey { get; }
        public string ExeKey { get; }
        public string Location { get; }
        public List<string> Tokens { get; }

        
        private static bool LooksLikeUninstaller(string stem)
        {
            if (stem.Length == 0) return true;
            var s = stem.ToLowerInvariant();

            return s.StartsWith("unins", StringComparison.Ordinal)
                || s.Contains("uninst", StringComparison.Ordinal)
                || s.Contains("卸载", StringComparison.Ordinal)
                || s.StartsWith("setup", StringComparison.Ordinal)
                || s.StartsWith("install", StringComparison.Ordinal);
        }

        
        
        
        
        public bool AllowWeak => true;
    }
}
