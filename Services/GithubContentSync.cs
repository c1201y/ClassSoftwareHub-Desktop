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

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>从站点仓库读软件数据的结果。</summary>
public sealed record RepoContentResult(bool Ok, bool Updated, int Files, string Message);

/// <summary>
/// 软件数据的**首选来源**：直接读站点仓库 c1201y/ClassSoftwareHub。
///
/// 为什么不用站点的 content/manifest.json：那份清单站点一直没发布（404），
/// 但仓库里「软件数据/apps/*.json + categories.json」就是原文，公开仓库不用令牌也能读。
/// 桌面端要用的其它内容（系统镜像清单）同样放在「软件数据/」下，跟着一起同步 ——
/// 这是"安装包不自带内容、一切从网络取"之后唯一的数据来源（2026-10-05）。
///
/// 做法（尽量省请求 —— 未登录的 GitHub 接口只有 60 次/小时，机房还是同一个出口 IP）：
///   1) 问一下分支头 commit sha（**每次同步就 1 个请求**，走 api.github.com，失败换镜像）
///   2) sha 跟上次一样 → 收工，一个文件都不下
///   3) sha 变了 → 列出「软件数据/」下的 json，再逐个取内容（raw 走 CDN，不吃那 60 次配额）
///      落地到 %LOCALAPPDATA%\ClassSoftwareHub\content —— 就是 ContentStore 优先读的缓存目录
///
/// ⚠️ 地址回退是必须的：raw.githubusercontent.com 在国内/校园网经常不通（本机 hosts 就把它掐了），
/// 所以还挂着 jsDelivr CDN 和 gh-proxy 镜像；成功过的入口记在本地下次优先用。
/// </summary>
public static class GithubContentSync
{
    /// <summary>GitHub 接口入口（按顺序回退）。</summary>
    private static readonly (string Label, string Prefix)[] ApiBases =
    {
        ("api.github.com", "https://api.github.com"),
        ("gh-proxy.com 镜像", "https://gh-proxy.com/https://api.github.com"),
        ("ghfast.top 镜像", "https://ghfast.top/https://api.github.com"),
    };

    /// <summary>文件内容入口（{0}=owner {1}=repo {2}=sha {3}=转义后的路径）。</summary>
    private static readonly string[] RawTemplates =
    {
        "https://raw.githubusercontent.com/{0}/{1}/{2}/{3}",
        "https://cdn.jsdelivr.net/gh/{0}/{1}@{2}/{3}",
        "https://fastly.jsdelivr.net/gh/{0}/{1}@{2}/{3}",
        "https://gh-proxy.com/https://raw.githubusercontent.com/{0}/{1}/{2}/{3}",
    };

    /// <summary>跟 ContentUpdater 共用的落地目录（ContentStore 优先读）。</summary>
    public static string Dir => ShellConfig.CachedContentDir;

    /// <summary>仓库地址（显示/排查用）。</summary>
    public static string RepoUrl => $"https://github.com/{ShellConfig.SiteRepoOwner}/{ShellConfig.SiteRepoName}";

    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>上次成功的入口（这样第二次以后不用再一个个试）。</summary>
    private static string BaseCacheFile => Path.Combine(AppPaths.DataDir, "content-base.txt");

    /// <summary>
    /// 内容目录的「整理版本」，写在缓存目录的 <c>.repofmt</c> 里。
    ///
    /// 为什么需要它：正常启动时若仓库 sha 没变，这里会**直接返回**（这是省请求的关键设计）——
    /// 于是"删掉仓库里没有的文件"这段根本不会跑，老机器上的历史垃圾会一直留着。
    /// 缓存里记的版本对不上时就强制走一次完整同步（列目录 + 清理），把上一种规则留下的东西收拾干净。
    ///
    /// 改同步规则（清理范围变了 / 布局变了）就 +1。
    /// v2 = 取消安装包内置内容包，同期把删除范围从 apps/ 扩到整个内容目录（2026-10-05）。
    /// </summary>
    private const string SyncFormat = "2";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        // GitHub 接口要求带 User-Agent，不然直接 403
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClassSoftwareHub/1.2 content-sync");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>
    /// 同步软件数据。**不抛异常**，失败就返回 Ok=false + 人话 Message，调用方自己决定降级。
    /// </summary>
    public static async Task<RepoContentResult> SyncAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Dir);

            var stamp = Path.Combine(Dir, ".reposha");
            var known = "";
            try { if (File.Exists(stamp)) known = (await File.ReadAllTextAsync(stamp, ct).ConfigureAwait(false)).Trim(); }
            catch { /* 读不到就当没记过 */ }

            var formatFile = Path.Combine(Dir, ".repofmt");
            var format = "";
            try { if (File.Exists(formatFile)) format = (await File.ReadAllTextAsync(formatFile, ct).ConfigureAwait(false)).Trim(); }
            catch { /* 读不到就当成老版本 → 走一次完整同步 */ }

            progress?.Report("正在检查 GitHub 仓库是否有新数据");
            var head = await GetHeadShaAsync(ct).ConfigureAwait(false);
            if (head is null)
                return new RepoContentResult(false, false, CountApps(), "暂时无法访问 GitHub 仓库（可能为无网络连接或被网络策略拦截），暂用本机数据。");

            var appsDir = Path.Combine(Dir, "apps");
            var hasApps = Directory.Exists(appsDir) && Directory.EnumerateFiles(appsDir, "*.json").Any();

            // ⚠️ 还要求「整理版本一致」：不一致说明缓存是上一套规则留下来的，得走一次完整同步清理
            if (hasApps && known.Length > 0 && format == SyncFormat
                && string.Equals(known, head, StringComparison.OrdinalIgnoreCase))
                return new RepoContentResult(true, false, CountApps(), $"软件数据已是最新（GitHub 仓库 {Short(head)}）");

            progress?.Report("正在列出仓库中的软件数据");
            var (paths, truncated) = await ListDataFilesAsync(head, ct).ConfigureAwait(false);
            if (paths.Count == 0)
                return new RepoContentResult(false, false, CountApps(), "GitHub 仓库中未列出软件数据文件，本机数据暂不更新。");

            Directory.CreateDirectory(appsDir);
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var downloaded = 0;
            var done = 0;

            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();

                var relative = ToRelative(path);
                if (relative is null) continue;

                var target = Path.GetFullPath(Path.Combine(Dir, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(Path.GetFullPath(Dir), StringComparison.OrdinalIgnoreCase)) continue; // 别被奇怪路径带出目录
                keep.Add(target);

                done++;
                progress?.Report($"正在读取软件数据 {done}/{paths.Count}");
                var text = await GetRawAsync(head, path, ct).ConfigureAwait(false);
                if (text.Length == 0) continue;

                var dirOfFile = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dirOfFile)) Directory.CreateDirectory(dirOfFile);

                if (!File.Exists(target)
                    || !string.Equals(await File.ReadAllTextAsync(target, ct).ConfigureAwait(false), text, StringComparison.Ordinal))
                {
                    await File.WriteAllTextAsync(target, text, Utf8NoBom, ct).ConfigureAwait(false);
                    downloaded++;
                }
            }

            // 仓库里没有的，本地也跟着删 —— 包含两类：
            //   ① 软件被下架（不删的话清单里留着幽灵条目）；
            //   ② 装机残留 —— 安装包以前自带过内容包，老机器缓存里躺着 text/、holiday/ 这类
            //      仓库根本不维护的文件。安装包不再自带内容后，它们没有任何更新途径，
            //      留着只会永远停在装机那天（2026-10-05）。
            // ⚠️ 列目录被 GitHub 截断时**不删**：那种情况下 paths 是残缺的，删了等于误伤。
            if (!truncated) DeleteUnknownFiles(keep);

            try { await File.WriteAllTextAsync(stamp, head, Utf8NoBom, ct).ConfigureAwait(false); } catch { }
            try { await File.WriteAllTextAsync(formatFile, SyncFormat, Utf8NoBom, ct).ConfigureAwait(false); } catch { }

            var count = CountApps();
            return new RepoContentResult(true, true, count,
                $"软件数据已从 GitHub 仓库更新到 {Short(head)}（{count} 个软件，{downloaded} 个文件有变化）");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden || ex.StatusCode == (HttpStatusCode)429)
        {
            return new RepoContentResult(false, false, CountApps(),
                "GitHub 接口访问次数已达上限（未登录状态每小时 60 次），稍后将自动重试，暂用本机数据。");
        }
        catch (Exception ex)
        {
            return new RepoContentResult(false, false, CountApps(), "从 GitHub 读取软件数据失败：" + ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    // ── 取数（带入口回退） ───────────────────────────────────────────

    /// <summary>分支头 sha；所有入口都失败返回 null。</summary>
    private static async Task<string?> GetHeadShaAsync(CancellationToken ct)
    {
        var path = $"repos/{ShellConfig.SiteRepoOwner}/{ShellConfig.SiteRepoName}/git/ref/heads/{ShellConfig.SiteRepoBranch}";
        try
        {
            using var doc = JsonDocument.Parse(await GetApiAsync(path, ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("object", out var obj) && obj.TryGetProperty("sha", out var sha))
            {
                var value = sha.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch { /* 下面返回 null */ }
        return null;
    }

    /// <summary>
    /// 列出仓库里「软件数据/」下的所有 json（仓库根相对路径）。
    /// 返回的 <c>Truncated</c> = GitHub 的 tree 接口把结果截断了 —— 这时清单不完整，
    /// 调用方**不能**拿它当"仓库里就这些"去删本地文件。
    /// （⚠️ 不能用 <c>out</c> 参数：async 方法不允许 —— CS1988。）
    /// </summary>
    private static async Task<(List<string> Paths, bool Truncated)> ListDataFilesAsync(string sha, CancellationToken ct)
    {
        var path = $"repos/{ShellConfig.SiteRepoOwner}/{ShellConfig.SiteRepoName}/git/trees/{sha}?recursive=1";
        using var doc = JsonDocument.Parse(await GetApiAsync(path, ct).ConfigureAwait(false));

        var list = new List<string>();
        var prefix = ShellConfig.SiteRepoDataDir + "/";

        var truncated = doc.RootElement.TryGetProperty("truncated", out var tr)
                        && tr.ValueKind == JsonValueKind.True;

        if (!doc.RootElement.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Array)
            return (list, truncated);

        foreach (var item in tree.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
            var path2 = item.TryGetProperty("path", out var p) ? p.GetString() : null;
            if (type != "blob" || string.IsNullOrEmpty(path2)) continue;
            if (!path2.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (!path2.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

            // 跟站点一致：_ 开头的文件是备注/草稿，跳过
            if (Path.GetFileName(path2).StartsWith("_", StringComparison.Ordinal)) continue;

            list.Add(path2);
        }

        return (list, truncated);
    }

    /// <summary>
    /// 给同源服务复用这条接口通道（见 <see cref="NewlyAddedService"/>）：走同一套入口回退，
    /// 也共用「上次成功的入口」的记忆 —— 一条路通了，两边都跟着走那条。
    /// ⚠️ 会吃未登录的 60 次/小时配额，调用方自己掂量频率。
    /// </summary>
    internal static Task<string> GetApiTextAsync(string apiPath, CancellationToken ct)
        => GetApiAsync(apiPath, ct);

    /// <summary>走 GitHub 接口（api.github.com → 镜像）取一段 JSON 文本。</summary>
    private static async Task<string> GetApiAsync(string apiPath, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var index in PreferredOrder("api", ApiBases.Length))
        {
            var (label, prefix) = ApiBases[index];
            try
            {
                var text = await Http.GetStringAsync(prefix + "/" + apiPath, ct).ConfigureAwait(false);
                Remember("api", index, label);
                return text;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        throw last ?? new HttpRequestException("GitHub 接口所有入口均不可用");
    }

    /// <summary>取仓库里某个文件的内容（raw → jsDelivr → gh-proxy 镜像）。</summary>
    private static async Task<string> GetRawAsync(string sha, string repoPath, CancellationToken ct)
    {
        var escaped = string.Join('/', repoPath.Split('/').Select(Uri.EscapeDataString));
        Exception? last = null;

        foreach (var index in PreferredOrder("raw", RawTemplates.Length))
        {
            var url = string.Format(RawTemplates[index],
                ShellConfig.SiteRepoOwner, ShellConfig.SiteRepoName, sha, escaped);
            try
            {
                var text = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
                Remember("raw", index, "raw#" + index);
                return text;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                throw; // 真没有这个文件，换镜像也一样
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        throw last ?? new HttpRequestException("文件内容所有入口均不可用");
    }

    // ── 路径 / 入口记忆 ─────────────────────────────────────────────

    /// <summary>「软件数据/apps/x.json」→「apps/x.json」（ContentStore 认这个布局）。</summary>
    private static string? ToRelative(string repoPath)
    {
        var prefix = ShellConfig.SiteRepoDataDir + "/";
        if (!repoPath.StartsWith(prefix, StringComparison.Ordinal)) return null;

        var rel = repoPath[prefix.Length..];
        if (rel.Length == 0) return null;
        if (rel.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return null;

        return rel;
    }

    /// <summary>优先上次成功的入口，剩下的按原顺序跟上。</summary>
    private static IEnumerable<int> PreferredOrder(string kind, int count)
    {
        var remembered = LoadRemembered(kind);
        var list = new List<int>();
        if (remembered >= 0 && remembered < count) list.Add(remembered);
        for (var i = 0; i < count; i++) if (i != remembered) list.Add(i);
        return list;
    }

    private static int LoadRemembered(string kind)
    {
        try
        {
            if (!File.Exists(BaseCacheFile)) return -1;
            foreach (var line in File.ReadAllLines(BaseCacheFile))
            {
                var parts = line.Split('=', 2);
                if (parts.Length == 2 && parts[0].Trim() == kind && int.TryParse(parts[1].Trim(), out var index))
                    return index;
            }
        }
        catch { /* 忽略 */ }
        return -1;
    }

    private static void Remember(string kind, int index, string label)
    {
        try
        {
            var lines = File.Exists(BaseCacheFile)
                ? File.ReadAllLines(BaseCacheFile).Where(l => !l.StartsWith(kind + "=", StringComparison.Ordinal)).ToList()
                : new List<string>();
            lines.Add($"{kind}={index}");
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllLines(BaseCacheFile, lines, Utf8NoBom);
            System.Diagnostics.Debug.WriteLine($"[content] {kind} 入口改用：{label}");
        }
        catch { /* 记不住不影响功能 */ }
    }

    /// <summary>
    /// 清掉内容目录里「本次没从仓库同步到」的文件 —— 下架的软件、以及装机残留。
    ///
    /// ⛔⛔ 历史上这里曾经反着来：删完立刻从**安装包自带的内容包**里把缺的补回来
    /// （<c>SeedMissingFiles</c>，overwrite:false），于是仓库删掉的软件**删了又被补回**，
    /// 用户永远看不到它下架（实测本机就复活过一个已下架的激活工具）。同时自带的 text/ 一旦写进缓存
    /// 就再也不更新。2026-10-05 Nick 定案取消安装包内置内容，这个回填连同它的病根一起删掉。
    ///
    /// 放的：`.` 开头的元文件（<c>.reposha</c>）。
    /// 兜底：任何一步失败都只当没清干净 —— 绝不能连累本次已经同步下来的数据。
    /// </summary>
    private static void DeleteUnknownFiles(HashSet<string> keep)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Dir, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file).StartsWith('.')) continue;
                if (keep.Contains(Path.GetFullPath(file))) continue;
                try { File.Delete(file); } catch { /* 删不掉算了 */ }
            }

            // 顺手收掉空目录：holiday/ 这种整个被清完之后会剩个空壳，留着碍眼
            foreach (var folder in Directory.EnumerateDirectories(Dir, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
                }
                catch { /* 删不掉算了 */ }
            }
        }
        catch { /* 清理失败不影响主要数据 */ }
    }

    private static int CountApps()
    {
        try
        {
            var dir = Path.Combine(Dir, "apps");
            return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.json").Count() : 0;
        }
        catch { return 0; }
    }

    private static string Short(string sha) => sha.Length >= 7 ? sha[..7] : sha;
}
