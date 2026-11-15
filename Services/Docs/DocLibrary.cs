using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.Docs;

/// <summary>
/// 文档库（需求 15）：文章放在桌面端仓库的「文档库/」下，桌面端按需取用。
///
/// <para>
/// 两层缓存，目的不同：
/// <list type="bullet">
///   <item><b>清单</b>（<c>docs\catalog.json</c>）：所有文章的标题 / 作者 / 简介 / 标签。
///         列表页与全局搜索只读这一份，不下载正文。</item>
///   <item><b>正文</b>（<c>docs\files\</c>）：真正打开某篇时才下载。落地时**去掉扩展名、文件名换成
///         64 位随机十六进制**，映射关系只保存在本机的 <c>docs\cache.json</c> 里 ——
///         缓存文件不经本程序打不开，也无法根据文件名判断是哪篇文章。</item>
/// </list>
/// </para>
///
/// <para>
/// ⛔ 正文缓存必须保留真实字节：docx / pdf 不是纯文本，改名不影响读取，但换编码或转存会破坏格式。
/// 映射表是唯一的"钥匙"，删掉它等于缓存全部作废（下次打开重新下载即可，不会报错）。
/// </para>
/// </summary>
public static class DocLibrary
{
    /// <summary>桌面端仓库里放文章的目录。</summary>
    public const string RepoDir = "文档库";

    private static readonly object Gate = new();
    private static readonly SemaphoreSlim SyncGate = new(1, 1);
    private static readonly SemaphoreSlim ContentGate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOut = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions JsonIn = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly string[] SupportedExtensions = { ".md", ".markdown", ".txt", ".docx", ".pdf" };

    /// <summary>自动同步的最小间隔：列表页每次进入都问一次会很吵，十分钟足够。</summary>
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(10);

    private static List<DocArticle>? _articles;
    private static CacheIndex? _cache;
    private static DateTime _lastSync = DateTime.MinValue;

    public static string RootDir => Path.Combine(AppPaths.DataDir, "docs");
    public static string FilesDir => Path.Combine(RootDir, "files");
    private static string CatalogPath => Path.Combine(RootDir, "catalog.json");
    private static string CachePath => Path.Combine(RootDir, "cache.json");

    /// <summary>清单有变化时触发（正文变化不触发，正文在打开时自己校验指纹）。</summary>
    public static event Action? Changed;

    public static IReadOnlyList<DocArticle> Articles
    {
        get { EnsureLoaded(); return _articles!; }
    }

    public static DocArticle? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        EnsureLoaded();
        return _articles!.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    // ── 清单同步 ─────────────────────────────────────────────────

    /// <summary>
    /// 从 GitHub 仓库读出「文档库/」的目录清单，按需更新各篇的 meta.json。
    /// **不抛网络异常**：失败时保留本机清单返回 Ok=false。
    /// </summary>
    public static async Task<DocSyncResult> SyncAsync(
        bool force = false,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        await SyncGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureLoaded();

            if (!force && DateTime.UtcNow - _lastSync < SyncInterval)
                return new DocSyncResult(true, false, _articles!.Count, "文档库已就绪");

            _lastSync = DateTime.UtcNow;
            progress?.Report("正在检查文档库");

            string treeJson;
            try
            {
                var api = $"repos/{ShellConfig.DocsRepoOwner}/{ShellConfig.DocsRepoName}" +
                          $"/git/trees/{ShellConfig.DocsRepoBranch}?recursive=1";
                treeJson = await GithubContentSync.GetApiTextAsync(api, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Warn("docs", "取不到文档库目录：" + ex.Message);
                return new DocSyncResult(false, false, _articles!.Count,
                    "暂时无法访问 GitHub 仓库（可能为无网络连接或被网络策略拦截），暂用本机已缓存的文章。");
            }

            HashSet<string> folders;
            var files = new Dictionary<string, List<(string Path, string Sha)>>(StringComparer.OrdinalIgnoreCase);
            bool truncated;
            try
            {
                (folders, files, truncated) = ParseTree(treeJson);
            }
            catch (Exception ex)
            {
                AppLog.Warn("docs", "文档库目录格式异常：" + ex.Message);
                return new DocSyncResult(false, false, _articles!.Count, "GitHub 仓库返回的文档目录无法识别，暂用本机数据。");
            }
            if (truncated)
                AppLog.Warn("docs", "GitHub 目录列表被截断：本轮不删除本机文章");

            var before = JsonSerializer.Serialize(_articles, JsonOut);
            var known = _articles!.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
            var next = new List<DocArticle>();
            var done = 0;

            foreach (var id in folders.OrderBy(x => x, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (!IsSafeId(id) || !files.TryGetValue(id, out var entries)) continue;

                var meta = entries.FirstOrDefault(e =>
                    string.Equals(e.Path, "meta.json", StringComparison.OrdinalIgnoreCase));
                if (meta.Path is null) continue;

                var contentFiles = entries
                    .Where(e => !string.Equals(e.Path, "meta.json", StringComparison.OrdinalIgnoreCase))
                    .Where(e => !e.Path.StartsWith(".", StringComparison.Ordinal)
                                && !e.Path.Split('/').Any(p => p.Length == 0 || p == ".."))
                    .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                known.TryGetValue(id, out var existing);

                DocArticle? article = existing;
                if (existing is null || !string.Equals(existing.MetaSha, meta.Sha, StringComparison.Ordinal))
                {
                    done++;
                    progress?.Report($"正在读取文章信息 {done}");
                    try
                    {
                        var text = await GithubContentSync
                            .GetBranchFileTextAsync(
                                ShellConfig.DocsRepoOwner, ShellConfig.DocsRepoName,
                                ShellConfig.DocsRepoBranch, $"{RepoDir}/{id}/meta.json", ct)
                            .ConfigureAwait(false);
                        article = ParseMeta(id, text, contentFiles.Select(e => e.Path).ToList());
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn("docs", $"文章 {id} 的 meta.json 读取失败：{ex.Message}");
                        // 新文章读不到就跳过；老文章沿用本机那份，别因一次网络抖动让它从列表里消失
                        article = existing;
                    }
                }

                if (article is null) continue;

                article.Files = contentFiles.Select(e => e.Path).ToList();
                article.FileShas = contentFiles.ToDictionary(e => e.Path, e => e.Sha, StringComparer.OrdinalIgnoreCase);
                article.MetaSha = meta.Sha;
                next.Add(article);
            }

            // 目录被截断时，没列出来的老朋友不能当"已被删除"处理
            if (truncated)
                foreach (var old in _articles!)
                    if (next.All(a => !string.Equals(a.Id, old.Id, StringComparison.OrdinalIgnoreCase)))
                        next.Add(old);

            next = next
                .OrderBy(a => a.Order)
                .ThenByDescending(a => a.ParsedDate ?? DateTime.MinValue)
                .ThenBy(a => a.Title, StringComparer.CurrentCulture)
                .ToList();

            var changed = before != JsonSerializer.Serialize(next, JsonOut);
            _articles = next;

            if (changed)
            {
                SaveCatalog();
                PruneCache();
                AppLog.Step("docs", $"文档库已更新：{next.Count} 篇文章");
            }
            Changed?.Invoke();

            return new DocSyncResult(true, changed, next.Count,
                changed ? $"文档库已更新（{next.Count} 篇文章）" : "文档库已是最新");
        }
        finally
        {
            SyncGate.Release();
        }
    }

    // ── 正文缓存 ─────────────────────────────────────────────────

    /// <summary>
    /// 取到某篇的正文。已经缓存过且仓库内容没变就不联网；否则下载并写成无扩展名随机文件。
    /// </summary>
    public static async Task<DocContent> EnsureContentAsync(
        string id,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var article = Find(id) ?? throw new InvalidOperationException("这篇文档不在当前文档库中");
        if (article.Files.Count == 0 || string.IsNullOrEmpty(article.File))
            throw new InvalidOperationException("这篇文档还没有正文文件");

        var fingerprint = Fingerprint(article);
        var cache = GetCache();

        if (cache.Articles.TryGetValue(id, out var hit)
            && hit.Fingerprint == fingerprint
            && hit.Exists(File.Exists))
        {
            return BuildContent(article, hit);
        }

        await ContentGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 排队期间可能已经被另一处下载好了
            cache = GetCache();
            if (cache.Articles.TryGetValue(id, out hit)
                && hit.Fingerprint == fingerprint
                && hit.Exists(File.Exists))
            {
                return BuildContent(article, hit);
            }

            Directory.CreateDirectory(FilesDir);

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var written = new List<string>();

            try
            {
                var index = 0;
                foreach (var relative in article.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    index++;
                    progress?.Report($"正在读取文档 {index}/{article.Files.Count}");

                    byte[] bytes;
                    try
                    {
                        bytes = await GithubContentSync
                            .GetBranchFileBytesAsync(
                                ShellConfig.DocsRepoOwner, ShellConfig.DocsRepoName,
                                ShellConfig.DocsRepoBranch, $"{article.RepoArticleDir}/{relative}", ct)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (!string.Equals(relative, article.File, StringComparison.OrdinalIgnoreCase))
                    {
                        // 图片之类缺一张不影响读正文
                        AppLog.Warn("docs", $"文章 {id} 的资源 {relative} 读取失败：{ex.Message}");
                        continue;
                    }

                    var name = NewCacheName();
                    var path = Path.Combine(FilesDir, name);
                    await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
                    written.Add(path);
                    map[relative] = path;
                }
            }
            catch
            {
                foreach (var path in written) TryDelete(path);
                throw;
            }

            if (!map.TryGetValue(article.File, out var primary))
                throw new IOException("正文文件未能读取，请检查网络后重试");

            var entry = new CachedDoc
            {
                Fingerprint = fingerprint,
                File = primary,
                CachedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Resources = map
                    .Where(p => !string.Equals(p.Key, article.File, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase),
            };

            cache.Articles.TryGetValue(id, out var old);
            cache.Articles[id] = entry;
            SaveCache(cache);
            RemoveOrphanFiles(old);

            AppLog.Step("docs", $"文章 {id} 已缓存（{map.Count} 个文件）");
            return BuildContent(article, entry);
        }
        finally
        {
            ContentGate.Release();
        }
    }

    /// <summary>已经缓存过就返回，不联网。给 AI 工具做"能读就读"的快速路径。</summary>
    public static DocContent? TryGetCachedContent(string id)
    {
        var article = Find(id);
        if (article is null) return null;

        var cache = GetCache();
        if (!cache.Articles.TryGetValue(id, out var hit)) return null;
        if (hit.Fingerprint != Fingerprint(article) || !hit.Exists(File.Exists)) return null;
        return BuildContent(article, hit);
    }

    // ── 链接 ─────────────────────────────────────────────────────

    /// <summary>文章目录在 GitHub 上的地址（网页版也能直接看源文件）。</summary>
    public static string FolderUrl(string id) =>
        $"https://github.com/{ShellConfig.DocsRepoOwner}/{ShellConfig.DocsRepoName}/tree/" +
        $"{ShellConfig.DocsRepoBranch}/{Uri.EscapeDataString(RepoDir)}/{Uri.EscapeDataString(id)}";

    /// <summary>投稿入口（GitHub Issue 表单，不走服务器）。</summary>
    public static string SubmitUrl(string? title = null) =>
        $"https://github.com/{ShellConfig.DocsRepoOwner}/{ShellConfig.DocsRepoName}/issues/new" +
        "?template=doc-submission.yml&title=" +
        Uri.EscapeDataString(string.IsNullOrWhiteSpace(title) ? "[文档投稿] " : "[文档投稿] " + title.Trim());

    // ── 内部：清单 ───────────────────────────────────────────────

    private static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_articles is not null) return;
            _articles = LoadCatalog();
        }
    }

    private static List<DocArticle> LoadCatalog()
    {
        try
        {
            if (!File.Exists(CatalogPath)) return new List<DocArticle>();

            using var doc = JsonDocument.Parse(File.ReadAllText(CatalogPath, Encoding.UTF8));
            if (doc.RootElement.TryGetProperty("syncedAt", out var at)
                && DateTime.TryParse(at.GetString(), out var parsed))
                _lastSync = parsed.ToUniversalTime();

            if (!doc.RootElement.TryGetProperty("articles", out var list)
                || list.ValueKind != JsonValueKind.Array)
                return new List<DocArticle>();

            var result = JsonSerializer.Deserialize<List<DocArticle>>(list.GetRawText(), JsonIn)
                         ?? new List<DocArticle>();
            return result.Where(a => IsSafeId(a.Id)).ToList();
        }
        catch (Exception ex)
        {
            AppLog.Warn("docs", "读取本机文档清单失败：" + ex.Message);
            return new List<DocArticle>();
        }
    }

    private static void SaveCatalog()
    {
        try
        {
            Directory.CreateDirectory(RootDir);
            var payload = new
            {
                syncedAt = _lastSync.ToUniversalTime().ToString("O"),
                articles = _articles,
            };
            WriteAtomic(CatalogPath, JsonSerializer.Serialize(payload, JsonOut));
        }
        catch (Exception ex)
        {
            AppLog.Warn("docs", "保存本机文档清单失败：" + ex.Message);
        }
    }

    private static (HashSet<string> Folders,
                    Dictionary<string, List<(string Path, string Sha)>> Files,
                    bool Truncated)
        ParseTree(string json)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, List<(string Path, string Sha)>>(StringComparer.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(json);
        var truncated = doc.RootElement.TryGetProperty("truncated", out var tr)
                        && tr.ValueKind == JsonValueKind.True;

        if (!doc.RootElement.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Array)
            return (folders, files, truncated);

        var prefix = RepoDir + "/";
        foreach (var item in tree.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type) || type.GetString() != "blob") continue;
            if (!item.TryGetProperty("path", out var pathProp)) continue;

            var path = pathProp.GetString() ?? "";
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var rest = path[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0 || slash == rest.Length - 1) continue;

            var id = rest[..slash];
            var relative = rest[(slash + 1)..];
            if (!IsSafeId(id) || relative.Contains("..", StringComparison.Ordinal)) continue;

            var sha = item.TryGetProperty("sha", out var shaProp) ? shaProp.GetString() ?? "" : "";

            folders.Add(id);
            if (!files.TryGetValue(id, out var list))
                files[id] = list = new List<(string, string)>();
            list.Add((relative, sha));
        }

        return (folders, files, truncated);
    }

    private static DocArticle? ParseMeta(string id, string text, List<string> files)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            string Str(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? ""
                    : "";

            var title = Str("title").Trim();
            if (title.Length == 0) return null;

            var file = DocContent.NormalizeRelative(Str("file"));
            if (file.Length == 0 || !files.Any(f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase)))
                file = PickMainFile(files);
            if (file.Length == 0) return null;

            var actual = files.FirstOrDefault(f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase)) ?? file;
            var type = NormalizeType(Str("type"));
            if (type.Length == 0) type = TypeFromExtension(actual);
            if (type.Length == 0) return null;

            var order = 1000;
            if (root.TryGetProperty("order", out var orderProp)
                && orderProp.ValueKind == JsonValueKind.Number
                && orderProp.TryGetInt32(out var parsedOrder))
                order = parsedOrder;

            var tags = new List<string>();
            if (root.TryGetProperty("tags", out var tagProp) && tagProp.ValueKind == JsonValueKind.Array)
                tags.AddRange(tagProp.EnumerateArray()
                    .Where(t => t.ValueKind == JsonValueKind.String)
                    .Select(t => (t.GetString() ?? "").Trim())
                    .Where(t => t.Length > 0));

            return new DocArticle
            {
                Id = id,
                Title = title,
                Author = Str("author").Trim(),
                Contact = Str("contact").Trim(),
                Summary = Str("summary").Trim(),
                Date = Str("date").Trim(),
                Type = type,
                File = actual,
                Order = order,
                Tags = tags,
            };
        }
        catch (Exception ex)
        {
            AppLog.Warn("docs", $"文章 {id} 的 meta.json 无法解析：{ex.Message}");
            return null;
        }
    }

    private static string PickMainFile(List<string> files) =>
        files.FirstOrDefault(f => string.Equals(f, "article" + Path.GetExtension(f), StringComparison.OrdinalIgnoreCase)
                                  && SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
        ?? files.FirstOrDefault(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
        ?? "";

    internal static string NormalizeType(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "markdown" or "md" => "markdown",
        "txt" or "text" => "txt",
        "docx" or "word" => "docx",
        "pdf" => "pdf",
        _ => "",
    };

    internal static string TypeFromExtension(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".md" or ".markdown" => "markdown",
        ".txt" => "txt",
        ".docx" => "docx",
        ".pdf" => "pdf",
        _ => "",
    };

    // ── 内部：缓存 ───────────────────────────────────────────────

    private sealed class CacheIndex
    {
        public Dictionary<string, CachedDoc> Articles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static CacheIndex GetCache()
    {
        lock (Gate)
        {
            if (_cache is not null) return _cache;
            try
            {
                if (File.Exists(CachePath))
                {
                    _cache = JsonSerializer.Deserialize<CacheIndex>(File.ReadAllText(CachePath, Encoding.UTF8), JsonIn);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("docs", "读取正文缓存索引失败：" + ex.Message);
            }

            return _cache ??= new CacheIndex();
        }
    }

    private static void SaveCache(CacheIndex cache)
    {
        try
        {
            Directory.CreateDirectory(RootDir);
            WriteAtomic(CachePath, JsonSerializer.Serialize(cache, JsonOut));
        }
        catch (Exception ex)
        {
            AppLog.Warn("docs", "保存正文缓存索引失败：" + ex.Message);
        }
    }

    private static DocContent BuildContent(DocArticle article, CachedDoc entry) => new()
    {
        Article = article,
        FilePath = entry.File,
        Type = TypeFromExtension(article.File) is { Length: > 0 } type ? type : article.Type,
        Resources = entry.Resources,
    };

    /// <summary>缓存里"这篇文章现在由哪些文件组成"的指纹：文件集合或任一处内容变化都会变。</summary>
    private static string Fingerprint(DocArticle article)
    {
        var text = new StringBuilder(article.Id);
        foreach (var pair in article.FileShas.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            text.Append('\n').Append(pair.Key).Append('|').Append(pair.Value);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    /// <summary>清单同步后清掉已下架 / 内容已变的旧缓存，免得缓存目录越攒越大。</summary>
    private static void PruneCache()
    {
        var cache = GetCache();
        var changed = false;

        foreach (var id in cache.Articles.Keys.ToList())
        {
            var article = Find(id);
            if (article is not null && cache.Articles[id].Fingerprint == Fingerprint(article)) continue;

            RemoveOrphanFiles(cache.Articles[id]);
            cache.Articles.Remove(id);
            changed = true;
        }

        if (changed) SaveCache(cache);
    }

    private static void RemoveOrphanFiles(CachedDoc? entry)
    {
        if (entry is null) return;
        foreach (var path in entry.AllFiles) TryDelete(path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn("docs", "删除缓存文件失败：" + ex.Message);
        }
    }

    /// <summary>64 位十六进制、无扩展名 —— 文件本身看不出是什么，也打不开。</summary>
    private static string NewCacheName() =>
        Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private static bool IsSafeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (id.StartsWith("_", StringComparison.Ordinal) || id.StartsWith(".", StringComparison.Ordinal)) return false;
        if (id.Contains('/') || id.Contains('\\') || id.Contains("..", StringComparison.Ordinal)) return false;
        return id.All(ch => !char.IsControl(ch));
    }
}
