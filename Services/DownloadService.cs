using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>下载进度快照（给界面用；Total 为 0 表示服务端没给长度 → 界面走不确定进度）。</summary>
public sealed record DownloadProgress(long Received, long Total, double BytesPerSecond)
{
    public bool Indeterminate => Total <= 0;

    public double Percent => Total > 0 ? Math.Clamp(Received * 100.0 / Total, 0, 100) : 0;

    public string SizeText => Total > 0
        ? $"{Size(Received)} / {Size(Total)}"
        : Size(Received);

    public string SpeedText => BytesPerSecond >= 1 ? Size((long)BytesPerSecond) + "/s" : "";

    /// <summary>「12.3 MB」这种人类可读体积。</summary>
    public static string Size(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{value:0} {units[unit]}"
            : $"{value:0.#} {units[unit]}";
    }
}

/// <summary>下载结果。</summary>
public sealed record DownloadedFile(string Path, string FileName, long Bytes)
{
    public string SizeText => DownloadProgress.Size(Bytes);
}

/// <summary>
/// 原生下载（不跳浏览器）：流式写盘 → 先落 .part 再改名 → 支持取消/进度/测速。
/// 走的是应用自己的 HttpClient，跟网页端下载框的体验对齐但完全在本程序里完成。
/// </summary>
public static class DownloadService
{
    /// <summary>
    /// 默认落到系统真实的「下载」文件夹（注册表 known folder，用户改过盘符/名字也认；
    /// 比如这台机器就是 D:\Microsoft Edge 下载）。**绝不自己另建一个 Downloads 目录**。
    /// </summary>
    public static string DefaultDir => _defaultDir ??= ResolveDownloadsDir();

    private static string? _defaultDir;

    private static string ResolveDownloadsDir()
    {
        // 系统「下载」文件夹的 known folder GUID
        const string id = "{374DE290-123F-4565-9164-39C4925E467B}";

        foreach (var sub in new[]
                 {
                     @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
                     @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders",
                 })
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(sub);
                if (key?.GetValue(id) is string raw && !string.IsNullOrWhiteSpace(raw))
                {
                    var path = Environment.ExpandEnvironmentVariables(raw);
                    if (path.Length > 0) return path;
                }
            }
            catch { /* 读不到就试下一个 */ }
        }

        // 最后兜底（几乎不会走到）
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    /// <summary>在资源管理器里打开「下载」文件夹（任务列表底部的按钮用；打不开就安静算了）。</summary>
    public static void OpenDownloadsFolder()
    {
        try
        {
            Directory.CreateDirectory(DefaultDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = DefaultDir,
                UseShellExecute = true,      // 交给 shell，才能让已有的资源管理器窗口接管
            });
        }
        catch
        {
            // 打不开文件夹不值得打扰用户
        }
    }

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var client = new HttpClient(handler)
        {
            // 大文件可能很慢，别让默认 100 秒超时把下载掐了
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ClassSoftwareHub/1.2");
        return client;
    }

    /// <summary>
    /// 下载到 <paramref name="directory"/>（默认「下载」文件夹）。
    /// 文件名优先用 <paramref name="suggestedName"/>，否则从 URL 猜；重名自动加 (1)(2)。
    /// </summary>
    public static async Task<DownloadedFile> DownloadAsync(
        string url,
        string? suggestedName = null,
        string? directory = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("下载地址是空的。", nameof(url));

        var dir = string.IsNullOrWhiteSpace(directory) ? DefaultDir : directory!;
        Directory.CreateDirectory(dir);

        var fileName = ResolveFileName(url, suggestedName);
        var target = UniquePath(dir, fileName);
        var part = target + ".part";

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;

        // 名字还没有正经扩展名时，看服务端 Content-Disposition 给的文件名（带扩展名就换过来）
        if (!HasKnownExtension(fileName))
        {
            var header = response.Content.Headers.ContentDisposition?.FileNameStar
                         ?? response.Content.Headers.ContentDisposition?.FileName;
            if (!string.IsNullOrWhiteSpace(header))
            {
                var better = Sanitize(header.Trim('"'));
                if (better.Length > 0 && !string.Equals(better, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    fileName = better;
                    target = UniquePath(dir, fileName);
                    part = target + ".part";
                }
            }
        }

        long received = 0;
        var buffer = new byte[128 * 1024];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var lastTick = TimeSpan.Zero;

        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var sink = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, true))
            {
                progress?.Report(new DownloadProgress(0, total, 0));

                while (true)
                {
                    var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read <= 0) break;

                    await sink.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;

                    // 最多 8 次/秒 上报，别把 UI 线程刷爆
                    if (watch.Elapsed - lastTick >= TimeSpan.FromMilliseconds(120) || (total > 0 && received >= total))
                    {
                        lastTick = watch.Elapsed;
                        var speed = watch.Elapsed.TotalSeconds > 0 ? received / watch.Elapsed.TotalSeconds : 0;
                        progress?.Report(new DownloadProgress(received, total, speed));
                    }
                }
            }

            File.Move(part, target, overwrite: true);
            progress?.Report(new DownloadProgress(received, total > 0 ? total : received, 0));
            return new DownloadedFile(target, Path.GetFileName(target), received);
        }
        catch
        {
            // 失败/取消都别留半个文件
            try { if (File.Exists(part)) File.Delete(part); } catch { /* 删不掉就算了 */ }
            throw;
        }
    }

    /// <summary>
    /// 这个 URL 看起来是「直接一个文件」还是「一个网页/官网跳转」？
    /// 站点里不少软件更新频繁，下载链接其实指向官网页面（让用户自己去点），
    /// 这种情况绝不能当文件下载（否则会把网页存成文件）。
    /// </summary>
    public static bool IsDirectFileUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        try
        {
            var path = new Uri(url).AbsolutePath;
            var ext = Path.GetExtension(path);
            return ext.Length > 1 && FileExtensions.Contains(ext);
        }
        catch
        {
            return false;
        }
    }

    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".appx", ".appxbundle", ".msixbundle",
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".xz", ".bz2", ".zst",
        ".cab", ".iso", ".img", ".bin", ".dmg", ".pkg", ".deb", ".rpm", ".apk",
        ".apks", ".jar", ".whl", ".crx",
    };

    /// <summary>
    /// 文件名怎么定：
    ///   1) **优先用链接里的原始文件名**（开发者起的，最不容易出错，浏览器也这么干）
    ///   2) 链接名是垃圾（没扩展名 / 一串 hash / download 之类）才用界面上给的「软件名 + 平台 + 版本」
    ///   3) 扩展名一律从白名单里认，避免把「…v0.2.8」里的「.8」当成扩展名（踩过这个坑）
    /// </summary>
    public static string ResolveFileName(string url, string? suggestedName)
    {
        var fromUrl = Sanitize(FileNameFromUrl(url));
        var urlHasRealExt = HasKnownExtension(fromUrl);

        if (fromUrl.Length > 0 && urlHasRealExt && !LooksLikeJunk(fromUrl))
            return fromUrl;

        if (!string.IsNullOrWhiteSpace(suggestedName))
        {
            var name = Sanitize(suggestedName!);
            if (name.Length > 0)
                return WithKnownExtension(name, urlHasRealExt ? fromUrl : url);
        }

        return fromUrl.Length > 0 ? fromUrl : "download.bin";
    }

    /// <summary>URL 末段（URL 解码后）当文件名。</summary>
    private static string FileNameFromUrl(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath.TrimEnd('/');
            var last = path.Split('/')[^1];
            return Uri.UnescapeDataString(last);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>扩展名是不是我们认识的（白名单），别拿「.8」这种当扩展名。</summary>
    public static bool HasKnownExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var ext = Path.GetExtension(fileName);
        return ext.Length > 1 && FileExtensions.Contains(ext);
    }

    /// <summary>没有正经扩展名就补一个（从 URL / 原始文件名里认）。</summary>
    private static string WithKnownExtension(string name, string source)
    {
        if (HasKnownExtension(name)) return name;

        var ext = Path.GetExtension(FileNameFromUrl(source));
        return ext.Length > 1 && FileExtensions.Contains(ext) ? name + ext : name;
    }

    /// <summary>哈希名 / 明显的占位名，不值得当文件名用。</summary>
    private static bool LooksLikeJunk(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length < 2) return true;
        if (stem.Equals("download", StringComparison.OrdinalIgnoreCase)) return true;
        if (stem.Equals("file", StringComparison.OrdinalIgnoreCase)) return true;
        if (stem.Equals("setup", StringComparison.OrdinalIgnoreCase)) return false;

        // 一长串十六进制/数字，通常是 CDN 或 release 的 hash 名
        return stem.Length >= 24 && stem.All(c => Uri.IsHexDigit(c) || c == '-' || c == '_');
    }

    private static string Sanitize(string name)
    {
        name = name.Trim().Trim('"');
        foreach (var bad in Path.GetInvalidFileNameChars())
            name = name.Replace(bad, '_');
        return name.Trim(' ', '.');
    }

    private static string UniquePath(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return path;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");
    }
}
