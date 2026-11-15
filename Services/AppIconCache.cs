using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClassSoftwareHub.Desktop.Services;




















public static class AppIconCache
{
    private static readonly string Dir =
        Path.Combine(ShellConfig.CachedContentDir, "icons");

    
    private static readonly ConcurrentDictionary<string, ImageSource> _memo = new();
    
    private static readonly ConcurrentDictionary<string, byte> _inflight = new();

    
    
    
    
    
    
    
    private static string EpochFile => Path.Combine(Dir, ".epoch");

    private static DateTime? _epochUtc;

    private static bool IsHttp(string url) =>
        !string.IsNullOrWhiteSpace(url) &&
        url.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    private static string ExtOf(string url)
    {
        var q = url.IndexOf('?');
        var path = q >= 0 ? url[..q] : url;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".svg" or ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp"
            ? ext : ".img";
    }

    private static string NameOf(string url)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url));
        var hex = Convert.ToHexString(bytes)[..16];   
        return hex + ExtOf(url);
    }

    private static string PathOf(string url) => Path.Combine(Dir, NameOf(url));

    
    
    
    
    
    public static bool TryGet(string url, out ImageSource? source, out bool stale)
    {
        source = null;
        stale = false;
        if (!IsHttp(url)) return false;
        if (_memo.TryGetValue(url, out var cached))
        {
            source = cached;
            return true;
        }
        var p = PathOf(url);
        if (!File.Exists(p)) return false;
        stale = IsStale(p);
        source = MakeSource(p);
        _memo[url] = source!;
        return true;
    }

    
    
    
    
    
    
    
    public static bool TryGetLocalPath(string url, out string path)
    {
        path = "";
        if (!IsHttp(url)) return false;

        var p = PathOf(url);
        if (!File.Exists(p)) return false;

        path = p;
        return true;
    }

    
    
    
    
    
    
    
    
    
    public static void Invalidate()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(EpochFile, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            _epochUtc = SafeMtimeUtc(EpochFile);
            _memo.Clear();   
            Core.AppLog.Info("cache", "软件数据已更新 → 图标缓存标记为陈旧，下次访问时按需刷新");
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("cache", ex, "提升图标缓存纪元");
        }
    }

    
    private static bool IsStale(string path)
    {
        var epoch = EpochUtc();
        if (epoch is null) return false;               
        var wrote = SafeMtimeUtc(path);
        return wrote is { } w && w < epoch.Value;
    }

    private static DateTime? EpochUtc()
    {
        
        if (_epochUtc is null)
            _epochUtc = File.Exists(EpochFile) ? SafeMtimeUtc(EpochFile) : null;
        return _epochUtc;
    }

    private static DateTime? SafeMtimeUtc(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null; }
        catch { return null; }
    }

    
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(8);

    
    private static MirrorChannel? _lastGoodMirror;

    
    
    
    public static async Task EnsureCachedAsync(string url, CancellationToken ct = default)
    {
        if (!IsHttp(url)) return;
        var target = PathOf(url);
        if (File.Exists(target) && !IsStale(target)) return;   
        if (!_inflight.TryAdd(url, 0)) return;        
        try
        {
            var bytes = await DownloadAsync(url, ct).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
            {
                Core.AppLog.Warn("cache", $"图标取不到（各条线路均失败）→ 暂用本机旧图：{Host(url)}");
                return;
            }
            Directory.CreateDirectory(Dir);
            
            await File.WriteAllBytesAsync(target, bytes, ct).ConfigureAwait(false);
            
            
            _memo.TryRemove(url, out _);
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("cache", ex, $"缓存图标 {Host(url)}");
        }
        finally
        {
            _inflight.TryRemove(url, out _);
        }
    }

    
    private static string Host(string url)
    {
        try { return new Uri(url).Host; } catch { return url; }
    }

    
    
    
    
    
    
    
    
    
    private static async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        if (Core.GithubMirror.IsMirrorableUrl(url))
        {
            var tried = new HashSet<string>(StringComparer.Ordinal);

            
            foreach (var candidate in await GithubRoute.ResolveAsync(url, ct).ConfigureAwait(false))
            {
                tried.Add(candidate.Url);
                var bytes = await FetchAsync(candidate.Url, ct).ConfigureAwait(false);
                if (bytes is { Length: > 0 })
                {
                    _lastGoodMirror = null;         
                    return bytes;
                }
            }

            
            
            
            
            
            
            var ordered = new List<MirrorChannel>();
            var good = _lastGoodMirror;                                  
            if (good is not null) ordered.Add(good);
            foreach (var channel in Core.GithubMirror.Channels)
                if (!ReferenceEquals(channel, good)) ordered.Add(channel);
            foreach (var channel in ordered)
            {
                var mirror = Core.GithubMirror.MirrorUrl(url, channel);
                if (!tried.Add(mirror)) continue;
                var bytes = await FetchAsync(mirror, ct).ConfigureAwait(false);
                if (bytes is { Length: > 0 })
                {
                    _lastGoodMirror = channel;
                    return bytes;
                }
            }
            return null;
        }
        return await FetchAsync(url, ct).ConfigureAwait(false);
    }

    
    private static async Task<byte[]?> FetchAsync(string url, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = FetchTimeout };
            return await http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;   
        }
    }

    
    private static ImageSource MakeSource(string file)
    {
        var uri = new Uri("file:///" + file.Replace('\\', '/'));
        if (file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            return new SvgImageSource(uri);
        return new BitmapImage(uri) { DecodePixelWidth = 128 };
    }
}
