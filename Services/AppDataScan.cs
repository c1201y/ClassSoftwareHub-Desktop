using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;


public enum AppDataZone { Local, Roaming }


public enum AppDataKind { Temp, Browser, Store, Dev, Graphics }


public sealed record AppDataRule(string Pattern, AppDataZone Zone, AppDataKind Kind, string Note);


public sealed record AppDataEntry(
    string RelPath, string FullPath, AppDataZone Zone, AppDataKind Kind, string Note);


public sealed record AppDataFolder(string Name, string FullPath, AppDataZone Zone);


public sealed record CleanOutcome(bool Ok, int Cleaned, int Skipped, string Message);



























public static class AppDataScan
{
    
    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        MatchType = MatchType.Simple,
    };

    
    public static string LocalRoot =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    
    public static string RoamingRoot =>
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    
    
    
    
    
    
    
    private static readonly AppDataRule[] Rules =
    {
        
        new(@"Temp", AppDataZone.Local, AppDataKind.Temp,
            "系统临时文件。正在被程序占用的会跳过，属于正常现象。"),
        new(@"Microsoft\Windows\INetCache", AppDataZone.Local, AppDataKind.Temp,
            "旧版 WinINet 网页缓存。删了下次访问重新下载。"),

        
        new(@"Google\Chrome\User Data\*\Cache", AppDataZone.Local, AppDataKind.Browser,
            "Chrome 网页缓存。不含书签、密码、历史记录。"),
        new(@"Google\Chrome\User Data\*\Code Cache", AppDataZone.Local, AppDataKind.Browser,
            "Chrome 脚本字节码缓存。"),
        new(@"Google\Chrome\User Data\*\GPUCache", AppDataZone.Local, AppDataKind.Browser,
            "Chrome 图形缓存。"),
        new(@"Google\Chrome\User Data\*\Service Worker\CacheStorage", AppDataZone.Local, AppDataKind.Browser,
            "Chrome 离线网页缓存。"),
        new(@"Microsoft\Edge\User Data\*\Cache", AppDataZone.Local, AppDataKind.Browser,
            "Edge 网页缓存。不含收藏夹、密码、历史记录。"),
        new(@"Microsoft\Edge\User Data\*\Code Cache", AppDataZone.Local, AppDataKind.Browser,
            "Edge 脚本字节码缓存。"),
        new(@"Microsoft\Edge\User Data\*\GPUCache", AppDataZone.Local, AppDataKind.Browser,
            "Edge 图形缓存。"),
        new(@"Microsoft\Edge\User Data\*\Service Worker\CacheStorage", AppDataZone.Local, AppDataKind.Browser,
            "Edge 离线网页缓存。"),
        new(@"BraveSoftware\Brave-Browser\User Data\*\Cache", AppDataZone.Local, AppDataKind.Browser,
            "Brave 网页缓存。"),

        
        new(@"Packages\*\LocalCache", AppDataZone.Local, AppDataKind.Store,
            "商店应用的本地缓存。目录本身保留，只清里面的内容。"),
        new(@"Packages\*\TempState", AppDataZone.Local, AppDataKind.Store,
            "商店应用的临时状态。"),
        new(@"Packages\*\AC\Temp", AppDataZone.Local, AppDataKind.Store,
            "商店应用的账户组件临时文件。"),

        
        new(@"npm-cache", AppDataZone.Local, AppDataKind.Dev,
            "npm 下载缓存。删了不影响已装好的包，只影响下次安装速度。"),
        new(@"NuGet\v3-cache", AppDataZone.Local, AppDataKind.Dev,
            "NuGet 包缓存（v3）。"),
        new(@"pip\Cache", AppDataZone.Local, AppDataKind.Dev,
            "pip 下载缓存。"),
        new(@"Yarn\Cache", AppDataZone.Local, AppDataKind.Dev,
            "Yarn 下载缓存。"),
        new(@"Code\Cache", AppDataZone.Roaming, AppDataKind.Dev,
            "VS Code 缓存。不含配置与插件本体。"),
        new(@"Code\CachedData", AppDataZone.Roaming, AppDataKind.Dev,
            "VS Code 的 JS 字节码缓存。"),
        new(@"Code\CachedExtensionVSIXs", AppDataZone.Roaming, AppDataKind.Dev,
            "VS Code 已装插件的安装包残留。"),
        new(@"Code\Code Cache", AppDataZone.Roaming, AppDataKind.Dev,
            "VS Code 脚本字节码缓存。"),
        new(@"Code\GPUCache", AppDataZone.Roaming, AppDataKind.Dev,
            "VS Code 图形缓存。"),

        
        new(@"D3DSCache", AppDataZone.Local, AppDataKind.Graphics,
            "Direct3D 着色器缓存。删了游戏会重新编译一次。"),
        new(@"NVIDIA\DXCache", AppDataZone.Local, AppDataKind.Graphics,
            "NVIDIA 着色器缓存。"),
        new(@"NVIDIA\GLCache", AppDataZone.Local, AppDataKind.Graphics,
            "NVIDIA OpenGL 着色器缓存。"),
        new(@"AMD\DxCache", AppDataZone.Local, AppDataKind.Graphics,
            "AMD 着色器缓存。"),
        new(@"Intel\ShaderCache", AppDataZone.Local, AppDataKind.Graphics,
            "Intel 着色器缓存。"),
    };

    
    private static IEnumerable<(string Root, AppDataZone Zone)> RootPairs()
    {
        var local = LocalRoot;
        if (local.Length > 0) yield return (local, AppDataZone.Local);

        var roaming = RoamingRoot;
        if (roaming.Length > 0) yield return (roaming, AppDataZone.Roaming);
    }

    private static IEnumerable<AppDataRule> RulesFor(AppDataZone zone) =>
        Rules.Where(r => r.Zone == zone);

    

    
    
    
    
    public static IReadOnlyList<AppDataFolder> TopFolders()
    {
        var list = new List<AppDataFolder>();

        foreach (var (root, zone) in RootPairs())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in SafeChildren(root, dirs: true))
            {
                var name = Path.GetFileName(dir);
                if (name.Length == 0) continue;
                list.Add(new AppDataFolder(name, dir, zone));
            }
        }

        return list;
    }

    

    
    public static IReadOnlyList<AppDataEntry> FindCleanable()
    {
        var found = new List<AppDataEntry>();

        foreach (var (root, zone) in RootPairs())
        {
            if (!Directory.Exists(root)) continue;

            foreach (var rule in RulesFor(zone))
                Walk(root, rule.Pattern.Split('\\'), 0, rule, zone, found);
        }

        
        var dedup = found
            .GroupBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        
        
        
        
        var cleanable = dedup.Where(e => IsCleanable(e.FullPath)).ToList();
        if (cleanable.Count != dedup.Count)
        {
            AppLog.Warning("appdata",
                $"白名单里有 {dedup.Count - cleanable.Count} 条规则列得出、清不掉（判据不一致），已在列表里隐去："
                + string.Join("、", dedup.Except(cleanable).Select(e => e.RelPath)));
        }

        return cleanable
            .OrderBy(e => e.RelPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    
    private static void Walk(string dir, string[] segs, int idx,
        AppDataRule rule, AppDataZone zone, List<AppDataEntry> into)
    {
        if (idx >= segs.Length)
        {
            if (!Directory.Exists(dir)) return;
            if (IsReparsePoint(dir)) return;

            var rel = RelativeToRoot(dir, zone);
            if (rel.Length == 0) return;

            into.Add(new AppDataEntry(rel, dir, zone, rule.Kind, rule.Note));
            return;
        }

        var seg = segs[idx];
        if (seg == "*")
        {
            
            
            if (!Directory.Exists(dir)) return;

            foreach (var sub in SafeChildren(dir, dirs: true))
                Walk(sub, segs, idx + 1, rule, zone, into);
            return;
        }

        Walk(Path.Combine(dir, seg), segs, idx + 1, rule, zone, into);
    }

    

    
    
    
    
    public static (long Bytes, int Files) Measure(string path, CancellationToken ct)
    {
        long bytes = 0;
        var files = 0;

        try
        {
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", WalkOptions))
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    bytes += f.Length;
                    files++;
                }
                catch (Exception ex)
                {
                    
                    AppLog.Debug("appdata", $"统计单个文件失败（{f.FullName}）：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("appdata", $"统计目录失败（{path}）：{ex.Message}");
        }

        return (bytes, files);
    }

    

    
    
    
    
    
    
    
    
    
    
    
    
    
    public static bool IsCleanable(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return false;

        string full;
        try { full = Path.GetFullPath(fullPath); }
        catch { return false; }

        foreach (var (root, zone) in RootPairs())
        {
            string rootFull;
            try { rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar; }
            catch { continue; }

            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;

            var rel = full[rootFull.Length..].TrimEnd(Path.DirectorySeparatorChar);
            if (rel.Length == 0) return false;                              

            var segs = rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (segs.Any(s => s is "." or "..")) return false;              
            if (IsReparsePoint(full)) return false;                         

            var rule = RulesFor(zone).FirstOrDefault(r => Matches(r.Pattern, segs));   
            if (rule is null) return false;

            
            
            
            
            
            if (segs.Length < 2 && rule.Pattern.Contains('*')) return false;

            return true;
        }

        return false;
    }

    
    private static bool Matches(string pattern, string[] segs)
    {
        var p = pattern.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != segs.Length) return false;

        for (var i = 0; i < p.Length; i++)
        {
            if (p[i] == "*") continue;
            if (!string.Equals(p[i], segs[i], StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    

    
    
    
    
    
    
    
    
    public static CleanOutcome Clean(AppDataEntry entry, CancellationToken ct, Action<long>? onBytesDeleted = null)
    {
        
        if (!IsCleanable(entry.FullPath))
        {
            AppLog.Warning("appdata", "拒绝清理不在白名单内的路径：" + entry.FullPath);
            return new CleanOutcome(false, 0, 0, "这个路径不在白名单内，已拒绝执行。");
        }

        if (!Directory.Exists(entry.FullPath))
            return new CleanOutcome(false, 0, 0, "目录已经不存在了。");

        
        var deleted = 0;
        var skipped = 0;
        PurgeInto(entry.FullPath, ref deleted, ref skipped, ct, onBytesDeleted);

        if (deleted == 0 && skipped == 0)
            return new CleanOutcome(false, 0, 0, "这个目录本来就是空的。");

        
        try
        {
            Directory.CreateDirectory(entry.FullPath);
        }
        catch (Exception ex)
        {
            AppLog.Info("appdata", $"清理后重建目录失败（{entry.RelPath}）：{ex.Message}");
        }

        AppLog.Info("appdata",
            $"已清理（永久删除 · {entry.RelPath}）：删掉 {deleted} 项，跳过 {skipped} 项");

        return new CleanOutcome(
            skipped == 0, deleted, skipped,
            skipped == 0 ? "" : $"有 {skipped} 项正被程序占用，已跳过。");
    }

    
    
    
    
    
    
    
    private static void PurgeInto(string dir, ref int deleted, ref int skipped, CancellationToken ct,
                              Action<long>? onBytesDeleted)
    {
        foreach (var sub in SafeChildren(dir, dirs: true))
        {
            if (ct.IsCancellationRequested) return;

            PurgeInto(sub, ref deleted, ref skipped, ct, onBytesDeleted);

            try
            {
                Directory.Delete(sub, recursive: false);
                deleted++;
            }
            catch (Exception ex)
            {
                skipped++;
                AppLog.Debug("appdata", $"跳过目录（{sub}）：{ex.Message}");
            }
        }

        foreach (var file in SafeFiles(dir))
        {
            if (ct.IsCancellationRequested) return;

            
            
            long size = 0;
            try { size = new FileInfo(file).Length; }
            catch { }

            try
            {
                File.Delete(file);
                deleted++;
                if (size > 0) onBytesDeleted?.Invoke(size);
            }
            catch (Exception ex)
            {
                skipped++;
                AppLog.Debug("appdata", $"跳过文件（{file}）：{ex.Message}");
            }
        }
    }

    

    
    
    
    
    
    
    public static bool OpenInExplorer(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;

            var full = Path.GetFullPath(path);
            var inside = RootPairs().Any(p =>
                full.StartsWith(
                    Path.GetFullPath(p.Root) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));

            if (!inside)
            {
                AppLog.Warning("appdata", "拒绝打开不在 AppData 下的路径：" + full);
                return false;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{full}\"") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Fail("appdata", ex, "打开目录：" + path);
            return false;
        }
    }

    

    
    
    
    
    private static readonly Dictionary<AppDataKind, string[]> KindProcesses = new()
    {
        [AppDataKind.Browser] = new[] { "chrome", "msedge", "brave", "firefox" },
        [AppDataKind.Dev] = new[] { "Code", "devenv" },
    };

    
    public static IReadOnlyList<string> RunningApps(AppDataKind kind)
    {
        if (!KindProcesses.TryGetValue(kind, out var names)) return Array.Empty<string>();

        var running = new List<string>();
        foreach (var name in names)
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                var alive = procs.Length > 0;
                foreach (var p in procs) p.Dispose();

                if (alive) running.Add(name);
            }
            catch (Exception ex)
            {
                AppLog.Debug("appdata", $"查询进程失败（{name}）：{ex.Message}");
            }
        }

        return running;
    }

    
    
    
    
    private static IEnumerable<string> SafeChildren(string dir, bool dirs)
    {
        IEnumerable<string> items;
        try
        {
            items = dirs ? Directory.EnumerateDirectories(dir) : Directory.EnumerateFileSystemEntries(dir);
        }
        catch (Exception ex)
        {
            AppLog.Debug("appdata", $"枚举目录失败（{dir}）：{ex.Message}");
            yield break;
        }

        foreach (var item in items)
        {
            bool skip;
            try { skip = IsReparsePoint(item); }
            catch { skip = true; }

            if (!skip) yield return item;
        }
    }

    
    private static IEnumerable<string> SafeFiles(string dir)
    {
        IEnumerable<string> items;
        try
        {
            items = Directory.EnumerateFiles(dir);
        }
        catch (Exception ex)
        {
            AppLog.Debug("appdata", $"枚举文件失败（{dir}）：{ex.Message}");
            yield break;
        }

        foreach (var item in items)
        {
            bool skip;
            try { skip = IsReparsePoint(item); }
            catch { skip = true; }

            if (!skip) yield return item;
        }
    }

    
    private static bool IsReparsePoint(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            
            return true;
        }
    }

    
    private static string RelativeToRoot(string fullPath, AppDataZone zone)
    {
        string root;
        try
        {
            root = Path.GetFullPath(zone == AppDataZone.Local ? LocalRoot : RoamingRoot)
                   + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(fullPath);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return "";
            return full[root.Length..].TrimEnd(Path.DirectorySeparatorChar);
        }
        catch
        {
            return "";
        }
    }
}
