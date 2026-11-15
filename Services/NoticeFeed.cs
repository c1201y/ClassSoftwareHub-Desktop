using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;

namespace ClassSoftwareHub.Desktop.Services;























public static class NoticeFeed
{
    
    public const string RepoOwner = "c1201y";
    public const string RepoName = "ClassSoftwareHub-Desktop";

    
    private const string DirName = "公告";

    
    
    
    
    
    
    
    
    private static readonly string[] Templates =
    {
        "https://raw.githubusercontent.com/{0}/{1}/main/{2}",
        "https://cdn.jsdelivr.net/gh/{0}/{1}@main/{2}",
        "https://fastly.jsdelivr.net/gh/{0}/{1}@main/{2}",
        "https://gh-proxy.com/https://raw.githubusercontent.com/{0}/{1}/main/{2}",
    };

    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    
    private static IReadOnlyList<NoticeItem> _raw = Array.Empty<NoticeItem>();

    
    private static DateTimeOffset _lastOk = DateTimeOffset.MinValue;

    
    
    
    private static DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;

    
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(10);

    
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(2);

    
    private static bool ShouldFetch(DateTimeOffset now, bool force)
    {
        if (force) return true;
        if (_lastOk != DateTimeOffset.MinValue && now - _lastOk < MinInterval) return false;
        if (_lastAttempt != DateTimeOffset.MinValue && now - _lastAttempt < RetryInterval) return false;
        return true;
    }

    private static string CacheFile => Path.Combine(AppPaths.DataDir, "notice-cache.json");

    private static readonly JsonSerializerOptions DiskJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    
    
    
    
    
    
    
    
    
    public static async Task<IReadOnlyList<NoticeItem>> LoadAsync(
        string version, bool force = false, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (!ShouldFetch(now, force))
            return Visible(_raw, now);

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            
            now = DateTimeOffset.UtcNow;
            if (!ShouldFetch(now, force))
                return Visible(_raw, now);

            _lastAttempt = now;

            
            
            var files = new List<string>();
            if (version.Length > 0) files.Add(version + ".json");

            
            
            
            var dash = version.IndexOf('-');
            if (dash > 0)
            {
                var major = version[..dash] + ".json";
                if (!files.Contains(major, StringComparer.OrdinalIgnoreCase)) files.Add(major);
            }

            files.Add("all.json");

            var merged = new List<NoticeItem>();
            var anyOk = false;

            foreach (var file in files)
            {
                var text = await FetchAsync(file, ct).ConfigureAwait(false);
                if (text is null) continue;          
                anyOk = true;
                merged.AddRange(NoticeItem.Parse(text));
            }

            if (anyOk)
            {
                _raw = merged;
                _lastOk = DateTimeOffset.UtcNow;
                SaveCache(merged);
                AppLog.Step("notice", $"公告已更新 · {merged.Count} 条（{string.Join(" + ", files)}）");
            }
            else if (_raw.Count == 0)
            {
                
                var disk = LoadCache();
                if (disk.Count > 0)
                {
                    _raw = disk;
                    AppLog.Step("notice", $"公告拉取失败，使用本地缓存 {disk.Count} 条");
                }
            }

            return Visible(_raw, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            return Visible(_raw, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            AppLog.Warn("notice", "公告拉取异常：" + ex.Message);
            return Visible(_raw, DateTimeOffset.UtcNow);
        }
        finally
        {
            Gate.Release();
        }
    }

    
    
    
    
    public static IReadOnlyList<NoticeItem> Visible(IReadOnlyList<NoticeItem> all, DateTimeOffset now)
    {
        if (all.Count == 0) return Array.Empty<NoticeItem>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<NoticeItem>(all.Count);

        foreach (var n in all)
        {
            if (!seen.Add(n.Id)) continue;
            if (n.StartsAt is { } start && now < start) continue;
            if (n.EndsAt is { } end && now >= end) continue;
            list.Add(n);
        }

        return list;
    }

    

    
    private static string BaseFile => Path.Combine(AppPaths.DataDir, "notice-base.txt");

    
    
    
    
    
    
    
    private static IEnumerable<int> Candidates()
    {
        var preferred = ReadPreferredBase();
        if (preferred >= 0)
        {
            yield return preferred;
            for (var i = 0; i < Templates.Length; i++)
                if (i != preferred) yield return i;
            yield break;
        }

        for (var i = 0; i < Templates.Length; i++) yield return i;
    }

    private static int ReadPreferredBase()
    {
        try
        {
            if (!File.Exists(BaseFile)) return -1;
            var text = File.ReadAllText(BaseFile).Trim();
            return int.TryParse(text, out var index) && index >= 0 && index < Templates.Length ? index : -1;
        }
        catch
        {
            return -1;
        }
    }

    
    private static void RememberBase(int index)
    {
        try
        {
            if (ReadPreferredBase() == index) return;
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(BaseFile, index.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            
        }
    }

    
    
    
    
    private static async Task<string?> FetchAsync(string file, CancellationToken ct)
    {
        
        
        var urlPath = Uri.EscapeDataString(DirName) + "/" + Uri.EscapeDataString(file);

        Exception? last = null;
        foreach (var index in Candidates())
        {
            var url = string.Format(Templates[index], RepoOwner, RepoName, urlPath);
            try
            {
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                
                
                if (resp.StatusCode == HttpStatusCode.NotFound) return null;
                if (!resp.IsSuccessStatusCode)
                {
                    last = new HttpRequestException($"HTTP {(int)resp.StatusCode}");
                    continue;
                }

                RememberBase(index);
                return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        if (last is not null)
            AppLog.Warn("notice", $"公告文件 {file} 所有入口均不可用：{last.Message}");
        return null;
    }

    

    
    
    
    
    private static void SaveCache(List<NoticeItem> items)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(CacheFile,
                JsonSerializer.Serialize(items, DiskJson), new UTF8Encoding(false));
        }
        catch
        {
            
        }
    }

    private static List<NoticeItem> LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return new List<NoticeItem>();
            var text = File.ReadAllText(CacheFile);
            return JsonSerializer.Deserialize<List<NoticeItem>>(text, DiskJson) ?? new List<NoticeItem>();
        }
        catch
        {
            return new List<NoticeItem>();
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(6),
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(12),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClassSoftwareHub-Desktop");
        return client;
    }
}
