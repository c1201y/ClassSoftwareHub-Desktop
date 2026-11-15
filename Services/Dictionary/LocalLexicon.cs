using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Core.Dictionary;

namespace ClassSoftwareHub.Desktop.Services.Dictionary;





















public static class LocalLexicon
{
    private static readonly object Gate = new();
    private static Dictionary<string, DictEntry>? _index;

    
    private static string[]? _sortedKeys;
    private static Task? _loading;

    
    public static bool IsReady
    {
        get { lock (Gate) return _index is not null; }
    }

    
    public static int Count
    {
        get { lock (Gate) return _index?.Count ?? 0; }
    }

    private static string Folder => Path.Combine(AppPaths.DataDir, "dict");

    private static string CorePath => Path.Combine(Folder, "dict-core.tsv");

    private static string UserPath => Path.Combine(Folder, "user.tsv");

    
    private static string StampPath => Path.Combine(Folder, "dict-core.stamp");

    
    
    
    
    public static Task EnsureLoadedAsync()
    {
        lock (Gate)
        {
            if (_index is not null) return Task.CompletedTask;
            return _loading ??= Task.Run(LoadCore);
        }
    }

    private static void LoadCore()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var map = new Dictionary<string, DictEntry>(StringComparer.OrdinalIgnoreCase);

            var core = EnsureExtractedCore();
            if (core is not null) ReadInto(core, map);

            
            
            if (File.Exists(UserPath)) ReadInto(UserPath, map);

            lock (Gate)
            {
                _index = map;
                _sortedKeys = null;   
            }
            AppLog.Info("dict", $"本地词库已加载，共 {map.Count} 条");
        }
        catch (Exception ex)
        {
            AppLog.Exception("dict", ex, "加载本地词库");
            lock (Gate) _loading = null;   
        }
    }

    private static void ReadInto(string path, Dictionary<string, DictEntry> map)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var entry = LexiconFormat.ParseLine(line);
                if (entry is null) continue;
                map.TryAdd(entry.Word, entry);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("dict", $"读取词库 {Path.GetFileName(path)} 失败：{ex.Message}");
        }
    }

    
    private static string? EnsureExtractedCore()
    {
        try
        {
            var stamp = ShellConfig.ShellVersion;
            var upToDate = File.Exists(CorePath)
                           && File.Exists(StampPath)
                           && string.Equals(File.ReadAllText(StampPath).Trim(), stamp, StringComparison.Ordinal)
                           && new FileInfo(CorePath).Length > 0;

            if (upToDate) return CorePath;

            if (!EmbeddedAssets.ExtractTo("dict-core.tsv", CorePath)) return null;

            File.WriteAllText(StampPath, stamp);
            return CorePath;
        }
        catch (Exception ex)
        {
            AppLog.Exception("dict", ex, "释放内置词库");
            return null;
        }
    }

    
    public static DictEntry? Find(string normalizedWord)
    {
        if (normalizedWord.Length == 0) return null;

        lock (Gate)
        {
            if (_index is null) return null;
            return _index.TryGetValue(normalizedWord, out var entry) ? entry : null;
        }
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    public static IReadOnlyList<string> Suggest(string? prefix, int max = 8)
    {
        var key = (prefix ?? "").Trim();
        if (key.Length == 0 || max <= 0) return Array.Empty<string>();

        string[] keys;
        lock (Gate)
        {
            if (_index is null) return Array.Empty<string>();

            if (_sortedKeys is null)
            {
                var list = new string[_index.Count];
                _index.Keys.CopyTo(list, 0);
                Array.Sort(list, StringComparer.OrdinalIgnoreCase);
                _sortedKeys = list;
            }

            keys = _sortedKeys;
        }

        var lo = LowerBound(keys, key);
        var result = new List<string>(max);
        for (var i = lo; i < keys.Length && result.Count < max; i++)
        {
            if (!keys[i].StartsWith(key, StringComparison.OrdinalIgnoreCase)) break;
            result.Add(keys[i]);
        }

        return result;
    }

    
    private static int LowerBound(string[] sortedKeys, string key)
    {
        var lo = 0;
        var hi = sortedKeys.Length;

        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (string.Compare(sortedKeys[mid], key, StringComparison.OrdinalIgnoreCase) < 0) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    
    
    
    
    
    
    
    
    public static void Remember(DictEntry entry)
    {
        if (entry.IsEmpty || entry.Word.Length == 0) return;

        lock (Gate)
        {
            if (_index is not null && _index.ContainsKey(entry.Word)) return;
        }

        try
        {
            Directory.CreateDirectory(Folder);
            File.AppendAllText(UserPath, LexiconFormat.BuildLine(entry) + Environment.NewLine);

            lock (Gate)
            {
                _index?[entry.Word] = entry;
                _sortedKeys = null;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("dict", "补记用户词库失败：" + ex.Message);
        }
    }
}
