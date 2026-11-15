using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;



























public static class LegalTextSource
{
    
    public static string CachePath => Path.Combine(AppPaths.DataDir, "legal", "legal.json");

    
    
    
    public static IReadOnlyList<LegalDoc> All
    {
        get
        {
            EnsureLoaded();
            lock (Gate) return _all;
        }
    }

    
    public static LegalDoc? Find(string? key)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(key)) return null;

        lock (Gate)
        {
            return _all.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
        }
    }

    
    public static bool IsBuiltIn
    {
        get
        {
            EnsureLoaded();
            lock (Gate) return _origin == OriginBuiltIn;
        }
    }

    
    
    
    
    
    
    
    public static async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        EnsureLoaded();

        lock (Gate)
        {
            
            if (_refreshing) return false;
            _refreshing = true;
        }

        try
        {
            foreach (var url in Endpoints())
            {
                if (ct.IsCancellationRequested) return false;

                string json;
                try
                {
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    budget.CancelAfter(TimeSpan.FromSeconds(6));
                    json = await Http.GetStringAsync(url, budget.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    AppLog.Warning("legal", $"{HostOf(url)} 超时（6 秒），换下一条线路");
                    continue;
                }
                catch (Exception ex)
                {
                    AppLog.Warning("legal", $"{HostOf(url)} 取不到：{ex.Message}");
                    continue;
                }

                if (!TryParse(json, out var docs, out var generated))
                {
                    AppLog.Warning("legal", $"{HostOf(url)} 返回的内容不是可用的条款 json，已忽略");
                    continue;
                }

                bool newest;
                lock (Gate)
                {
                    
                    
                    
                    if (generated == _generated)
                    {
                        AppLog.Info("legal", $"条款已是最新（站点生成于 {generated:yyyy-MM-dd HH:mm}）");
                        return false;
                    }

                    if (generated < _generated) newest = false;   
                    else
                    {
                        _all = docs;
                        _generated = generated;
                        _origin = "在线";
                        newest = true;
                    }
                }

                if (!newest) continue;

                SaveCache(json);
                AppLog.Info("legal", $"条款已从 {HostOf(url)} 更新，站点生成时间 {generated:yyyy-MM-dd HH:mm}");
                return true;
            }

            AppLog.Info("legal", "本次没取到条款，继续用手上这份");
            return false;
        }
        finally
        {
            lock (Gate) _refreshing = false;
        }
    }

    

    private const string OriginBuiltIn = "程序内置";

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private static IEnumerable<string> Endpoints()
    {
        var owner = ShellConfig.SiteRepoOwner;
        var repo = ShellConfig.SiteRepoName;
        var branch = ShellConfig.SiteRepoBranch;
        var rawPath = $"{owner}/{repo}/{branch}/public/legal.json";

        yield return ShellConfig.SiteUrl + "/legal.json";
        yield return "https://classsoftwarehub.cn/legal.json";
        yield return $"https://fastly.jsdelivr.net/gh/{owner}/{repo}@{branch}/public/legal.json";
        yield return $"https://cdn.jsdelivr.net/gh/{owner}/{repo}@{branch}/public/legal.json";
        yield return "https://gh-proxy.com/https://raw.githubusercontent.com/" + rawPath;
        yield return "https://raw.githubusercontent.com/" + rawPath;
    }

    
    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    

    private static readonly object Gate = new();

    
    
    
    
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static IReadOnlyList<LegalDoc> _all = LegalText.All;
    private static DateTimeOffset _generated = DateTimeOffset.MinValue;
    private static string _origin = OriginBuiltIn;
    private static bool _loaded;
    private static bool _refreshing;

    
    private static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                if (!File.Exists(CachePath)) return;

                var text = File.ReadAllText(CachePath);
                if (!TryParse(text, out var docs, out var generated))
                {
                    
                    AppLog.Warning("legal", "本地条款缓存解析失败，已删除并改用内置版本");
                    try { File.Delete(CachePath); } catch {  }
                    return;
                }

                _all = docs;
                _generated = generated;
                _origin = "本地缓存";
                AppLog.Info("legal", $"条款缓存已载入（{docs.Count} 份，站点生成于 {generated:yyyy-MM-dd HH:mm}）");
            }
            catch (Exception ex)
            {
                AppLog.Warning("legal", "读本地条款缓存失败：" + ex.Message);
            }
        }
    }

    private static void SaveCache(string json)
    {
        try
        {
            var dir = Path.GetDirectoryName(CachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            
            var tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, CachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warning("legal", "写条款缓存失败：" + ex.Message);
        }
    }

    

    
    
    
    
    
    
    
    
    
    private static bool TryParse(string json, out IReadOnlyList<LegalDoc> docs, out DateTimeOffset generated)
    {
        docs = Array.Empty<LegalDoc>();
        generated = default;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("docs", out var arr) || arr.ValueKind != JsonValueKind.Array) return false;

            var list = new List<LegalDoc>();
            foreach (var item in arr.EnumerateArray())
            {
                var one = ReadDoc(item);
                if (one is not null) list.Add(one);
            }

            if (list.Count == 0) return false;

            
            foreach (var expected in LegalText.All)
            {
                if (!list.Any(d => string.Equals(d.Key, expected.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    AppLog.Warning("legal", $"条款 json 里缺少「{expected.Title}」，整份丢弃");
                    return false;
                }
            }

            if (root.TryGetProperty("generated", out var g) && g.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(g.GetString(), out var when))
            {
                generated = when;
            }

            docs = list;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static LegalDoc? ReadDoc(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;

        var key = Str(e, "key");
        if (key.Length == 0) return null;

        var sections = new List<LegalSection>();
        if (e.TryGetProperty("sections", out var secs) && secs.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in secs.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;

                var items = new List<string>();
                if (s.TryGetProperty("items", out var its) && its.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in its.EnumerateArray())
                    {
                        if (t.ValueKind == JsonValueKind.String) items.Add(t.GetString() ?? "");
                    }
                }

                sections.Add(new LegalSection { Heading = Str(s, "heading"), Items = items });
            }
        }

        
        if (sections.Count == 0) return null;

        return new LegalDoc
        {
            Key = key,
            Version = Str(e, "version"),
            Effective = Str(e, "effective"),
            Title = Str(e, "title"),
            Intro = Str(e, "intro"),
            Sections = sections,
        };
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "")
            : "";
}
