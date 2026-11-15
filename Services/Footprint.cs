using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Services;


public enum FootprintKind
{
    
    Path = 0,

    
    RegistryKey = 1,

    
    RegistryValue = 2,
}






public sealed record FootprintNode(
    FootprintKind Kind,
    string Probe,
    string Path,
    string Name,
    string ValueName = "");





















public sealed class FootprintSnapshot
{
    private readonly Dictionary<string, FootprintNode> _nodes;

    private FootprintSnapshot(DateTime takenAt, Dictionary<string, FootprintNode> nodes,
                              int elapsedMs, bool truncated)
    {
        TakenAt = takenAt;
        _nodes = nodes;
        ElapsedMs = elapsedMs;
        Truncated = truncated;
    }

    public DateTime TakenAt { get; }

    
    public int ElapsedMs { get; }

    
    
    
    
    
    public bool Truncated { get; }

    public int Count => _nodes.Count;

    internal IReadOnlyDictionary<string, FootprintNode> Nodes => _nodes;

    
    public static FootprintSnapshot Capture(string phase)
    {
        var sw = Stopwatch.StartNew();
        var nodes = new Dictionary<string, FootprintNode>(StringComparer.OrdinalIgnoreCase);
        var truncated = false;

        foreach (var probe in Probes)
        {
            if (sw.ElapsedMilliseconds > BudgetMs)
            {
                truncated = true;
                AppLog.Warning("footprint", $"快照（{phase}）超过 {BudgetMs} ms 预算，剩下的体检点已跳过");
                break;
            }

            try
            {
                if (probe.Kind == ProbeKind.Directory) ScanDirectory(probe, nodes);
                else ScanRegistry(probe, nodes);
            }
            catch (Exception ex)
            {
                
                AppLog.Info("footprint", $"体检点「{probe.Label}」读取失败：{ex.GetType().Name} {ex.Message}");
            }
        }

        sw.Stop();
        AppLog.Info("footprint",
            $"快照（{phase}）：{nodes.Count} 条痕迹，用时 {sw.ElapsedMilliseconds} ms" +
            (truncated ? "（已截断）" : ""));

        return new FootprintSnapshot(DateTime.Now, nodes, (int)sw.ElapsedMilliseconds, truncated);
    }

    
    public IReadOnlyList<FootprintNode> NewSince(FootprintSnapshot before)
    {
        var list = new List<FootprintNode>();
        foreach (var (key, node) in _nodes)
            if (!before._nodes.ContainsKey(key))
                list.Add(node);
        return list;
    }

    
    public bool Has(FootprintNode node) => _nodes.ContainsKey(KeyOf(node));

    
    
    

    
    private const int BudgetMs = 2000;

    private enum ProbeKind { Directory, Registry }

    
    
    
    
    private sealed record Probe(
        ProbeKind Kind,
        string Label,
        string Base,
        int Depth = 1,
        bool ValuesOnly = false,
        bool IncludeFiles = true);

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string ProgramDataDir => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static string UserProfileDir => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    
    private static string StartMenuAllUsers => Path.Combine(ProgramDataDir,
        @"Microsoft\Windows\Start Menu\Programs");

    private static string StartMenuCurrentUser => Path.Combine(RoamingAppData,
        @"Microsoft\Windows\Start Menu\Programs");

    
    
    
    
    private static readonly Probe[] Probes =
    {
        
        new(ProbeKind.Directory, "用户数据",      LocalAppData),
        new(ProbeKind.Directory, "漫游数据",      RoamingAppData),
        new(ProbeKind.Directory, "用户级程序",    Path.Combine(LocalAppData, "Programs")),
        new(ProbeKind.Directory, "低完整性数据",  Path.Combine(LocalAppData, "..", "LocalLow")),
        new(ProbeKind.Directory, "公共数据",      ProgramDataDir),
        new(ProbeKind.Directory, "程序目录",      Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
        new(ProbeKind.Directory, "程序目录(32位)", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
        new(ProbeKind.Directory, "开始菜单(全部用户)", StartMenuAllUsers),
        new(ProbeKind.Directory, "开始菜单(当前用户)", StartMenuCurrentUser),
        new(ProbeKind.Directory, "用户目录",      UserProfileDir),

        
        new(ProbeKind.Registry, "用户软件键",   @"HKCU\SOFTWARE"),
        new(ProbeKind.Registry, "机器软件键",   @"HKLM\SOFTWARE"),
        new(ProbeKind.Registry, "机器软件键(32位)", @"HKLM\SOFTWARE\WOW6432Node"),

        
        
        new(ProbeKind.Registry, "卸载登记(当前用户)", @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        new(ProbeKind.Registry, "卸载登记(机器)",     @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        new(ProbeKind.Registry, "卸载登记(机器32位)", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),

        
        new(ProbeKind.Registry, "开机自启(当前用户)", @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", ValuesOnly: true),
        new(ProbeKind.Registry, "开机自启(机器)",     @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", ValuesOnly: true),
        new(ProbeKind.Registry, "一次性自启(当前用户)", @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", ValuesOnly: true),
        new(ProbeKind.Registry, "一次性自启(机器)",   @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", ValuesOnly: true),

        
        new(ProbeKind.Registry, "路径登记(当前用户)", @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"),
        new(ProbeKind.Registry, "路径登记(机器)",     @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"),
        new(ProbeKind.Registry, "路径登记(机器32位)", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths"),

        
        new(ProbeKind.Registry, "文件关联(当前用户)", @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FileExts", Depth: 2),
        new(ProbeKind.Registry, "文件夹联(机器)",     @"HKLM\SOFTWARE\Classes", Depth: 1),
        new(ProbeKind.Registry, "文件夹联(当前用户)", @"HKCU\SOFTWARE\Classes", Depth: 1),

        
        new(ProbeKind.Registry, "程序关联(当前用户)", @"HKCU\SOFTWARE\Classes\Applications"),
        new(ProbeKind.Registry, "程序关联(机器)",     @"HKLM\SOFTWARE\Classes\Applications"),

        
        
        new(ProbeKind.Registry, "共享组件登记", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs", ValuesOnly: true),
        new(ProbeKind.Registry, "共享组件登记(32位)", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\SharedDLLs", ValuesOnly: true),
    };

    
    
    

    private static void ScanDirectory(Probe probe, Dictionary<string, FootprintNode> sink)
    {
        var baseDir = Path.GetFullPath(probe.Base);
        if (!Directory.Exists(baseDir)) return;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(baseDir))
                Add(sink, FootprintKind.Path, probe.Label, dir, Path.GetFileName(dir));

            if (probe.IncludeFiles)
            {
                foreach (var file in Directory.EnumerateFiles(baseDir))
                    Add(sink, FootprintKind.Path, probe.Label, file, Path.GetFileName(file));
            }
        }
        catch (UnauthorizedAccessException)
        {
            
        }
    }

    private static void ScanRegistry(Probe probe, Dictionary<string, FootprintNode> sink)
    {
        if (!RegistryPath.TryParse(probe.Base, out var hive, out var subPath)) return;

        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using var key = root.OpenSubKey(subPath, writable: false);
        if (key is null) return;

        if (probe.ValuesOnly)
        {
            
            foreach (var name in key.GetValueNames())
            {
                if (name.Length == 0) continue;   
                Add(sink, FootprintKind.RegistryValue, probe.Label, probe.Base, name, name);
            }
            return;
        }

        WalkRegistry(key, probe.Base, probe.Label, probe.Depth, sink);
    }

    private static void WalkRegistry(RegistryKey key, string keyPath, string label, int depth,
                                     Dictionary<string, FootprintNode> sink)
    {
        if (depth <= 0) return;

        foreach (var name in key.GetSubKeyNames())
        {
            var childPath = keyPath + "\\" + name;
            Add(sink, FootprintKind.RegistryKey, label, childPath, name);

            if (depth <= 1) continue;

            try
            {
                using var child = key.OpenSubKey(name, writable: false);
                if (child is not null) WalkRegistry(child, childPath, label, depth - 1, sink);
            }
            catch (Exception ex)
            {
                AppLog.Info("footprint", $"读注册表子键失败（{childPath}）：{ex.GetType().Name}");
            }
        }
    }

    
    
    
    
    private static void Add(Dictionary<string, FootprintNode> sink, FootprintKind kind, string label,
                            string path, string name, string valueName = "")
    {
        sink.TryAdd(KeyOf(kind, path, valueName), new FootprintNode(kind, label, path, name, valueName));
    }

    private static string KeyOf(FootprintKind kind, string path, string valueName)
        => kind + "|" + path.ToLowerInvariant() + "|" + valueName.ToLowerInvariant();

    private static string KeyOf(FootprintNode node) => KeyOf(node.Kind, node.Path, node.ValueName);

    
    internal static string KeyOfPublic(FootprintNode node) => KeyOf(node);
}





internal static class RegistryPath
{
    
    public static bool TryParse(string fullPath, out RegistryHive hive, out string subPath)
    {
        hive = RegistryHive.CurrentUser;
        subPath = "";

        if (string.IsNullOrWhiteSpace(fullPath)) return false;

        var s = fullPath.Trim();
        var slash = s.IndexOf('\\');
        var root = slash < 0 ? s : s[..slash];
        subPath = slash < 0 ? "" : s[(slash + 1)..];

        switch (root.ToUpperInvariant())
        {
            case "HKCU":
            case "HKEY_CURRENT_USER":
                hive = RegistryHive.CurrentUser;
                return true;

            case "HKLM":
            case "HKEY_LOCAL_MACHINE":
                hive = RegistryHive.LocalMachine;
                return true;

            case "HKCR":
            case "HKEY_CLASSES_ROOT":
                hive = RegistryHive.ClassesRoot;
                return true;

            case "HKU":
            case "HKEY_USERS":
                hive = RegistryHive.Users;
                return true;

            default:
                return false;
        }
    }

    
    public static string Combine(RegistryHive hive, string subPath)
    {
        var head = hive switch
        {
            RegistryHive.LocalMachine => "HKLM",
            RegistryHive.CurrentUser => "HKCU",
            RegistryHive.ClassesRoot => "HKCR",
            RegistryHive.Users => "HKU",
            _ => "HKLM",
        };

        return subPath.Length == 0 ? head : head + "\\" + subPath;
    }
}
