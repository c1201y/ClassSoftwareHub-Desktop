using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services.Diagnostics;
using ClassSoftwareHub.Desktop.Services.Docs;

namespace ClassSoftwareHub.Desktop.Services.HubAi;


public sealed record SoftwareHit(string Id, string Name, string Tagline, string Category);




public sealed class ToolSession
{
    public List<SoftwareHit> SoftwareHits { get; } = new();
    public List<string> Trace { get; } = new();

    
    public List<AiAction> Actions { get; } = new();

    public void Reset()
    {
        SoftwareHits.Clear();
        Trace.Clear();
        Actions.Clear();
    }
}













public static class ToolRunner
{
    
    
    
    
    
    
    
    
    
    private static readonly TimeSpan DiskBudget = TimeSpan.FromSeconds(25);

    
    
    
    
    
    
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(90);

    
    private const int MaxResultChars = 2400;

    
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.Ordinal)
    {
        "get_machine_snapshot",
        "list_disk_folders",
        "search_software_library",
        "list_installed_apps",
        "list_startup_items",
        "web_search",
        "fetch_url",
        
        "suggest_actions",
        
        "list_documents",
        "read_document",
    };

    
    
    
    
    private static readonly HashSet<string> MutatingTools = new(StringComparer.Ordinal);

    public static bool IsReadOnly(string id) => ReadOnlyTools.Contains(id);

    
    
    
    
    public static bool RequiresApproval(string id) => MutatingTools.Contains(id);

    
    public static bool IsKnown(string id) => ReadOnlyTools.Contains(id) || MutatingTools.Contains(id);

    public static async Task<string> ExecuteAsync(
        string id,
        string argumentsJson,
        ToolSession session,
        CancellationToken ct)
    {
        session.Trace.Add(id);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ToolTimeout);

        try
        {
            var result = id switch
            {
                "get_machine_snapshot" => await GetSnapshotAsync(timeout.Token),
                "list_disk_folders" => await ListDiskFoldersAsync(argumentsJson, timeout.Token),
                "search_software_library" => SearchLibrary(argumentsJson, session),
                "list_installed_apps" => await ListInstalledAsync(argumentsJson, timeout.Token),
                "list_startup_items" => await ListStartupAsync(timeout.Token),

                
                "suggest_actions" => SuggestActions(argumentsJson, session),

                
                "list_documents" => await ListDocumentsAsync(argumentsJson, timeout.Token),
                "read_document" => await ReadDocumentAsync(argumentsJson, timeout.Token),

                
                "web_search" => await WebTools.SearchAsync(argumentsJson, timeout.Token),
                "fetch_url" => await WebTools.FetchAsync(argumentsJson, timeout.Token),
                _ => $"未知工具：{id}。只能使用工具清单中列出的工具。",
            };

            if (result.Length > MaxResultChars)
                result = result[..MaxResultChars] + "\n（结果过长，已截断）";

            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            AppLog.Warn("hubai", $"工具 {id} 执行超时");
            return $"工具 {id} 执行超过 {ToolTimeout.TotalSeconds:0} 秒仍未完成。" +
                   "如果是整盘扫描，说明该盘文件过多；请如实告知用户本次未完成，不要编造结果，可建议改为查询具体目录。";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Exception("hubai", ex, $"执行工具 {id}");
            return $"工具 {id} 执行失败。请告知用户该操作未能完成，不要编造结果。";
        }
    }

    
    
    

    private static async Task<string> GetSnapshotAsync(CancellationToken ct)
    {
        var snapshot = await MachineProbe.CollectAsync(ct);

        
        var text = MachineDigest.ToPrompt(snapshot);

        var findings = MachineDiagnosis.Analyze(snapshot);
        if (findings.Count > 0)
        {
            text += "\n\n[本地规则判断]\n" + string.Join("\n",
                findings.Select(f => $"- {f.Title}：{f.Detail}"));
        }

        return text;
    }

    private static async Task<string> ListDiskFoldersAsync(string argumentsJson, CancellationToken ct)
    {
        var target = @"C:\";
        var top = 8;

        using (var doc = JsonDocument.Parse(NormalizeArgs(argumentsJson)))
        {
            
            if (doc.RootElement.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(p.GetString()))
            {
                target = p.GetString()!;
            }
            else if (doc.RootElement.TryGetProperty("drive", out var d) && d.ValueKind == JsonValueKind.String
                     && !string.IsNullOrWhiteSpace(d.GetString()))
            {
                target = d.GetString()!;
            }

            if (doc.RootElement.TryGetProperty("top", out var t) && t.ValueKind == JsonValueKind.Number)
                top = Math.Clamp(t.GetInt32(), 3, 20);
        }

        
        
        target = target.Trim().Replace('/', '\\');

        if (target.Length == 1) target += ":\\";
        else if (target.EndsWith(':')) target += "\\";

        var scan = await DiskUsage.ScanAsync(target, ct, budget: DiskBudget);

        var sb = new StringBuilder();

        if (scan.Truncated)
        {
            sb.AppendLine($"⚠️ 扫描超过 {DiskBudget.TotalSeconds:0} 秒已停止，以下**只统计了一部分**，" +
                          "数字偏小，不代表完整占用。拿它判断「大概是谁占地方」可以，别当精确值。");
        }

        sb.AppendLine($"{target} 已统计 {Size(scan.TotalBytes)}，顶层 {scan.Children.Count} 项" +
                      (scan.SkippedDirectories > 0 ? $"，{scan.SkippedDirectories} 个目录无权限已跳过" : "") + "。");

        foreach (var entry in scan.Children.OrderByDescending(c => c.Bytes).Take(top))
            sb.AppendLine($"- {entry.Name}：{Size(entry.Bytes)}");

        return sb.ToString();
    }

    private static string SearchLibrary(string argumentsJson, ToolSession session)
    {
        var query = "";
        var limit = 5;

        using (var doc = JsonDocument.Parse(NormalizeArgs(argumentsJson)))
        {
            if (doc.RootElement.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String)
                query = q.GetString() ?? "";

            if (doc.RootElement.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number)
                limit = Math.Clamp(l.GetInt32(), 1, 10);
        }

        if (query.Length == 0) return "检索关键词为空。请给出更具体的需求，例如「解压」「PDF」。";

        
        var apps = App.Content.Apps.ToList();
        var hits = apps
            .Where(a => a.Matches(query))
            .Take(limit)
            .Select(a => new SoftwareHit(a.Id, a.Name, a.Tagline, a.CategoryDisplay))
            .ToList();

        if (hits.Count == 0)
            return $"软件库中没有匹配「{query}」的条目。不要编造站外软件，如实告知用户可以换个说法或到软件下载页浏览。";

        foreach (var hit in hits)
        {
            
            if (!session.SoftwareHits.Any(h => h.Id == hit.Id))
                session.SoftwareHits.Add(hit);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"软件库中匹配「{query}」的有 {hits.Count} 款：");
        foreach (var hit in hits)
            sb.AppendLine($"- {hit.Name}（id: {hit.Id}，分类: {hit.Category}）：{hit.Tagline}");

        sb.Append("只能从以上条目中推荐，不得提及其他软件。");
        return sb.ToString();
    }

    

    
    
    
    
    private static async Task<string> ListDocumentsAsync(string argumentsJson, CancellationToken ct)
    {
        var query = "";

        using (var doc = JsonDocument.Parse(NormalizeArgs(argumentsJson)))
        {
            if (doc.RootElement.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String)
                query = (q.GetString() ?? "").Trim();
        }

        if (DocLibrary.Articles.Count == 0)
            await DocLibrary.SyncAsync(ct: ct);

        var articles = DocLibrary.Articles.ToList();
        if (articles.Count == 0)
        {
            return "本机还没有文档库清单（可能没有网络）。请如实告知用户暂时读不到文档库，" +
                   "可以建议打开导航里的「文档库」页面重试。";
        }

        var matched = query.Length == 0
            ? articles
            : articles.Where(a => SearchText(a).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matched.Count == 0)
            return $"文档库里没有匹配「{query}」的文章。可以换个说法，或先列出全部文章看看。";

        var sb = new StringBuilder();
        sb.AppendLine($"文档库共 {articles.Count} 篇，匹配「{query}」的有 {matched.Count} 篇：");
        foreach (var article in matched.Take(20))
        {
            sb.Append($"- {article.Title}（id: {article.Id}");
            if (article.Author.Length > 0) sb.Append($"，作者 {article.Author}");
            sb.Append($"，{article.TypeLabel}）");
            if (article.Summary.Length > 0) sb.Append("：" + article.Summary);
            sb.AppendLine();
        }

        sb.Append("要读正文请用 read_document，id 只能取自上面这些条目。");
        return sb.ToString();
    }

    
    
    
    
    private static async Task<string> ReadDocumentAsync(string argumentsJson, CancellationToken ct)
    {
        var id = "";

        using (var doc = JsonDocument.Parse(NormalizeArgs(argumentsJson)))
        {
            if (doc.RootElement.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String)
                id = (value.GetString() ?? "").Trim();
        }

        if (id.Length == 0) return "没有给出文章 id。先用 list_documents 查一下。";

        var article = DocLibrary.Find(id);
        if (article is null)
        {
            await DocLibrary.SyncAsync(ct: ct);
            article = DocLibrary.Find(id);
        }

        if (article is null)
            return $"文档库里没有 id 为「{id}」的文章。请用 list_documents 查真实的 id，不要编造。";

        try
        {
            var content = DocLibrary.TryGetCachedContent(article.Id)
                          ?? await DocLibrary.EnsureContentAsync(article.Id, null, ct);

            var body = await DocReading.ToTextAsync(content, ct);
            if (body.Length == 0) body = "（这篇文章的正文是空的。）";

            var header = new StringBuilder();
            header.Append("《").Append(article.Title).Append('》');
            if (article.Author.Length > 0) header.Append("　作者：").Append(article.Author);
            if (article.DisplayDate.Length > 0) header.Append("　更新：").Append(article.DisplayDate);
            header.AppendLine().AppendLine();

            return header.Append(body).ToString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn("hubai", $"读取文档 {id} 失败：{ex.Message}");
            return "这篇文档的正文暂时读不到（可能没有网络）。请如实告知用户，不要编造正文内容。";
        }
    }

    private static string SearchText(DocArticle article) =>
        article.Title + "\n" + article.Summary + "\n" + article.Author + "\n"
        + string.Join(" ", article.Tags);

    
    
    
    
    
    
    
    
    
    
    private static string SuggestActions(string argumentsJson, ToolSession session)
    {
        var wanted = new List<string>();

        using (var doc = JsonDocument.Parse(NormalizeArgs(argumentsJson)))
        {
            if (doc.RootElement.TryGetProperty("ids", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) wanted.Add(item.GetString() ?? "");
                }
            }
        }

        if (wanted.Count == 0) return "没有给出动作 id，回答末尾没有放按钮。";

        const int max = 4;

        
        var room = Math.Max(0, max - session.Actions.Count);

        var shown = new List<AiAction>();
        var rejected = new List<string>();
        var currentTheme = App.Settings.Current.Theme;

        foreach (var raw in wanted)
        {
            var action = AiActionCatalog.Find(raw);
            if (action is null)
            {
                rejected.Add($"「{raw}」不是清单里的 id");
                continue;
            }

            if (action.Kind == AiActionKind.Theme &&
                string.Equals(action.Target, currentTheme, StringComparison.OrdinalIgnoreCase))
            {
                rejected.Add($"「{action.Label}」不需要——当前已经是这个主题");
                continue;
            }

            if (session.Actions.Any(a => a.Id == action.Id) || shown.Any(a => a.Id == action.Id)) continue;
            if (shown.Count >= room)
            {
                rejected.Add($"「{action.Label}」超出 {max} 个上限");
                continue;
            }

            shown.Add(action);
        }

        if (shown.Count == 0)
        {
            return "这次没有可用的动作，回答末尾没有放按钮。"
                 + (rejected.Count > 0 ? "原因：" + string.Join("；", rejected) + "。" : "")
                 + "可用 id：" + string.Join("、", AiActionCatalog.All.Select(a => a.Id)) + "。";
        }

        foreach (var action in shown)
        {
            if (!session.Actions.Any(a => a.Id == action.Id)) session.Actions.Add(action);
        }

        var text = $"已在回答末尾放上 {shown.Count} 个按钮：{string.Join("、", shown.Select(a => a.Label))}。"
                 + "正文里不要再教用户怎么点，说明一句「可以点下面的按钮」即可。";

        if (rejected.Count > 0) text += "未放上的：" + string.Join("；", rejected) + "。";

        return text;
    }

    private static Task<string> ListInstalledAsync(string argumentsJson, CancellationToken ct)
    {
        var filter = "";

        using (var doc = JsonDocument.Parse(NormalizeArgs(argumentsJson)))
        {
            if (doc.RootElement.TryGetProperty("filter", out var f) && f.ValueKind == JsonValueKind.String)
                filter = (f.GetString() ?? "").Trim();
        }

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var apps = InstalledApps.Enumerate();

            
            
            if (filter.Length == 0)
            {
                var sample = apps.Take(15).Select(a => a.Name).ToList();
                return $"本机已安装 {apps.Count} 款软件。以下仅为少量样本，不完整：{string.Join("、", sample)}。" +
                       "\n如需确认某款软件是否已安装，请带上 filter 参数再调用一次。";
            }

            var matched = apps
                .Where(a => a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToList();

            return matched.Count == 0
                ? $"未找到名称包含「{filter}」的已安装软件。"
                : $"名称包含「{filter}」的已安装软件：{string.Join("、", matched)}。";
        }, ct);
    }

    private static async Task<string> ListStartupAsync(CancellationToken ct)
    {
        var items = await StartupItems.ScanAsync(ct);
        if (items.Count == 0) return "没有读取到开机启动项。";

        var enabled = items.Count(i => i.Enabled);

        var sb = new StringBuilder();
        sb.AppendLine($"共 {items.Count} 项开机启动，其中启用 {enabled} 项：");
        foreach (var item in items.Take(30))
            sb.AppendLine($"- {item.Name}（{item.SourceLabel}，{(item.Enabled ? "已启用" : "已禁用")}）");

        return sb.ToString();
    }

    
    
    

    
    private static string NormalizeArgs(string json)
        => string.IsNullOrWhiteSpace(json) ? "{}" : json;

    private static string Size(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / 1024.0 / 1024 / 1024:0.#} GB"
        : $"{bytes / 1024.0 / 1024:0.#} MB";
}
