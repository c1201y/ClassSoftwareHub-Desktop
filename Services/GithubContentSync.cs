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

            progress?.Report("正在问 GitHub 仓库有没有新数据…");
            var head = await GetHeadShaAsync(ct).ConfigureAwait(false);
            if (head is null)
                return new RepoContentResult(false, false, CountApps(), "GitHub 仓库暂时问不到（没网或被校园网拦了），先用本机数据。");

            var appsDir = Path.Combine(Dir, "apps");
            var hasApps = Directory.Exists(appsDir) && Directory.EnumerateFiles(appsDir, "*.json").Any();

            if (hasApps && known.Length > 0 && string.Equals(known, head, StringComparison.OrdinalIgnoreCase))
            {
                SeedMissingFiles();
                return new RepoContentResult(true, false, CountApps(), $"软件数据已是最新（GitHub 仓库 {Short(head)}）");
            }

            progress?.Report("正在列出仓库里的软件数据…");
            var paths = await ListDataFilesAsync(head, ct).ConfigureAwait(false);
            if (paths.Count == 0)
                return new RepoContentResult(false, false, CountApps(), "GitHub 仓库里没列出软件数据文件，本机数据先不动。");

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
                progress?.Report($"正在读软件数据 {done}/{paths.Count} …");
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

            // 仓库里删掉的软件，本地也跟着删（不然清单里留着幽灵条目）
            foreach (var old in Directory.EnumerateFiles(appsDir, "*.json"))
            {
                try { if (!keep.Contains(Path.GetFullPath(old))) File.Delete(old); } catch { /* 删不掉算了 */ }
            }

            try { await File.WriteAllTextAsync(stamp, head, Utf8NoBom, ct).ConfigureAwait(false); } catch { }

            SeedMissingFiles();

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
                "GitHub 接口次数用超了（没登录每小时 60 次，机房同一个出口 IP 分着用），过一会儿自己会再来，先用本机数据。");
        }
        catch (Exception ex)
        {
            return new RepoContentResult(false, false, CountApps(), "从 GitHub 读软件数据失败：" + ex.Message);
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

    /// <summary>列出仓库里「软件数据/」下的所有 json（仓库根相对路径）。</summary>
    private static async Task<List<string>> ListDataFilesAsync(string sha, CancellationToken ct)
    {
        var path = $"repos/{ShellConfig.SiteRepoOwner}/{ShellConfig.SiteRepoName}/git/trees/{sha}?recursive=1";
        using var doc = JsonDocument.Parse(await GetApiAsync(path, ct).ConfigureAwait(false));

        var list = new List<string>();
        var prefix = ShellConfig.SiteRepoDataDir + "/";

        if (!doc.RootElement.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Array)
            return list;

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

        return list;
    }

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
        throw last ?? new HttpRequestException("GitHub 接口所有入口都不通");
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
        throw last ?? new HttpRequestException("文件内容所有入口都不通");
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
    /// 从安装包自带的内容里补齐缺的文件（text/ui.json、text/mirror-sites.json、manifest.json 这些
    /// GitHub 仓库里不是这种形态或不用每次拉），
    /// 否则缓存目录里只有 apps/ → 界面文案、镜像站清单会空掉。
    /// </summary>
    private static void SeedMissingFiles()
    {
        var bundled = ShellConfig.BundledContentDir;
        if (!Directory.Exists(bundled)) return;
        if (Path.GetFullPath(bundled).Equals(Path.GetFullPath(Dir), StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            foreach (var source in Directory.EnumerateFiles(bundled, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(bundled, source);
                var target = Path.Combine(Dir, rel);
                if (File.Exists(target)) continue;

                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(source, target, overwrite: false);
            }
        }
        catch { /* 补不上不影响主要数据 */ }
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
