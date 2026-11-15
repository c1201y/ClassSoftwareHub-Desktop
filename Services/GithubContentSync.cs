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


public sealed record RepoContentResult(bool Ok, bool Updated, int Files, string Message);


















public static class GithubContentSync
{
    
    private static readonly (string Label, string Prefix)[] ApiBases =
    {
        ("api.github.com", "https://api.github.com"),
        ("gh-proxy.com 镜像", "https://gh-proxy.com/https://api.github.com"),
        ("ghfast.top 镜像", "https://ghfast.top/https://api.github.com"),
    };

    
    private static readonly string[] RawTemplates =
    {
        "https://raw.githubusercontent.com/{0}/{1}/{2}/{3}",
        "https://cdn.jsdelivr.net/gh/{0}/{1}@{2}/{3}",
        "https://fastly.jsdelivr.net/gh/{0}/{1}@{2}/{3}",
        "https://gh-proxy.com/https://raw.githubusercontent.com/{0}/{1}/{2}/{3}",
    };

    
    public static string Dir => ShellConfig.CachedContentDir;

    
    public static string RepoUrl => $"https://github.com/{ShellConfig.SiteRepoOwner}/{ShellConfig.SiteRepoName}";

    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    
    private static string BaseCacheFile => Path.Combine(AppPaths.DataDir, "content-base.txt");

    
    
    
    
    
    
    
    
    
    
    private const string SyncFormat = "2";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClassSoftwareHub/1.2 content-sync");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    
    
    
    public static async Task<RepoContentResult> SyncAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Dir);

            
            
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Core.AppLog.Begin("content", $"开始同步软件数据 · 仓库 {RepoUrl}");

            var stamp = Path.Combine(Dir, ".reposha");
            var known = "";
            try { if (File.Exists(stamp)) known = (await File.ReadAllTextAsync(stamp, ct).ConfigureAwait(false)).Trim(); }
            catch {  }

            var formatFile = Path.Combine(Dir, ".repofmt");
            var format = "";
            try { if (File.Exists(formatFile)) format = (await File.ReadAllTextAsync(formatFile, ct).ConfigureAwait(false)).Trim(); }
            catch {  }

            progress?.Report("正在检查 GitHub 仓库是否有新数据");
            var head = await GetHeadShaAsync(ct).ConfigureAwait(false);
            if (head is null)
            {
                
                Core.AppLog.Warn("content", "取不到仓库分支头（无网络 / 被网络策略拦截）→ 沿用本机数据");
                return new RepoContentResult(false, false, CountApps(), "暂时无法访问 GitHub 仓库（可能为无网络连接或被网络策略拦截），暂用本机数据。");
            }
            Core.AppLog.Step("content", $"分支头 {Short(head)}");

            var appsDir = Path.Combine(Dir, "apps");
            var hasApps = Directory.Exists(appsDir) && Directory.EnumerateFiles(appsDir, "*.json").Any();

            
            if (hasApps && known.Length > 0 && format == SyncFormat
                && string.Equals(known, head, StringComparison.OrdinalIgnoreCase))
            {
                Core.AppLog.Step("content", $"与仓库一致，无需更新（本机 {CountApps()} 个软件）");
                return new RepoContentResult(true, false, CountApps(), $"软件数据已是最新（GitHub 仓库 {Short(head)}）");
            }

            progress?.Report("正在列出仓库中的软件数据");
            var (paths, truncated) = await ListDataFilesAsync(head, ct).ConfigureAwait(false);
            if (paths.Count == 0)
            {
                Core.AppLog.Warn("content", $"仓库未列出任何软件数据文件（head={Short(head)}）→ 本机数据不动");
                return new RepoContentResult(false, false, CountApps(), "GitHub 仓库中未列出软件数据文件，本机数据暂不更新。");
            }
            Core.AppLog.Step("content", $"待处理 {paths.Count} 个文件" + (truncated ? "（⚠ 列表被 GitHub 截断，本轮不删本地文件）" : ""));

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
                if (!target.StartsWith(Path.GetFullPath(Dir), StringComparison.OrdinalIgnoreCase)) continue; 
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

            
            
            
            
            
            
            if (!truncated) DeleteUnknownFiles(keep);

            try { await File.WriteAllTextAsync(stamp, head, Utf8NoBom, ct).ConfigureAwait(false); } catch { }
            try { await File.WriteAllTextAsync(formatFile, SyncFormat, Utf8NoBom, ct).ConfigureAwait(false); } catch { }

            
            
            
            if (downloaded > 0)
            {
                AppIconCache.Invalidate();
                Core.AppLog.Step("content", $"{downloaded} 个数据文件有变化 → 图标缓存已标记为陈旧");
            }

            var count = CountApps();
            Core.AppLog.Done("content", $"软件数据已更新到 {Short(head)} · {count} 个软件 · {downloaded} 个文件有变化", sw.Elapsed);
            return new RepoContentResult(true, true, count,
                $"软件数据已从 GitHub 仓库更新到 {Short(head)}（{count} 个软件，{downloaded} 个文件有变化）");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden || ex.StatusCode == (HttpStatusCode)429)
        {
            
            Core.AppLog.Warn("content", "GitHub 接口限流（未登录 60 次/小时）→ 稍后自动重试，沿用本机数据");
            return new RepoContentResult(false, false, CountApps(),
                "GitHub 接口访问次数已达上限（未登录状态每小时 60 次），稍后将自动重试，暂用本机数据。");
        }
        catch (Exception ex)
        {
            Core.AppLog.Fail("content", ex, "同步软件数据失败");
            return new RepoContentResult(false, false, CountApps(), "从 GitHub 读取软件数据失败：" + ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    

    
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
        catch {  }
        return null;
    }

    
    
    
    
    
    
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

            
            if (Path.GetFileName(path2).StartsWith("_", StringComparison.Ordinal)) continue;

            list.Add(path2);
        }

        return (list, truncated);
    }

    
    
    
    
    
    internal static Task<string> GetApiTextAsync(string apiPath, CancellationToken ct)
        => GetApiAsync(apiPath, ct);

    
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

    
    
    
    
    
    internal static Task<string> GetBranchFileTextAsync(
        string owner, string repo, string branch, string repoPath, CancellationToken ct)
        => GetRawAsync(owner, repo, branch, repoPath, ct);

    
    internal static async Task<byte[]> GetBranchFileBytesAsync(
        string owner, string repo, string branch, string repoPath, CancellationToken ct)
    {
        var escaped = string.Join('/', repoPath.Split('/').Select(Uri.EscapeDataString));
        Exception? last = null;

        foreach (var index in PreferredOrder("raw", RawTemplates.Length))
        {
            var url = string.Format(RawTemplates[index], owner, repo, branch, escaped);
            try
            {
                var bytes = await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
                Remember("raw", index, "raw#" + index);
                return bytes;
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
        throw last ?? new HttpRequestException("文件内容所有入口均不可用");
    }

    
    private static Task<string> GetRawAsync(string sha, string repoPath, CancellationToken ct)
        => GetRawAsync(ShellConfig.SiteRepoOwner, ShellConfig.SiteRepoName, sha, repoPath, ct);

    private static async Task<string> GetRawAsync(
        string owner, string repo, string sha, string repoPath, CancellationToken ct)
    {
        var escaped = string.Join('/', repoPath.Split('/').Select(Uri.EscapeDataString));
        Exception? last = null;

        foreach (var index in PreferredOrder("raw", RawTemplates.Length))
        {
            var url = string.Format(RawTemplates[index], owner, repo, sha, escaped);
            try
            {
                var text = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
                Remember("raw", index, "raw#" + index);
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
        throw last ?? new HttpRequestException("文件内容所有入口均不可用");
    }

    

    
    private static string? ToRelative(string repoPath)
    {
        var prefix = ShellConfig.SiteRepoDataDir + "/";
        if (!repoPath.StartsWith(prefix, StringComparison.Ordinal)) return null;

        var rel = repoPath[prefix.Length..];
        if (rel.Length == 0) return null;
        if (rel.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return null;

        return rel;
    }

    
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
        catch {  }
        return -1;
    }

    private static void Remember(string kind, int index, string label)
    {
        try
        {
            var before = LoadRemembered(kind);
            var lines = File.Exists(BaseCacheFile)
                ? File.ReadAllLines(BaseCacheFile).Where(l => !l.StartsWith(kind + "=", StringComparison.Ordinal)).ToList()
                : new List<string>();
            lines.Add($"{kind}={index}");
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllLines(BaseCacheFile, lines, Utf8NoBom);

            
            
            
            if (before != index)
                Core.AppLog.Info("content", $"{kind} 入口改用：{label}");   
        }
        catch {  }
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private static bool IsDerivedCache(string file)
    {
        try
        {
            var full = Path.GetFullPath(file);
            var icons = Path.GetFullPath(Path.Combine(Dir, "icons")) + Path.DirectorySeparatorChar;
            var quick = Path.GetFullPath(Path.Combine(Dir, "quickicons")) + Path.DirectorySeparatorChar;
            return full.StartsWith(icons, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(quick, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void DeleteUnknownFiles(HashSet<string> keep)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Dir, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file).StartsWith('.')) continue;
                if (IsDerivedCache(file)) continue;     
                if (keep.Contains(Path.GetFullPath(file))) continue;
                try { File.Delete(file); } catch {  }
            }

            
            foreach (var folder in Directory.EnumerateDirectories(Dir, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
                }
                catch {  }
            }
        }
        catch {  }
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
