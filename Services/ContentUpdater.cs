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










public static class ContentUpdater
{
    
    public static string Dir => ShellConfig.CachedContentDir;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClassSoftwareHub/1.2 content-sync");
        return client;
    }

    
    
    
    
    
    
    
    public static async Task<ContentSyncResult> SyncAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        
        var repo = await GithubContentSync.SyncAsync(progress, ct).ConfigureAwait(false);
        if (repo.Ok)
            return new ContentSyncResult(repo.Updated, 0, repo.Files, null, GithubContentSync.RepoUrl, repo.Message);

        
        return await SyncFromManifestAsync(progress, ct).ConfigureAwait(false);
    }

    
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
            return new ContentSyncResult(false, 0, 0, "远端尚未发布内容清单（无法获取 content/manifest.json），本机数据保持不变。", "");

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

                progress?.Report($"正在同步 {file.Path}");
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

            
            try { await File.WriteAllTextAsync(Path.Combine(Dir, "manifest.json"), manifestText, Encoding.UTF8, ct).ConfigureAwait(false); }
            catch {  }
        }
        catch (Exception ex)
        {
            return new ContentSyncResult(downloaded > 0, downloaded, manifest.Files.Count, "同步出错：" + ex.Message, baseUrl);
        }

        return new ContentSyncResult(downloaded > 0, downloaded, manifest.Files.Count, null, baseUrl);
    }

    

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
