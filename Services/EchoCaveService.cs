using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services.License;

namespace ClassSoftwareHub.Desktop.Services;


public sealed class EchoMessage
{
    public string Text { get; init; } = "";
}





public sealed record EchoCaveResult(
    bool Ok,
    IReadOnlyList<EchoMessage> Messages,
    string Source,
    string Message);

















public static class EchoCaveService
{
    
    private const string RepoDir = "回声洞/messages";

    
    private const string FilePrefix = "message";

    
    private static readonly string[] RawTemplates =
    {
        "https://raw.githubusercontent.com/{0}/{1}/{2}/{3}",
        "https://cdn.jsdelivr.net/gh/{0}/{1}@{2}/{3}",
        "https://fastly.jsdelivr.net/gh/{0}/{1}@{2}/{3}",
        "https://gh-proxy.com/https://raw.githubusercontent.com/{0}/{1}/{2}/{3}",
    };

    
    
    
    
    
    private static readonly string[] ListTemplates =
    {
        
        
        "https://gh-proxy.com/https://api.github.com/repos/{0}/{1}/contents/{3}?ref={2}",
        
        
        
        "https://data.jsdelivr.com/v1/packages/gh/{0}/{1}@{2}?structure=flat",
    };

    
    private const int ContentsEntry = 0;

    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    
    private const int ReadConcurrency = 6;

    
    private static List<EchoMessage>? _memory;
    private static DateTime _memoryAt = DateTime.MinValue;
    private static readonly TimeSpan MemoryTtl = TimeSpan.FromMinutes(10);

    
    private static readonly TimeSpan PerEntryTimeout = TimeSpan.FromSeconds(6);

    
    public static string RepoUrl => $"https://github.com/{ShellConfig.SiteRepoOwner}/{ShellConfig.SiteRepoName}";

    
    public static string SubmitUrl =>
        $"{RepoUrl}/new/{ShellConfig.SiteRepoBranch}/{Escape(RepoDir)}";

    
    
    
    
    
    
    
    
    
    public static async Task<(bool Ok, string Message)> SubmitAsync(string text, CancellationToken ct = default)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return (false, "还没写内容。");

        var reply = await SubmitClient
            .PostAsync("/api/echocave", JsonSerializer.Serialize(new { text }),
                "已提交，审核通过后就会出现在回声洞里。", ct)
            .ConfigureAwait(false);

        return reply is null
            ? (false, "连不上投稿服务（网络或地区限制），可以改用下方入口在 GitHub 网页上投稿。")
            : (reply.Ok, reply.Message);
    }

    
    private static string CacheFile => Path.Combine(AppPaths.DataDir, "echo-cave.json");

    
    private static string RawBaseFile => Path.Combine(AppPaths.DataDir, "echo-cave-base.txt");

    
    private static string ListBaseFile => Path.Combine(AppPaths.DataDir, "echo-cave-list-base.txt");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new AuthHandler(new HttpClientHandler { AllowAutoRedirect = true }))
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClassSoftwareHub/1.2 echo-cave");
        return client;
    }

    
    
    
    
    public static async Task<EchoCaveResult> LoadAsync(bool force, CancellationToken ct = default)
    {
        if (!force && _memory is not null && DateTime.UtcNow - _memoryAt < MemoryTtl)
            return new EchoCaveResult(true, _memory, "memory", Summarize(_memory, ""));

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (listed, paths) = await ListAsync(ct).ConfigureAwait(false);
            if (!listed)
            {
                
                (listed, paths) = await ProbeAsync(ct).ConfigureAwait(false);
            }

            if (paths.Count > 0)
            {
                var list = await FetchAllAsync(paths, ct).ConfigureAwait(false);
                if (list.Count > 0)
                {
                    _memory = list;
                    _memoryAt = DateTime.UtcNow;
                    TrySaveCache(list);
                    return new EchoCaveResult(true, list, "network", Summarize(list, ""));
                }
            }

            var cached = TryLoadCache();

            if (listed)
            {
                
                
                if (cached.Count > 0)
                {
                    _memory = cached;
                    _memoryAt = DateTime.UtcNow;
                    return new EchoCaveResult(false, cached, "cache",
                        "还没取到最新的字条，先显示上次取到的内容。");
                }

                return new EchoCaveResult(true, Array.Empty<EchoMessage>(), "",
                    "回声洞里还没有字条。");
            }

            
            if (cached.Count > 0)
            {
                _memory = cached;
                _memoryAt = DateTime.UtcNow;   
                return new EchoCaveResult(false, cached, "cache",
                    "暂时无法连接 GitHub，正在显示上次取到的内容。");
            }

            return new EchoCaveResult(false, Array.Empty<EchoMessage>(), "",
                "暂时无法连接 GitHub，请检查网络后重试。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new EchoCaveResult(false, Array.Empty<EchoMessage>(), "",
                "读取失败：" + ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    
    
    
    
    private static string Summarize(IReadOnlyList<EchoMessage> list, string suffix)
    {
        if (list.Count == 0) return suffix.Length > 0 ? $"暂时还没人说话（{suffix}）" : "暂时还没人说话";
        return suffix.Length > 0 ? $"共 {list.Count} 条　·　{suffix}" : $"共 {list.Count} 条";
    }

    

    private static string Escape(string path)
        => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    
    
    
    
    
    
    private static async Task<(bool Listed, List<string> Paths)> ListAsync(CancellationToken ct)
    {
        var escapedDir = Escape(RepoDir);
        foreach (var index in PreferredOrder(ListTemplates.Length, ListBaseFile))
        {
            var url = string.Format(ListTemplates[index],
                ShellConfig.SiteRepoOwner, ShellConfig.SiteRepoName, ShellConfig.SiteRepoBranch, escapedDir);
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(PerEntryTimeout);

                var text = await Http.GetStringAsync(url, budget.Token).ConfigureAwait(false);
                if (text.Trim().Length == 0) continue;

                var paths = ParseListing(text, index);
                if (paths is null) continue;   

                
                if (paths.Count == 0 && index != ContentsEntry) continue;

                Remember(index, ListBaseFile);
                return (true, paths);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                
                if (index == ContentsEntry)
                {
                    Remember(index, ListBaseFile);
                    return (true, new List<string>());
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   
            }
            catch
            {
                
            }
        }
        return (false, new List<string>());
    }

    
    
    
    
    
    
    private static async Task<(bool Reached, List<string> Paths)> ProbeAsync(CancellationToken ct)
    {
        const int BatchSize = 8;     
        const int MaxNumber = 400;   

        var found = new List<(int Number, string Path)>();
        var reached = false;

        for (var start = 1; start <= MaxNumber; start += BatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = Enumerable.Range(start, BatchSize).ToArray();
            var results = await Task.WhenAll(batch.Select(async number =>
            {
                var path = $"{RepoDir}/{FilePrefix}{number}.json";
                var (answered, text) = await FetchExAsync(path, ct).ConfigureAwait(false);
                return (Number: number, Answered: answered, Text: text, Path: path);
            })).ConfigureAwait(false);

            if (results.Any(r => r.Answered)) reached = true;

            var hits = results.Where(r => r.Text.Length > 0).ToList();
            foreach (var hit in hits) found.Add((hit.Number, hit.Path));

            if (hits.Count == 0) break;   
        }

        return (reached, found.OrderBy(f => f.Number).Select(f => f.Path).ToList());
    }

    
    
    
    
    
    private static List<string>? ParseListing(string text, int entry)
    {
        var paths = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (entry == ContentsEntry)
            {
                
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("message", out _))
                    return paths;

                
                if (root.ValueKind != JsonValueKind.Array) return null;
                foreach (var file in root.EnumerateArray())
                {
                    if (file.ValueKind != JsonValueKind.Object) continue;
                    if (Str(file, "type") != "file") continue;
                    if (!IsMessageFile(Str(file, "name"))) continue;
                    var path = Str(file, "path");
                    if (path.Length > 0) paths.Add(path);
                }
            }
            else
            {
                
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("files", out var files)
                    || files.ValueKind != JsonValueKind.Array)
                    return null;

                var prefix = RepoDir + "/";
                foreach (var file in files.EnumerateArray())
                {
                    if (file.ValueKind != JsonValueKind.Object) continue;
                    var name = Str(file, "name").TrimStart('/');
                    if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var leaf = name[prefix.Length..];
                    if (leaf.Length == 0 || leaf.Contains('/')) continue;   
                    if (IsMessageFile(leaf)) paths.Add(name);
                }
            }
        }
        catch { return null; }   

        return paths.OrderBy(NumberOf).ToList();
    }

    private static bool IsMessageFile(string name)
        => name.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)
           && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    
    private static int NumberOf(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var digits = new string(name.Where(char.IsDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits, out var number) ? number : int.MaxValue;
    }

    

    private static async Task<List<EchoMessage>> FetchAllAsync(List<string> paths, CancellationToken ct)
    {
        var slots = new EchoMessage?[paths.Count];
        var cursor = -1;
        var workers = Math.Min(ReadConcurrency, paths.Count);

        var tasks = new List<Task>(workers);
        for (var i = 0; i < workers; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                while (true)
                {
                    var index = Interlocked.Increment(ref cursor);
                    if (index >= paths.Count) return;

                    var text = await FetchAsync(paths[index], ct).ConfigureAwait(false);
                    if (text.Length == 0) continue;
                    var message = ParseOne(text);
                    if (message is not null) slots[index] = message;
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        
        return slots.Where(m => m is not null).Select(m => m!).ToList();
    }

    
    private static async Task<string> FetchAsync(string path, CancellationToken ct)
        => (await FetchExAsync(path, ct).ConfigureAwait(false)).Text;

    
    
    
    
    
    private static async Task<(bool Answered, string Text)> FetchExAsync(string path, CancellationToken ct)
    {
        var escaped = Escape(path);
        foreach (var index in PreferredOrder(RawTemplates.Length, RawBaseFile))
        {
            var url = string.Format(RawTemplates[index],
                ShellConfig.SiteRepoOwner, ShellConfig.SiteRepoName, ShellConfig.SiteRepoBranch, escaped);
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(PerEntryTimeout);

                var text = await Http.GetStringAsync(url, budget.Token).ConfigureAwait(false);
                if (text.Trim().Length == 0) continue;
                Remember(index, RawBaseFile);
                return (true, text);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return (true, "");   
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                
            }
        }
        return (false, "");
    }

    
    private static EchoMessage? ParseOne(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var content = Str(doc.RootElement, "text");
            return content.Length > 0 ? new EchoMessage { Text = content } : null;
        }
        catch { return null; }
    }

    

    
    private static IEnumerable<int> PreferredOrder(int count, string baseFile)
    {
        var remembered = LoadRemembered(baseFile);
        var list = new List<int>(count);
        if (remembered >= 0 && remembered < count) list.Add(remembered);
        for (var i = 0; i < count; i++) if (i != remembered) list.Add(i);
        return list;
    }

    private static int LoadRemembered(string baseFile)
    {
        try
        {
            if (!File.Exists(baseFile)) return -1;
            return int.TryParse(File.ReadAllText(baseFile).Trim(), out var index) ? index : -1;
        }
        catch { return -1; }
    }

    private static void Remember(int index, string baseFile)
    {
        try
        {
            if (LoadRemembered(baseFile) == index) return;
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(baseFile, index.ToString(), Utf8NoBom);
        }
        catch {  }
    }

    

    private static string Str(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value)) return "";
        return value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "").Trim() : "";
    }

    private static void TrySaveCache(IReadOnlyList<EchoMessage> list)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var json = JsonSerializer.Serialize(list.Select(m => new { text = m.Text }));
            File.WriteAllText(CacheFile, json, Utf8NoBom);
        }
        catch {  }
    }

    private static List<EchoMessage> TryLoadCache()
    {
        var list = new List<EchoMessage>();
        try
        {
            if (!File.Exists(CacheFile)) return list;
            using var doc = JsonDocument.Parse(File.ReadAllText(CacheFile));
            var root = doc.RootElement;

            
            
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("messages", out var older) && older.ValueKind == JsonValueKind.Array)
                    root = older;
                else
                    return list;
            }
            if (root.ValueKind != JsonValueKind.Array) return list;

            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var content = Str(item, "text");
                if (content.Length > 0) list.Add(new EchoMessage { Text = content });
            }
        }
        catch {  }
        return list;
    }
}
