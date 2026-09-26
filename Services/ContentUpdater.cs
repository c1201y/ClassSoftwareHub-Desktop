using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>一次内容同步的结果。</summary>
public sealed record ContentSyncResult(
    bool Updated,
    int Downloaded,
    int Total,
    string? Error,
    string BaseUrl,
    string? Note = null)
{
    public string Message => Error
        ?? Note
        ?? (Updated
            ? $"内容已更新（{Downloaded}/{Total} 个文件）"
            : $"内容已是最新（{Total} 个文件）");
}

/// <summary>
/// 软件数据的同步入口：
///   ① 首选 **GitHub 仓库直读**（见 GithubContentSync：软件数据就在公开仓库里）
///   ② 备胎：站点 content/manifest.json（列了每个文件的 path + sha256），逐个比对本地
///      %LOCALAPPDATA%\ClassSoftwareHub\content 里的文件，只下载 sha256 不一致的
///      （先落 .part，校验通过再改名）
/// 两条都拿不到就安静地什么都不做 —— ContentStore 会用安装包自带的内容 / 开发目录兜底。
/// </summary>
public static class ContentUpdater
{
    /// <summary>本地缓存目录（ContentStore 优先读它）。</summary>
    public static string Dir => ShellConfig.CachedContentDir;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClassSoftwareHub/1.2 content-sync");
        return client;
    }

    /// <summary>
    /// 同步软件数据。失败不抛异常，返回带 Error 的结果（界面自己决定要不要提示）。
    ///
    /// 顺序：
    ///   ① **GitHub 仓库直读**（首选 —— 软件数据就在公开仓库里，永远最新，不依赖站点有没有发布清单）
    ///   ② 站点 content/manifest.json（等站点以后发布了就走这条；GitHub 被墙时也是它的备胎）
    /// </summary>
    public static async Task<ContentSyncResult> SyncAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        // ① GitHub 仓库（一次 ref 请求就知道有没有变，没变一个文件都不下）
        var repo = await GithubContentSync.SyncAsync(progress, ct).ConfigureAwait(false);
        if (repo.Ok)
            return new ContentSyncResult(repo.Updated, 0, repo.Files, null, GithubContentSync.RepoUrl, repo.Message);

        // ② 站点清单（增量 sha256）
        return await SyncFromManifestAsync(progress, ct).ConfigureAwait(false);
    }

    /// <summary>老路子：按站点 content/manifest.json 的文件 sha256 增量拉。</summary>
    private static async Task<ContentSyncResult> SyncFromManifestAsync(
        IProgress<string>? progress,
        CancellationToken ct)
    {
        Manifest? manifest = null;
        string manifestUrl = "";
        string manifestText = "";

        foreach (var candidate in new[] { ShellConfig.ContentManifestUrl, ShellConfig.ContentManifestUrlFallback })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                var text = await Http.GetStringAsync(candidate, ct).ConfigureAwait(false);
                manifest = ParseManifest(text);
                if (manifest is { Files.Count: > 0 })
                {
                    manifestUrl = candidate;
                    manifestText = text;
                    break;
                }
            }
            catch (Exception ex)
            {
                progress?.Report("无法获取内容清单：" + ex.Message);
            }
        }

        if (manifest is null || manifestUrl.Length == 0)
            return new ContentSyncResult(false, 0, 0, "远端尚未发布内容包（无法获取 content/manifest.json），已回退到本机数据。", "");

        var baseUrl = manifestUrl[..(manifestUrl.LastIndexOf('/') + 1)];
        Directory.CreateDirectory(Dir);

        var downloaded = 0;
        try
        {
            foreach (var file in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();

                var relative = file.Path.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
                var target = Path.Combine(Dir, relative);

                if (File.Exists(target) && Sha256Of(target) == file.Sha256.ToLowerInvariant())
                    continue;

                progress?.Report($"正在同步 {file.Path} …");
                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var part = target + ".part";
                await using (var source = await Http.GetStreamAsync(baseUrl + file.Path, ct).ConfigureAwait(false))
                await using (var sink = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await source.CopyToAsync(sink, ct).ConfigureAwait(false);
                }

                if (Sha256Of(part) != file.Sha256.ToLowerInvariant())
                {
                    try { File.Delete(part); } catch { }
                    return new ContentSyncResult(false, downloaded, manifest.Files.Count, $"校验失败，已丢弃：{file.Path}", baseUrl);
                }

                File.Move(part, target, overwrite: true);
                downloaded++;
            }

            // 清单正文也留一份，方便排查本地是哪一版
            try { await File.WriteAllTextAsync(Path.Combine(Dir, "manifest.json"), manifestText, Encoding.UTF8, ct).ConfigureAwait(false); }
            catch { /* 写不下不影响内容 */ }
        }
        catch (Exception ex)
        {
            return new ContentSyncResult(downloaded > 0, downloaded, manifest.Files.Count, "同步出错：" + ex.Message, baseUrl);
        }

        return new ContentSyncResult(downloaded > 0, downloaded, manifest.Files.Count, null, baseUrl);
    }

    // ── 内部 ────────────────────────────────────────────────────────

    private sealed class Manifest
    {
        public List<ManifestFile> Files { get; } = new();
    }

    private sealed class ManifestFile
    {
        public string Path { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }

    private static Manifest? ParseManifest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var manifest = new Manifest();

            if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                return manifest;

            foreach (var item in files.EnumerateArray())
            {
                var path = item.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                var hash = item.TryGetProperty("sha256", out var h) ? h.GetString() ?? "" : "";
                if (path.Length == 0 || hash.Length == 0) continue;

                // 只认识正经相对路径，别让清单里的 ../ 跳出目录
                if (path.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(path)) continue;

                manifest.Files.Add(new ManifestFile { Path = path, Sha256 = hash });
            }

            return manifest;
        }
        catch
        {
            return null;
        }
    }

    private static string Sha256Of(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
        catch
        {
            return "";
        }
    }
}
