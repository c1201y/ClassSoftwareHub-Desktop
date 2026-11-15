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




















public static class DocLibrary
{
    
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

    
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(10);

    private static List<DocArticle>? _articles;
    private static CacheIndex? _cache;
    private static DateTime _lastSync = DateTime.MinValue;

    public static string RootDir => Path.Combine(AppPaths.DataDir, "docs");
    public static string FilesDir => Path.Combine(RootDir, "files");
    private static string CatalogPath => Path.Combine(RootDir, "catalog.json");
    private static string CachePath => Path.Combine(RootDir, "cache.json");

    
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
                        
                        article = existing;
                    }
                }

                if (article is null) continue;

                article.Files = contentFiles.Select(e => e.Path).ToList();
                article.FileShas = contentFiles.ToDictionary(e => e.Path, e => e.Sha, StringComparer.OrdinalIgnoreCase);
                article.MetaSha = meta.Sha;
                next.Add(article);
            }

            
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

    
    public static DocContent? TryGetCachedContent(string id)
    {
        var article = Find(id);
        if (article is null) return null;

        var cache = GetCache();
        if (!cache.Articles.TryGetValue(id, out var hit)) return null;
        if (hit.Fingerprint != Fingerprint(article) || !hit.Exists(File.Exists)) return null;
        return BuildContent(article, hit);
    }

    

    
    public static string FolderUrl(string id) =>
        $"https://github.com/{ShellConfig.DocsRepoOwner}/{ShellConfig.DocsRepoName}/tree/" +
        $"{ShellConfig.DocsRepoBranch}/{Uri.EscapeDataString(RepoDir)}/{Uri.EscapeDataString(id)}";

    
    public static string SubmitUrl(string? title = null) =>
        $"https://github.com/{ShellConfig.DocsRepoOwner}/{ShellConfig.DocsRepoName}/issues/new" +
        "?template=doc-submission.yml&title=" +
        Uri.EscapeDataString(string.IsNullOrWhiteSpace(title) ? "[文档投稿] " : "[文档投稿] " + title.Trim());

    

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

    
    private static string Fingerprint(DocArticle article)
    {
        var text = new StringBuilder(article.Id);
        foreach (var pair in article.FileShas.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            text.Append('\n').Append(pair.Key).Append('|').Append(pair.Value);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    
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
