using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;


public sealed record NewlyAddedResult(IReadOnlyList<string> Ids)
{
    public int Count => Ids.Count;
}





























public static class NewlyAddedService
{
    
    private static readonly SemaphoreSlim Gate = new(1, 1);

    
    private static CacheEntry? _memo;

    private static string CacheFile =>
        Path.Combine(ShellConfig.CachedContentDir, ".newlyadded.json");

    
    private static string AppsDirInRepo => ShellConfig.SiteRepoDataDir + "/apps";

    private static string Repo =>
        $"repos/{ShellConfig.SiteRepoOwner}/{ShellConfig.SiteRepoName}";

    
    public static async Task<NewlyAddedResult?> GetRecentAsync(TimeSpan window, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var anchor = DateTime.UtcNow;
            var head = ReadLocalHeadSha();

            
            foreach (var entry in new[] { _memo, ReadCache() })
            {
                if (entry is null) continue;
                if (head.Length > 0 && !string.Equals(entry.Head, head, StringComparison.OrdinalIgnoreCase)) continue;
                if ((anchor - entry.Anchor) > window) continue;   
                _memo = entry;
                return new NewlyAddedResult(entry.Ids);
            }

            
            var headIds = ReadLocalAppIds();
            if (headIds.Count == 0) return null;

            
            var until = anchor.Subtract(window).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            var path = string.Join("/", AppsDirInRepo.Split('/').Select(Uri.EscapeDataString));
            var baseSha = ReadFirstSha(await GithubContentSync.GetApiTextAsync(
                $"{Repo}/commits?until={until}&path={path}&per_page=1", ct).ConfigureAwait(false));

            
            if (baseSha is null) return null;

            
            var (baseIds, truncated) = ReadTreeApps(await GithubContentSync.GetApiTextAsync(
                $"{Repo}/git/trees/{baseSha}?recursive=1", ct).ConfigureAwait(false));

            
            if (truncated)
            {
                AppLog.Warning("ui", "近期新增：基准清单被 GitHub 截断，这次不显示");
                return null;
            }

            var ids = headIds.Where(id => !baseIds.Contains(id))
                             .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                             .ToList();

            var fresh = new CacheEntry(anchor, head, ids);
            _memo = fresh;
            WriteCache(fresh);

            AppLog.Info("ui",
                $"近期新增：{ids.Count} 款（窗口 {window.TotalHours:0} 小时，基准 {(baseSha.Length >= 7 ? baseSha[..7] : baseSha)}，"
                + $"清单 {baseIds.Count} → {headIds.Count}）");
            return new NewlyAddedResult(ids);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            
            AppLog.Warning("ui", "近期新增：算不出来，这次不显示（" + ex.Message + "）");
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    

    
    private static HashSet<string> ReadLocalAppIds()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var dir = Path.Combine(ShellConfig.CachedContentDir, "apps");
            if (!Directory.Exists(dir)) return set;

            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.StartsWith('_')) continue;
                set.Add(name);
            }
        }
        catch {  }
        return set;
    }

    private static string? ReadFirstSha(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            foreach (var item in doc.RootElement.EnumerateArray())
                if (item.TryGetProperty("sha", out var sha)
                    && sha.GetString() is { Length: > 0 } value)
                    return value;
        }
        catch {  }
        return null;
    }

    
    private static (HashSet<string> Ids, bool Truncated) ReadTreeApps(string json)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var truncated = false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            truncated = root.TryGetProperty("truncated", out var tr) && tr.ValueKind == JsonValueKind.True;

            if (!root.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Array)
                return (set, truncated);

            var prefix = AppsDirInRepo + "/";

            foreach (var item in tree.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var type) && type.GetString() != "blob") continue;

                var full = item.TryGetProperty("path", out var p) ? p.GetString() : null;
                if (full is null) continue;
                if (!full.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (!full.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

                var bare = full[(full.LastIndexOf('/') + 1)..];
                if (bare.StartsWith('_')) continue;

                set.Add(Path.GetFileNameWithoutExtension(bare));
            }
        }
        catch {  }

        return (set, truncated);
    }

    

    private sealed record CacheEntry(DateTime Anchor, string Head, List<string> Ids);

    private static CacheEntry? ReadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(CacheFile));
            var root = doc.RootElement;

            if (!root.TryGetProperty("anchor", out var a)
                || !DateTime.TryParse(a.GetString(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var anchor))
                return null;
            var head = root.TryGetProperty("head", out var h) ? h.GetString() : "";

            var ids = new List<string>();
            if (root.TryGetProperty("ids", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray())
                    if (item.GetString() is { } s) ids.Add(s);

            return new CacheEntry(anchor, head ?? "", ids);
        }
        catch { return null; }
    }

    private static void WriteCache(CacheEntry entry)
    {
        try
        {
            Directory.CreateDirectory(ShellConfig.CachedContentDir);
            var json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["anchor"] = entry.Anchor.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                ["head"] = entry.Head,
                ["ids"] = entry.Ids,
            });
            File.WriteAllText(CacheFile, json);
        }
        catch {  }
    }

    
    private static string ReadLocalHeadSha()
    {
        try
        {
            var stamp = Path.Combine(ShellConfig.CachedContentDir, ".reposha");
            return File.Exists(stamp) ? File.ReadAllText(stamp).Trim() : "";
        }
        catch { return ""; }
    }
}
