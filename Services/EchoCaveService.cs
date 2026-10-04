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

    /// <summary>
    /// 目录清单入口（{0}=owner {1}=repo {2}=分支 {3}=转义后的目录路径）。
    /// <see cref="ContentsEntry"/> 那一项的 HTTP 404 / GitHub 错误体都算**权威答案**（＝目录里没字条），
    /// 其余入口只当"有就用"，解析不出就继续往下试 —— 判据见 <see cref="ListAsync"/>。
    /// </summary>
    private static readonly string[] ListTemplates =
    {
        // ① gh-proxy 代理的 GitHub contents：一次拿到该目录的**权威**清单，空目录也如实返回空数组。
        //    走镜像转发，不消耗本机的 GitHub 匿名配额。
        "https://gh-proxy.com/https://api.github.com/repos/{0}/{1}/contents/{3}?ref={2}",
        // ② jsDelivr 的文件清单：匿名、无配额，但它的**包索引可能整个目录漏掉**
        //    （2026-10-03 实测：仓库里的 `回声洞/` 不在清单里，而同一路径走 CDN 直读是 200）
        //    ⇒ 只当补充，解析不出字条就继续往下走。
        "https://data.jsdelivr.com/v1/packages/gh/{0}/{1}@{2}?structure=flat",
    };

    /// <summary><see cref="ListTemplates"/> 里"权威"的那一项（它说没有就是真没有）。</summary>
    private const int ContentsEntry = 0;

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

    /// <summary>
    /// 投稿一条字条 —— **走「提交软件」同一套自建服务**（令牌在服务端，客户端只发内容）：
    /// <c>POST {入口}/api/echocave</c>，请求体 <c>{ "text": "…" }</c>，
    /// 服务端负责落一份 <c>submissions/echo-*.json</c> 草稿 + 开审核 Issue，
    /// 审核通过后由工作流收进 <c>回声洞/messages/messageN.json</c>。
    ///
    /// 入口顺序、超时、出错体解析都在 <see cref="SubmitClient"/> 里，三条路共用。
    /// 不抛异常：连不上 / 服务端还没这条路由，都返回 (false, 人话)，界面上照说照显示。
    /// </summary>
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
            return new EchoCaveResult(true, _memory, "memory", Summarize(_memory, ""));

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (listed, paths) = await ListAsync(ct).ConfigureAwait(false);
            if (!listed)
            {
                // 列目录的入口全不通 → 干脆不靠"列目录"，按编号把 message1…N 直接探一遍
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
                // 取数链路是通的，只是这条目录里眼下没有字条（或 CDN / jsDelivr 还没刷出来）。
                // 手上还有上次的内容就先接着显示 —— 别让卡片在某次刷新后突然空掉。
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

            // 网络不通 → 退回本机缓存
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

    /// <summary>
    /// 卡面那行小字。<paramref name="suffix"/> 只在有额外信息时给（例如走本机缓存），
    /// 正常从网上取到时就是干干净净一句「共 N 条」。
    /// </summary>
    private static string Summarize(IReadOnlyList<EchoMessage> list, string suffix)
    {
        if (list.Count == 0) return suffix.Length > 0 ? $"暂时还没人说话（{suffix}）" : "暂时还没人说话";
        return suffix.Length > 0 ? $"共 {list.Count} 条　·　{suffix}" : $"共 {list.Count} 条";
    }

    // ── 列目录（带入口回退） ─────────────────────────────────────────

    private static string Escape(string path)
        => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    /// <summary>
    /// 逐个入口试拿「字条文件清单」（按编号排好）。
    /// <c>Listed=false</c> ＝ **所有入口都没答话**（网络问题）；
    /// <c>Listed=true</c> 且清单为空 ＝ **链路是通的、但目录里确实没有字条**。
    /// 这两种必须分开，否则"目录空"会被当成"请检查网络"报给用户（2026-10-03 修）。
    /// </summary>
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
                if (paths is null) continue;   // 响应不是一份合格清单（HTML / 限流提示）→ 换下一个入口

                // 权威入口的空数组就是答案；其余入口只说明"它那儿没有"，继续往下问
                if (paths.Count == 0 && index != ContentsEntry) continue;

                Remember(index, ListBaseFile);
                return (true, paths);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // 权威入口 404 ＝ 目录还没建，这也是答案（＝里面没有字条）
                if (index == ContentsEntry)
                {
                    Remember(index, ListBaseFile);
                    return (true, new List<string>());
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // 是外面要取消，不是单个入口超时
            }
            catch
            {
                // 单入口超时 / 连不上 / 内容不是文本 → 换下一个入口
            }
        }
        return (false, new List<string>());
    }

    /// <summary>
    /// 末路兜底：完全不依赖"列目录"服务，直接按编号探
    /// <c>message1.json</c>、<c>message2.json</c>……（编号是连续的，中途不会断档）。
    /// 一批并发探 8 个，某一批**一个都没有**就认为到末尾了。
    /// <c>Reached=false</c> ＝ 连"文件不存在"这种答复都拿不到 ⇒ 网络不通（而不是没数据）。
    /// </summary>
    private static async Task<(bool Reached, List<string> Paths)> ProbeAsync(CancellationToken ct)
    {
        const int BatchSize = 8;     // 一批并发探几个编号
        const int MaxNumber = 400;   // 防呆上限：探到这儿还没到末尾就不再往下探

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

            if (hits.Count == 0) break;   // 这一批一个都没探到 ⇒ 后面不会再有更靠后的编号
        }

        return (reached, found.OrderBy(f => f.Number).Select(f => f.Path).ToList());
    }

    /// <summary>
    /// 把两种清单格式都解成「字条文件路径」并排序。
    /// 返回 <c>null</c> ＝ 这份响应压根不是清单（HTML 错误页、限流提示等），外层应换下一个入口；
    /// 返回空表 ＝ 确实是一份"里面没有字条"的清单（只有权威入口允许这么答）。
    /// </summary>
    private static List<string>? ParseListing(string text, int entry)
    {
        var paths = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (entry == ContentsEntry)
            {
                // GitHub contents 的错误体：{ "message": "Not Found", ... } ⇒ 目录不存在＝没有字条
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("message", out _))
                    return paths;

                // 正常：[ { "name": "message1.json", "path": "...", "type": "file" }, ... ]
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
                // jsDelivr：{ "files": [ { "name": "/回声洞/messages/message1.json" }, ... ] }
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
                    if (leaf.Length == 0 || leaf.Contains('/')) continue;   // 只要目录下的文件
                    if (IsMessageFile(leaf)) paths.Add(name);
                }
            }
        }
        catch { return null; }   // 语法坏了 ＝ 不是清单

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
        => (await FetchExAsync(path, ct).ConfigureAwait(false)).Text;

    /// <summary>
    /// 逐个入口试一个文件。<c>Answered</c> ＝ 是否**得到了服务器明确的答复**
    /// （拿到了内容，或明确的 404 —— 两者都说明网络这一段是通的），<c>Text</c> ＝ 文件内容。
    /// 编号探测靠 <c>Answered</c> 把"目录里没有这条"和"网断了"分开。
    /// </summary>
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
                return (true, "");   // 服务器答话了、但这条不存在 —— 网络是通的
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
        return (false, "");
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
