using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;


public sealed record CacheInstanceInfo(
    string Name,
    string FullPath,
    DateTime CreatedLocal,
    long Bytes,
    int FileCount,
    bool IsCurrent);


public sealed record CacheClearResult(int Removed, long FreedBytes, int Failed);























public static class CacheHome
{
    
    public const string FolderName = "cache";

    
    private static readonly Regex InstancePattern =
        new(@"^dv(.+)-(\d{12})$", RegexOptions.Compiled);

    private const string StampFormat = "yyyyMMddHHmm";

    private static readonly object Gate = new();
    private static string? _instanceName;

    
    public static string Root => Path.Combine(SettingsStore.Dir, FolderName);

    
    public static string InstanceName => Ensure();

    
    public static string InstanceDir
    {
        get
        {
            var name = Ensure();
            return name.Length == 0 ? Root : Path.Combine(Root, name);
        }
    }

    
    
    
    
    public static string Ensure()
    {
        if (_instanceName is not null) return _instanceName;

        lock (Gate)
        {
            if (_instanceName is not null) return _instanceName;

            try
            {
                Directory.CreateDirectory(Root);

                
                var prefix = "dv" + ShellConfig.ShellVersion + "-";

                
                var reuse = Directory.EnumerateDirectories(Root)
                    .Select(Path.GetFileName)
                    .Where(n => n is not null
                                && n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                && InstancePattern.IsMatch(n))
                    .OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                var name = reuse ?? prefix + DateTime.Now.ToString(StampFormat, CultureInfo.InvariantCulture);
                Directory.CreateDirectory(Path.Combine(Root, name));
                _instanceName = name;
            }
            catch (Exception ex)
            {
                AppLog.Info("cache", "初始化版本缓存目录失败，退回 cache\\ 根：" + ex.Message);
                _instanceName = "";   
            }

            return _instanceName;
        }
    }

    
    public static IReadOnlyList<CacheInstanceInfo> Scan()
    {
        var root = Root;
        if (!Directory.Exists(root)) return Array.Empty<CacheInstanceInfo>();

        var current = Ensure();
        var list = new List<CacheInstanceInfo>();

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            var m = InstancePattern.Match(name);
            if (!m.Success) continue;

            if (!DateTime.TryParseExact(m.Groups[2].Value, StampFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var created))
                continue;

            long bytes = 0;
            var count = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { bytes += new FileInfo(file).Length; count++; }
                    catch {  }
                }
            }
            catch (Exception ex)
            {
                AppLog.Info("cache", $"统计缓存目录失败（{name}）：{ex.Message}");
            }

            list.Add(new CacheInstanceInfo(
                name, dir, created, bytes, count,
                string.Equals(name, current, StringComparison.OrdinalIgnoreCase)));
        }

        return list.OrderByDescending(x => x.CreatedLocal).ToList();
    }

    
    
    
    
    public static int Clean(int keep)
    {
        keep = Math.Clamp(keep, 1, 10);

        var current = Ensure();
        if (current.Length == 0) return 0;   

        var removed = 0;
        var rank = 0;
        foreach (var inst in Scan())     
        {
            rank++;
            if (rank <= keep) continue;                       
            if (inst.IsCurrent) continue;                     
            if (!IsInsideRoot(inst.FullPath)) continue;       

            try
            {
                Directory.Delete(inst.FullPath, recursive: true);
                removed++;
            }
            catch (Exception ex)
            {
                
                AppLog.Info("cache", $"删除缓存目录失败（{inst.Name}）：{ex.Message}");
            }
        }

        return removed;
    }

    
    
    
    
    
    
    
    
    
    
    
    
    public static CacheClearResult ClearAll()
    {
        var current = Ensure();
        if (current.Length == 0)
        {
            AppLog.Info("cache", "清除缓存已取消：当前实例目录未初始化（无法判断哪个目录在用）");
            return new CacheClearResult(0, 0, 0);
        }

        var removed = 0;
        var failed = 0;
        var freed = 0L;

        foreach (var inst in Scan())
        {
            if (inst.IsCurrent) continue;                    
            if (!IsInsideRoot(inst.FullPath)) continue;      

            try
            {
                Directory.Delete(inst.FullPath, recursive: true);
                removed++;
                freed += inst.Bytes;
            }
            catch (Exception ex)
            {
                failed++;
                AppLog.Info("cache", $"清除缓存时删除失败（{inst.Name}）：{ex.Message}");
            }
        }

        AppLog.Info("cache", $"清除缓存：删除 {removed} 个目录、释放 {freed} 字节、{failed} 个被占用");
        return new CacheClearResult(removed, freed, failed);
    }

    
    public static bool TryDelete(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (!InstancePattern.IsMatch(name)) return false;
        if (string.Equals(name, Ensure(), StringComparison.OrdinalIgnoreCase)) return false;

        var path = Path.Combine(Root, name);
        if (!IsInsideRoot(path)) return false;

        try
        {
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Info("cache", $"删除缓存目录失败（{name}）：{ex.Message}");
            return false;
        }
    }

    
    public static (long Bytes, int Files) Measure()
    {
        long bytes = 0;
        var files = 0;
        foreach (var inst in Scan())
        {
            bytes += inst.Bytes;
            files += inst.FileCount;
        }
        return (bytes, files);
    }

    
    
    
    public static void Start() => System.Threading.Tasks.Task.Run(() =>
    {
        try
        {
            var keep = App.Settings.Current.CacheKeepCount;
            var removed = Clean(keep);
            if (removed > 0)
                AppLog.Info("cache", $"已清理 {removed} 个旧版本缓存目录（保留最近 {Math.Clamp(keep, 1, 10)} 个）");
        }
        catch (Exception ex)
        {
            AppLog.Info("cache", "缓存清理失败: " + ex.Message);
        }
    });

    
    private static bool IsInsideRoot(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
