using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services.Diagnostics;

namespace ClassSoftwareHub.Desktop.Services.HubAi;

/// <summary>模型从软件库里查到的一条候选。UI 用它渲染可点击的推荐卡。</summary>
public sealed record SoftwareHit(string Id, string Name, string Tagline, string Category);

/// <summary>
/// 一轮对话里的工具上下文：累计本轮命中过的软件、记下调用轨迹（界面上显示"正在查看…"用）。
/// </summary>
public sealed class ToolSession
{
    public List<SoftwareHit> SoftwareHits { get; } = new();
    public List<string> Trace { get; } = new();

    public void Reset()
    {
        SoftwareHits.Clear();
        Trace.Clear();
    }
}

/// <summary>
/// 工具执行器（2026-10-18）。
///
/// <para>
/// ⛔⛔ <b>白名单就在这里</b>：只有 <see cref="ExecuteAsync"/> 的 switch 里写着的 id 能执行，
/// 工具包里写别的 id 一律走 default 拒绝。模型没有直接能力，它只能"请求"，执行永远在宿主。
/// </para>
/// <para>
/// ⛔ 全部**只读**。这里不允许出现任何删除、修改、注册表写入、进程结束的调用。
/// 需要改东西时，工具只能返回"去哪个页面处理"，由用户自己点。
/// </para>
/// </summary>
public static class ToolRunner
{
    /// <summary>
    /// 磁盘扫描的时间预算（2026-10-18）。
    ///
    /// <para>
    /// ⚠️ 这不是"超时失败"，是**软截止**：到点就停止递归，把已经统计到的部分交出去并标注不完整。
    /// 整盘扫描动辄上百万文件，用户等 90 秒然后得到一句"超时"是最糟的体验；
    /// 25 秒给一份"大致谁最占地方"的清单，通常已经足够回答用户的问题。
    /// </para>
    /// </summary>
    private static readonly TimeSpan DiskBudget = TimeSpan.FromSeconds(25);

    /// <summary>
    /// 工具本身最多跑多久（兜底）。正常路径靠 <see cref="DiskBudget"/> 提前收工，
    /// 这里只防"某个工具彻底卡死"。⚠️ 它 != 整个对话的最长等待：一轮最多 5 次工具往返。
    /// </summary>
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(90);

    /// <summary>给模型的单条结果最多多少字符。太长的清单它读不完，也浪费额度。</summary>
    private const int MaxResultChars = 2400;

    /// <summary>只读工具：随便调，不需要问人。</summary>
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.Ordinal)
    {
        "get_machine_snapshot",
        "list_disk_folders",
        "search_software_library",
        "list_installed_apps",
        "list_startup_items",
        "web_search",
        "fetch_url",
    };

    /// <summary>
    /// 会改动系统的工具。⛔ 目前**故意为空** —— 写操作通道没做好之前不加进来。
    /// 加之前必须先确认 UI 侧的审批对话框已经通了（<see cref="RequiresApproval"/> 会拦住它）。
    /// </summary>
    private static readonly HashSet<string> MutatingTools = new(StringComparer.Ordinal);

    public static bool IsReadOnly(string id) => ReadOnlyTools.Contains(id);

    /// <summary>
    /// 执行前是否必须问过用户。
    /// 未知 id 返回 false —— 它反正会被 <see cref="ExecuteAsync"/> 的 default 分支拒绝。
    /// </summary>
    public static bool RequiresApproval(string id) => MutatingTools.Contains(id);

    /// <summary>白名单里认得的工具（只读 + 改动）。</summary>
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

                // 联网（2026-10-21）：自建实现，不依赖第三方 key
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

    // ══════════════════════════════════════════════════════
    //  各工具实现（全部只读）
    // ══════════════════════════════════════════════════════

    private static async Task<string> GetSnapshotAsync(CancellationToken ct)
    {
        var snapshot = await MachineProbe.CollectAsync(ct);

        // ⚠️ 与首屏那份摘要保持一致：默认不带已装软件清单
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
            // path 优先于 drive：整盘很慢，模型查 AppData 这类具体目录时应该直接给 path
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

        // ⚠️ 路径什么写法都有：`C:` / `C` / `C:\` / **`C://`**（模型常把反斜杠转成正斜杠），
        //    统一成正斜杠再补反斜杠，否则会拼出 `C://\` 这种扫不到的路径。
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

        // 先拷一份再筛：本方法可能在后台线程跑，而 App.Content.Apps 会被同步流程整体替换
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
            // 同一款可能被多次检索命中，去重后再交给界面
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

            // ⚠️ 没给过滤词时**只报数量与少量样本**：完整软件清单能推断使用者身份与喜好，
            //    没必要为了回答"装没装某款软件"把整张单子交出去。
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

    // ══════════════════════════════════════════════════════
    //  辅助
    // ══════════════════════════════════════════════════════

    /// <summary>模型可能给空串或空白，JsonDocument.Parse 会直接抛 —— 统一兜成空对象。</summary>
    private static string NormalizeArgs(string json)
        => string.IsNullOrWhiteSpace(json) ? "{}" : json;

    private static string Size(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / 1024.0 / 1024 / 1024:0.#} GB"
        : $"{bytes / 1024.0 / 1024:0.#} MB";
}
