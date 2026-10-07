using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>「今日新增」的结果。拿不到数据时整个返回 <c>null</c>（不显示 0，免得像坏了）。</summary>
public sealed record NewlyAddedResult(IReadOnlyList<string> Ids)
{
    public int Count => Ids.Count;
}

/// <summary>
/// 今天新收录了几款软件 —— 拿「今天 0 点那一刻的仓库清单」和「本机现在的清单」做差集。
///
/// 为什么只能这么算：软件 json 里**没有任何日期字段**（id / name / icon / category / sort… 全看过了），
/// 「回声洞」字条只有 <c>{"text"}</c>，「提交 / 反馈」只在服务端 —— 三个来源里只有**软件**这一条
/// 能从 git 的历史里问出时间，而且它不在文件内容里。
///
/// 做法（固定 2 个请求，与当天提交数无关；未登录的 GitHub 接口只有 60 次/小时）：
///   ① <c>commits?until=&lt;本地今天 0 点&gt;&amp;path=软件数据/apps&amp;per_page=1</c>
///      —— apps 目录在那一刻的最后一个版本（拿它的 sha）。
///   ② <c>git/trees/{sha}?recursive=1</c> —— 那个版本的完整文件清单，筛出 apps 下的软件 id。
///   ③ 拿本机 content/apps 的清单（＝同步链路拉下来的 head 那一版，不必再问 GitHub）减掉 ②。
///   ④ 结果按「日期 + 仓库 sha」缓存到内容目录的 <c>.newlyadded.json</c>，同一天同一版本只算一次。
///
/// ⛔⛔ 走过的两条弯路，别再回去：
///   · <c>commits?since=…&amp;path=…</c> 再逐个取 commit 详情收 <c>status=="added"</c> ——
///     请求数是 <c>1 + 当天提交数</c>（实测一天 12 个提交＝13 次），而且**结果还是错的**：
///     merge commit 的 <c>files</c> 常常列不全，实测 10-06 明明净增 3 款只算出 2 款。
///   · <c>compare</c> 的净差异 —— 只要 2 次请求，但它按"两点之间的净变化"算：昨天加的软件
///     今天被改一下，同样报 <c>added</c>，会算成"今天新增"。
///   ⇒ tree 差集既省（固定 2 次）又准（比的是"那一刻存在不存在"）。
///
/// ⚠️ 口径是**净新增**：今天加了又被删掉的不算，删了又加回来的也不算（那一刻它本来就在）。
/// ⚠️ 缓存文件名以 <c>.</c> 开头是刻意的 —— 同步的清理环节只删**不以点开头**的文件
/// （见 <c>GithubContentSync.DeleteUnknownFiles</c>），放这儿不会被误删。
///
/// 失败一律返回 <c>null</c>（调用方据此**整句不显示**）—— 与"算出来是 0"严格分开。
/// </summary>
public static class NewlyAddedService
{
    /// <summary>串行化：并发进来就排队，别把配额打散。</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>进程内记忆（跨调用），判据与磁盘缓存一致。</summary>
    private static CacheEntry? _memo;

    private static string CacheFile =>
        Path.Combine(ShellConfig.CachedContentDir, ".newlyadded.json");

    /// <summary>apps 目录在仓库里的相对路径（与 <c>GithubContentSync.ListDataFilesAsync</c> 同一口径）。</summary>
    private static string AppsDirInRepo => ShellConfig.SiteRepoDataDir + "/apps";

    private static string Repo =>
        $"repos/{ShellConfig.SiteRepoOwner}/{ShellConfig.SiteRepoName}";

    /// <summary>取「今天新收录的软件」。算不出来返回 <c>null</c>。</summary>
    public static async Task<NewlyAddedResult?> GetTodayAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var today = DateTime.Today.ToString("yyyy-MM-dd");
            var head = ReadLocalHeadSha();

            // ① 命中（内存 → 磁盘）：同一天 + 仓库 sha 没变 → 直接用，零请求
            foreach (var entry in new[] { _memo, ReadCache() })
            {
                if (entry is null || entry.Date != today) continue;
                if (head.Length > 0 && !string.Equals(entry.Head, head, StringComparison.OrdinalIgnoreCase)) continue;
                _memo = entry;
                return new NewlyAddedResult(entry.Ids);
            }

            // ② 本机此刻的清单（＝仓库 head 那一版）。还没同步到内容就没法比 —— 不猜
            var headIds = ReadLocalAppIds();
            if (headIds.Count == 0) return null;

            // ③ 基准：本地今天 0 点之前，apps 目录的最后一次变更
            var until = DateTime.Today.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            var path = string.Join("/", AppsDirInRepo.Split('/').Select(Uri.EscapeDataString));
            var baseSha = ReadFirstSha(await GithubContentSync.GetApiTextAsync(
                $"{Repo}/commits?until={until}&path={path}&per_page=1", ct).ConfigureAwait(false));

            // 那一刻之前该目录从没被改过（几乎没有）—— 没有基准就别算，免得把全部软件报成"新增"
            if (baseSha is null) return null;

            // ④ 基准那一刻的清单，做差集
            var (baseIds, truncated) = ReadTreeApps(await GithubContentSync.GetApiTextAsync(
                $"{Repo}/git/trees/{baseSha}?recursive=1", ct).ConfigureAwait(false));

            // 清单被 GitHub 截断＝不完整，减出来的"新增"会是一堆假货 —— 宁可不报
            if (truncated)
            {
                AppLog.Warning("ui", "今日新增：基准清单被 GitHub 截断，这次不显示");
                return null;
            }

            var ids = headIds.Where(id => !baseIds.Contains(id))
                             .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                             .ToList();

            var fresh = new CacheEntry(today, head, ids);
            _memo = fresh;
            WriteCache(fresh);

            AppLog.Info("ui",
                $"今日新增：{ids.Count} 款（{today}，基准 {(baseSha.Length >= 7 ? baseSha[..7] : baseSha)}，"
                + $"清单 {baseIds.Count} → {headIds.Count}）");
            return new NewlyAddedResult(ids);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 拿不到就是拿不到 —— 调用方不显示这一句，而不是显示 0
            AppLog.Warning("ui", "今日新增：算不出来，这次不显示（" + ex.Message + "）");
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    // ── 解析 ────────────────────────────────────────────────────────

    /// <summary>本机已同步下来的软件 id（content/apps 下的文件名，跳过 _ 开头的模板/草稿）。</summary>
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
        catch { /* 读不到就当成空 */ }
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
        catch { /* 解析不了当没有 */ }
        return null;
    }

    /// <summary>从一个 tree 里收出 apps 下的软件 id；<c>Truncated</c> 为真时清单不完整，调用方要放弃。</summary>
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
        catch { /* 解析不了当没有 */ }

        return (set, truncated);
    }

    // ── 缓存 ────────────────────────────────────────────────────────

    private sealed record CacheEntry(string Date, string Head, List<string> Ids);

    private static CacheEntry? ReadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(CacheFile));
            var root = doc.RootElement;

            var date = root.TryGetProperty("date", out var d) ? d.GetString() : null;
            var head = root.TryGetProperty("head", out var h) ? h.GetString() : "";
            if (string.IsNullOrEmpty(date)) return null;

            var ids = new List<string>();
            if (root.TryGetProperty("ids", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray())
                    if (item.GetString() is { } s) ids.Add(s);

            return new CacheEntry(date, head ?? "", ids);
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
                ["date"] = entry.Date,
                ["head"] = entry.Head,
                ["ids"] = entry.Ids,
            });
            File.WriteAllText(CacheFile, json);
        }
        catch { /* 缓存写不进去只影响省请求，不影响正确性 */ }
    }

    /// <summary>同步链路上次记下的分支头 sha（<c>content/.reposha</c>）；没有就返回空串。</summary>
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
