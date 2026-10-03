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

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>回声洞里的一条字条（只有内容，不带发言人 / 群 / 日期）。</summary>
public sealed class EchoMessage
{
    public string Text { get; init; } = "";
}

/// <summary>
/// 读取结果。<see cref="Source"/>：<c>network</c>（刚从 GitHub 取到）/
/// <c>cache</c>（网络不通，用的本机缓存）/ <c>memory</c>（本次会话内已取过）/ 空串（啥都没有）。
/// </summary>
public sealed record EchoCaveResult(
    bool Ok,
    IReadOnlyList<EchoMessage> Messages,
    string Source,
    string Message);

/// <summary>
/// 「回声洞」数据源：站点仓库根目录的 <c>回声洞/messages/</c> 目录，**一条一个文件**
/// （<c>message1.json</c>、<c>message2.json</c>……），文件里只有 <c>{ "text": "..." }</c>
/// （与网页版、CSHcontroller 控制台读的是同一套文件）。
///
/// 两段式取数：
///   · **列目录** —— 首选 <c>data.jsdelivr.com</c> 的文件清单接口（匿名、一次拿全仓库文件表、
///     ⛔ 不耗 GitHub 配额）；兜底走 gh-proxy 包一层 api.github.com 的 contents 接口。
///   · **读内容** —— 每个文件走 raw → jsDelivr → fastly → gh-proxy **逐个回退**
///     （raw.githubusercontent.com 在国内 / 校园网经常不通，本机 hosts 就把它掐了），
///     6 路并发，别把镜像打爆。
///
/// ⛔ **不直接碰 api.github.com** —— 那边有 60 次/小时的匿名配额，读几十条小字条不值当。
///
/// 取不到网络时的降级顺序：内存缓存 → 本机磁盘缓存 → 空列表 + 人话提示。
/// </summary>
public static class EchoCaveService
{
    /// <summary>仓库里字条所在目录（⛔ 与网页版、控制台读的是同一套文件，别改）。</summary>
    private const string RepoDir = "回声洞/messages";

    /// <summary>字条文件名的前缀（message1.json、message2.json……）。</summary>
    private const string FilePrefix = "message";

    /// <summary>文件内容入口（{0}=owner {1}=repo {2}=分支 {3}=转义后的路径）。</summary>
    private static readonly string[] RawTemplates =
    {
        "https://raw.githubusercontent.com/{0}/{1}/{2}/{3}",
        "https://cdn.jsdelivr.net/gh/{0}/{1}@{2}/{3}",
        "https://fastly.jsdelivr.net/gh/{0}/{1}@{2}/{3}",
        "https://gh-proxy.com/https://raw.githubusercontent.com/{0}/{1}/{2}/{3}",
    };

    /// <summary>目录清单入口（{0}=owner {1}=repo {2}=分支 {3}=转义后的目录路径）。</summary>
    private static readonly string[] ListTemplates =
    {
        // ① jsDelivr 的文件清单：匿名、无 GitHub 配额、一次拿到全仓库文件表
        "https://data.jsdelivr.com/v1/packages/gh/{0}/{1}@{2}?structure=flat",
        // ② 兜底：gh-proxy 代理的 GitHub contents 接口（走镜像，不直连本尊）
        "https://gh-proxy.com/https://api.github.com/repos/{0}/{1}/contents/{3}?ref={2}",
    };

    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>同时最多读几个字条文件。字条都很小，6 路足够快又不至于被镜像限流。</summary>
    private const int ReadConcurrency = 6;

    /// <summary>本次会话里取到的内容（短时间内进页面不再重复请求）。</summary>
    private static List<EchoMessage>? _memory;
    private static DateTime _memoryAt = DateTime.MinValue;
    private static readonly TimeSpan MemoryTtl = TimeSpan.FromMinutes(10);

    /// <summary>单个入口最多等多久 —— 某个镜像被墙时不必干等到 HttpClient 的 20 秒上限。</summary>
    private static readonly TimeSpan PerEntryTimeout = TimeSpan.FromSeconds(6);

    /// <summary>仓库地址（界面显示 / 排查用）。</summary>
    public static string RepoUrl => $"https://github.com/{ShellConfig.SiteRepoOwner}/{ShellConfig.SiteRepoName}";

    /// <summary>投稿入口：GitHub 上给字条目录「新建文件」（与网页版指向同一处）。</summary>
    public static string SubmitUrl =>
        $"{RepoUrl}/new/{ShellConfig.SiteRepoBranch}/{Escape(RepoDir)}";

    /// <summary>本机缓存（网络不通时显示上次取到的内容）。</summary>
    private static string CacheFile => Path.Combine(AppPaths.DataDir, "echo-cave.json");

    /// <summary>上次成功的「文件内容」入口索引。</summary>
    private static string RawBaseFile => Path.Combine(AppPaths.DataDir, "echo-cave-base.txt");

    /// <summary>上次成功的「目录清单」入口索引。</summary>
    private static string ListBaseFile => Path.Combine(AppPaths.DataDir, "echo-cave-list-base.txt");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClassSoftwareHub/1.2 echo-cave");
        return client;
    }

    /// <summary>
    /// 读取回声洞内容。**不抛异常**（取消失常除外），失败也返回可用结果 + 人话说明。
    /// </summary>
    /// <param name="force">true=忽略内存缓存，一定去网上拉一次（界面上的「刷新」）。</param>
    public static async Task<EchoCaveResult> LoadAsync(bool force, CancellationToken ct = default)
    {
        if (!force && _memory is not null && DateTime.UtcNow - _memoryAt < MemoryTtl)
            return new EchoCaveResult(true, _memory, "memory", Summarize(_memory, "本机缓存"));

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var paths = await ListAsync(ct).ConfigureAwait(false);
            if (paths.Count > 0)
            {
                var list = await FetchAllAsync(paths, ct).ConfigureAwait(false);
                if (list.Count > 0)
                {
                    _memory = list;
                    _memoryAt = DateTime.UtcNow;
                    TrySaveCache(list);
                    return new EchoCaveResult(true, list, "network", Summarize(list, "已同步"));
                }
            }

            // 网络不通 → 退回本机缓存
            var cached = TryLoadCache();
            if (cached.Count > 0)
            {
                _memory = cached;
                _memoryAt = DateTime.UtcNow;   // 短时间内不再反复重试网络
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
        => list.Count == 0 ? $"暂时还没人说话（{suffix}）" : $"共 {list.Count} 条　·　{suffix}";

    // ── 列目录（带入口回退） ─────────────────────────────────────────

    private static string Escape(string path)
        => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    /// <summary>逐个入口试，返回字条文件的路径清单（按编号排好）；全不通返回空表。</summary>
    private static async Task<List<string>> ListAsync(CancellationToken ct)
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
                if (paths.Count == 0) continue;   // 入口通但目录为空/格式不对 → 换下一个再确认

                Remember(index, ListBaseFile);
                return paths;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // 是外面要取消，不是单个入口超时
            }
            catch
            {
                // 单入口超时 / 连不上 / 404 / 内容不是文本 → 换下一个入口
            }
        }
        return new List<string>();
    }

    /// <summary>把两种清单格式都解成「字条文件路径」并排序。</summary>
    private static List<string> ParseListing(string text, int entry)
    {
        var paths = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (entry == 0)
            {
                // jsDelivr：{ "files": [ { "name": "/回声洞/messages/message1.json" }, ... ] }
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("files", out var files)
                    || files.ValueKind != JsonValueKind.Array)
                    return paths;

                var prefix = RepoDir + "/";
                foreach (var file in files.EnumerateArray())
                {
                    if (file.ValueKind != JsonValueKind.Object) continue;
                    var name = Str(file, "name").TrimStart('/');
                    if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var leaf = name[prefix.Length..];
                    if (leaf.Length == 0 || leaf.Contains('/')) continue;   // 只要目录下的文件
                    if (IsMessageFile(leaf)) paths.Add(name);
                }
            }
            else
            {
                // GitHub contents：[ { "name": "message1.json", "path": "...", "type": "file" }, ... ]
                if (root.ValueKind != JsonValueKind.Array) return paths;
                foreach (var file in root.EnumerateArray())
                {
                    if (file.ValueKind != JsonValueKind.Object) continue;
                    if (Str(file, "type") != "file") continue;
                    if (!IsMessageFile(Str(file, "name"))) continue;
                    var path = Str(file, "path");
                    if (path.Length > 0) paths.Add(path);
                }
            }
        }
        catch { /* 语法坏了就当没有 */ }

        return paths.OrderBy(NumberOf).ToList();
    }

    private static bool IsMessageFile(string name)
        => name.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)
           && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>message12.json → 12（认不出编号的排到最后）。</summary>
    private static int NumberOf(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var digits = new string(name.Where(char.IsDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits, out var number) ? number : int.MaxValue;
    }

    // ── 读内容（并发 + 逐文件入口回退） ──────────────────────────────

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

        // 按文件名编号的顺序收拢（slots 已按排好序的 paths 对齐），读不出来的跳过
        return slots.Where(m => m is not null).Select(m => m!).ToList();
    }

    /// <summary>逐个入口试一个文件，返回第一个拿到的内容；全不通返回空串。</summary>
    private static async Task<string> FetchAsync(string path, CancellationToken ct)
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
                return text;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // 换下一个入口
            }
        }
        return "";
    }

    /// <summary>字条文件只有一个 text 字段。</summary>
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

    // ── 入口记忆 ─────────────────────────────────────────────────────

    /// <summary>优先上次成功的入口，剩下的按原顺序跟上。</summary>
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
        catch { /* 记不住不影响功能 */ }
    }

    // ── 解析 / 缓存 ─────────────────────────────────────────────────

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
        catch { /* 写不进去不影响本次显示 */ }
    }

    private static List<EchoMessage> TryLoadCache()
    {
        var list = new List<EchoMessage>();
        try
        {
            if (!File.Exists(CacheFile)) return list;
            using var doc = JsonDocument.Parse(File.ReadAllText(CacheFile));
            var root = doc.RootElement;

            // 现行格式：[ { "text": "..." }, ... ]
            // 兼容旧格式：{ "messages": [ { "text": "..." }, ... ] }
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
        catch { /* 缓存坏了就当没有 */ }
        return list;
    }
}
