using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Core.Dictionary;

namespace ClassSoftwareHub.Desktop.Services.Dictionary;

/// <summary>
/// 本地词库。两个文件合成一个索引：
///
/// <list type="number">
///   <item><b>dict-core.tsv</b> —— 随应用分发的内置词库。内嵌在程序里，第一次查词时释放到本地。
///         改它就等于改全局词库，只读。</item>
///   <item><b>user.tsv</b> —— 用户词库。在线查到、而内置词库里没有的词，自动补记到这儿。
///         下次断网也能查到同一个词。这是"离线源"真正会长大的那部分。</item>
/// </list>
///
/// <para>
/// ⚠️ 为什么不把词库做成需要下载资源包：几万行的纯文本建索引只要一次顺序扫描，
///    而联网下载一个词库包反而把"离线"这件事的可靠性交给了网络。内置的那份够日常用，
///    生僻词走在线并自动沉淀到用户词库 —— 用久了离线命中率自然上来。
/// </para>
/// <para>
/// ⚠️ 放在 <c>%LOCALAPPDATA%\ClassSoftwareHub\dict\</c>，不放缓存目录：缓存会被「清理缓存」整目录删掉，
///    而用户词库是用户自己攒出来的，不能陪着一起被回收。
/// </para>
/// </summary>
public static class LocalLexicon
{
    private static readonly object Gate = new();
    private static Dictionary<string, DictEntry>? _index;

    /// <summary>排好序的 key，供前缀匹配二分用。<see cref="Remember"/> 加词后置空，下次用到再重建。</summary>
    private static string[]? _sortedKeys;
    private static Task? _loading;

    /// <summary>索引建好没有（没建好就是还没人查过，或者正在建）。</summary>
    public static bool IsReady
    {
        get { lock (Gate) return _index is not null; }
    }

    /// <summary>本地词库里有多少个词条（没加载时返回 0）。</summary>
    public static int Count
    {
        get { lock (Gate) return _index?.Count ?? 0; }
    }

    private static string Folder => Path.Combine(AppPaths.DataDir, "dict");

    private static string CorePath => Path.Combine(Folder, "dict-core.tsv");

    private static string UserPath => Path.Combine(Folder, "user.tsv");

    /// <summary>记录已释放词库的版本，换版本时重解一次。</summary>
    private static string StampPath => Path.Combine(Folder, "dict-core.stamp");

    /// <summary>
    /// 确保词库已加载。多次调用只会真正加载一次；加载失败不抛异常，
    /// 只是 <see cref="IsReady"/> 一直为 false，调用方据此回落到在线源。
    /// </summary>
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

            // 用户词库后读：同一个词以内置词库为准（内置的释义更规范），
            // 除非内置那份压根没收这个词 —— 而那种情况才会被写进用户词库。
            if (File.Exists(UserPath)) ReadInto(UserPath, map);

            lock (Gate)
            {
                _index = map;
                _sortedKeys = null;   // 重建时再排一次，避免一次性把排序算在加载时间里
            }
            AppLog.Info("dict", $"本地词库已加载，共 {map.Count} 条");
        }
        catch (Exception ex)
        {
            AppLog.Exception("dict", ex, "加载本地词库");
            lock (Gate) _loading = null;   // 允许下次再试
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

    /// <summary>内嵌词库释放到本地；没有内嵌资源（或释放失败）返回 null，此时只靠用户词库。</summary>
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

    /// <summary>按词查本地词库。没加载就返回 null（调用方应先 <see cref="EnsureLoadedAsync"/>）。</summary>
    public static DictEntry? Find(string normalizedWord)
    {
        if (normalizedWord.Length == 0) return null;

        lock (Gate)
        {
            if (_index is null) return null;
            return _index.TryGetValue(normalizedWord, out var entry) ? entry : null;
        }
    }

    /// <summary>
    /// 前缀匹配：输入框边打字边给候选词。返回最多 <paramref name="max"/> 个。
    ///
    /// <para>
    /// 为什么另存一份排好序的 key 数组：词库有三万条，每敲一个字符就扫一遍太浪费；
    /// 排序后二分找到起点往前扫，单次只要几十微秒。数组在建索引时顺手排出来，只在
    /// <see cref="Remember"/> 往词库里加词时作废重建。
    /// </para>
    /// <para>
    /// ⚠️ 只用 <see cref="StringComparer.OrdinalIgnoreCase"/> 排序与比较，两边必须是同一个比较器，
    ///    否则二分找出来的区间和实际排序对不上，会漏词。
    /// </para>
    /// </summary>
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

    /// <summary>二分找第一个 ≥ <paramref name="key"/> 的位置（忽略大小写）。</summary>
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

    /// <summary>
    /// 把一次在线查到的词补进用户词库，下次断网也能查到。
    ///
    /// <para>
    /// ⚠️ 只在"本地没有、在线有"时调用。写失败只记日志不影响本次查询 ——
    ///    磁盘满、文件被占用这类事不该让用户看不到刚查到的释义。
    /// </para>
    /// </summary>
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
