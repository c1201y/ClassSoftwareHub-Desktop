using System;
using System.IO;
using System.Linq;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;





















public static class LegacyCleanup
{
    private static bool _done;

    
    private static readonly string[] RootAssetPatterns = { "*.png", "*.ico" };

    
    private static readonly string[] RootDumpFiles = { "pane-dump.txt", "dlg-dump.txt" };

    
    private static readonly string[] CacheRootPatterns = { "*.png", "*.ico", "*.wav" };

    
    public static void Run()
    {
        if (_done) return;
        _done = true;

        try
        {
            var removed = CleanDataRoot() + CleanCacheRoot();
            if (removed > 0)
                AppLog.Info("legacy", $"已清理 {removed} 个旧结构遗留文件");
        }
        catch (Exception ex)
        {
            AppLog.Info("legacy", "清理旧残留失败：" + ex.Message);
        }
    }

    
    private static int CleanDataRoot()
    {
        var root = SettingsStore.Dir;
        if (!Directory.Exists(root)) return 0;

        var removed = 0;

        
        foreach (var pattern in RootAssetPatterns)
            foreach (var file in Directory.GetFiles(root, pattern))
                if (TryDelete(file)) removed++;

        
        
        
        var logRoot = AppLog.Root;
        foreach (var file in Directory.GetFiles(root, "*.log"))
        {
            var name = Path.GetFileName(file);
            if (!File.Exists(Path.Combine(logRoot, name)) && !ExistsUnder(logRoot, name)) continue;
            if (TryDelete(file)) removed++;
        }

        
        foreach (var name in RootDumpFiles)
        {
            var file = Path.Combine(root, name);
            if (File.Exists(file) && TryDelete(file)) removed++;
        }

        return removed;
    }

    
    private static int CleanCacheRoot()
    {
        var cache = CacheHome.Root;
        if (!Directory.Exists(cache)) return 0;

        var removed = 0;
        foreach (var pattern in CacheRootPatterns)
            foreach (var file in Directory.GetFiles(cache, pattern))
                if (TryDelete(file)) removed++;

        return removed;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Info("legacy", $"删除失败（{Path.GetFileName(path)}）：{ex.Message}");
            return false;
        }
    }

    
    private static bool ExistsUnder(string logRoot, string fileName)
    {
        try
        {
            if (!Directory.Exists(logRoot)) return false;
            return Directory.EnumerateFiles(logRoot, fileName, SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }
}
