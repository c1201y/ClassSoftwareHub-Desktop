using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services;


public sealed record DownloadProgress(long Received, long Total, double BytesPerSecond)
{
    public bool Indeterminate => Total <= 0;

    public double Percent => Total > 0 ? Math.Clamp(Received * 100.0 / Total, 0, 100) : 0;

    public string SizeText => Total > 0
        ? $"{Size(Received)} / {Size(Total)}"
        : Size(Received);

    public string SpeedText => BytesPerSecond >= 1 ? Size((long)BytesPerSecond) + "/s" : "";

    
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




public enum DownloadRoute
{
    
    Setting,

    
    Official,

    
    Accelerated,
}


public sealed record DownloadedFile(string Path, string FileName, long Bytes)
{
    public string SizeText => DownloadProgress.Size(Bytes);
}










public static class DownloadService
{
    
    
    
    
    public static string DefaultDir => _defaultDir ??= ResolveDownloadsDir();

    private static string? _defaultDir;

    private static string ResolveDownloadsDir()
    {
        
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
            catch {  }
        }

        
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    
    public static void OpenDownloadsFolder()
    {
        try
        {
            Directory.CreateDirectory(SaveDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = SaveDir,
                UseShellExecute = true,      
            });
        }
        catch
        {
            
        }
    }

    
    
    
    
    
    
    
    
    public static string SaveDir
    {
        get
        {
            var custom = App.Settings.Current.DownloadFolder;
            if (string.IsNullOrWhiteSpace(custom)) return DefaultDir;

            try
            {
                if (Path.IsPathFullyQualified(custom))
                {
                    if (Directory.Exists(custom)) return custom;

                    
                    var parent = Path.GetDirectoryName(custom);
                    if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) return custom;
                }
            }
            catch (Exception ex)
            {
                Core.AppLog.Warn("download", $"自定义下载文件夹读不了，改用系统下载文件夹：{ex.Message}");
            }

            Core.AppLog.Info("download", $"自定义下载文件夹不可用（{custom}），改用 {DefaultDir}");
            return DefaultDir;
        }
    }

    

    
    private const long ParallelThreshold = 8L * 1024 * 1024;

    
    private const long SegmentTargetBytes = 16L * 1024 * 1024;

    
    private const int MaxSegments = 8;

    
    private const int StreamBufferBytes = 256 * 1024;

    
    private const int SegmentAttempts = 3;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            
            MaxConnectionsPerServer = 16,
        };
        var client = new HttpClient(handler)
        {
            
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ClassSoftwareHub/1.2");
        return client;
    }

    
    
    
    
    public static async Task<DownloadedFile> DownloadAsync(
        string url,
        string? suggestedName = null,
        string? directory = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default,
        DownloadRoute route = DownloadRoute.Setting)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("下载地址为空。", nameof(url));

        var dir = string.IsNullOrWhiteSpace(directory) ? SaveDir : directory!;
        Directory.CreateDirectory(dir);

        var fileName = ResolveFileName(url, suggestedName);

        
        
        var label = string.IsNullOrWhiteSpace(suggestedName) ? fileName : suggestedName!.Trim();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Core.AppLog.Begin("download", $"下载「{label}」开始 · 线路={route}");
        Core.AppLog.Step("download", $"原始链接 {url}");

        
        IReadOnlyList<string> candidates = route switch
        {
            DownloadRoute.Official => new[] { url },
            DownloadRoute.Accelerated => await GithubRoute.ResolveAcceleratedAsync(url, ct).ConfigureAwait(false),
            
            _ => (await GithubRoute.ResolveAsync(url, ct).ConfigureAwait(false)).Select(c => c.Url).ToList(),
        };

        Exception? lastError = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            string? target = null;
            string? part = null;
            try
            {
                
                
                var probe = await ProbeAsync(candidate, ct).ConfigureAwait(false);

                if (!HasKnownExtension(fileName) && !string.IsNullOrWhiteSpace(probe.ServerFileName))
                {
                    var better = Sanitize(probe.ServerFileName!.Trim('"'));
                    if (better.Length > 0) fileName = better;
                }

                
                target = ReserveUniquePath(dir, fileName);
                part = target + ".part";

                var received = await FetchAsync(candidate, part, probe, progress, ct).ConfigureAwait(false);

                File.Move(part, target, overwrite: true);
                progress?.Report(new DownloadProgress(received, received, 0));
                Core.AppLog.Done("download",
                    $"下载「{label}」完成 · {Core.AppLog.Size(received)} · {target}", sw.Elapsed);
                return new DownloadedFile(target, Path.GetFileName(target), received);
            }
            catch (OperationCanceledException)
            {
                
                TryDelete(part);
                if (target is not null) ReleaseIfEmpty(target);
                throw;
            }
            catch (Exception ex)
            {
                
                lastError = ex;
                TryDelete(part);
                if (target is not null) ReleaseIfEmpty(target);
                if (i < candidates.Count - 1)
                {
                    
                    
                    Core.AppLog.Warn("download", $"候选 {i + 1}/{candidates.Count} 不可用（{ex.Message}）→ 换下一条");
                    progress?.Report(new DownloadProgress(0, 0, 0));
                }
                continue;
            }
        }

        
        var final = lastError ?? new InvalidOperationException("下载失败。");
        Core.AppLog.Fail("download", final, $"下载「{label}」失败（{candidates.Count} 条候选全部试过）");
        throw final;
    }

    

    
    
    
    
    public static async Task<long> DownloadToFileAsync(
        string url, string path, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var probe = await ProbeAsync(url, ct).ConfigureAwait(false);
        return await FetchAsync(url, path, probe, progress, ct).ConfigureAwait(false);
    }

    
    
    
    
    
    private static async Task<long> FetchAsync(
        string url, string part, ProbeInfo probe, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var total = probe.Total;
        var segments = SegmentCountFor(total);

        if (probe.SupportsRange && segments > 1)
        {
            Core.AppLog.Info("download", $"并行 {segments} 路 · {DownloadProgress.Size(total)} · {url}");
            try
            {
                return await DownloadParallelAsync(
                        url, part, total, segments, progress, ct, Stopwatch.StartNew())
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                
                Core.AppLog.Info("download", "分片失败，退回单流：" + ex.Message);
                TryDelete(part);
            }
        }

        Core.AppLog.Info("download", $"单流 · {DownloadProgress.Size(total)} · {url}");
        return await DownloadSingleAsync(url, part, total, progress, ct, Stopwatch.StartNew())
            .ConfigureAwait(false);
    }

    
    private sealed record ProbeInfo(long Total, bool SupportsRange, string? ServerFileName);

    
    
    
    
    private static async Task<ProbeInfo> ProbeAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        if (GithubRoute.IsAcceleratedUrl(url))
            request.Headers.Referrer = new Uri(GithubRoute.AcceleratorReferer);

        using var response = await Http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long total;
        var supportsRange = response.StatusCode == HttpStatusCode.PartialContent;
        if (supportsRange)
            total = response.Content.Headers.ContentRange?.Length ?? -1;
        else
            total = response.Content.Headers.ContentLength ?? -1;

        
        if (!supportsRange && total > 0 && response.Headers.AcceptRanges.Contains("bytes"))
            supportsRange = true;

        var name = response.Content.Headers.ContentDisposition?.FileNameStar
                   ?? response.Content.Headers.ContentDisposition?.FileName;

        return new ProbeInfo(total, supportsRange, name);
    }

    
    private static int SegmentCountFor(long total)
    {
        if (total < ParallelThreshold) return 1;
        var bySize = (int)Math.Ceiling(total / (double)SegmentTargetBytes);
        return Math.Clamp(bySize, 2, MaxSegments);
    }

    
    private static async Task<long> DownloadSingleAsync(
        string url, string part, long total,
        IProgress<DownloadProgress>? progress, CancellationToken ct, Stopwatch watch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (GithubRoute.IsAcceleratedUrl(url))
            request.Headers.Referrer = new Uri(GithubRoute.AcceleratorReferer);

        using var response = await Http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (total <= 0) total = response.Content.Headers.ContentLength ?? -1;

        var pump = new ProgressPump(progress, total, watch);
        pump.Reset();

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var sink = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None,
            StreamBufferBytes, true);

        var buffer = new byte[StreamBufferBytes];
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) break;

            await sink.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            pump.Add(read);
        }

        pump.Complete();
        return pump.Received;
    }

    
    
    
    
    private static async Task<long> DownloadParallelAsync(
        string url, string part, long total, int segments,
        IProgress<DownloadProgress>? progress, CancellationToken ct, Stopwatch watch)
    {
        
        using (var seed = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 1, false))
            seed.SetLength(total);

        var pump = new ProgressPump(progress, total, watch);
        pump.Reset();

        var tasks = new Task[segments];
        for (var i = 0; i < segments; i++)
        {
            
            var start = total * i / segments;
            var end = total * (i + 1) / segments - 1;
            tasks[i] = DownloadSegmentAsync(url, part, start, end, pump, ct);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        pump.Complete();
        return pump.Received;
    }

    
    private static async Task DownloadSegmentAsync(
        string url, string part, long start, long end, ProgressPump pump, CancellationToken ct)
    {
        var want = end - start + 1;
        Exception? last = null;

        for (var attempt = 1; attempt <= SegmentAttempts; attempt++)
        {
            
            long billed = 0;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(start, end);
                if (GithubRoute.IsAcceleratedUrl(url))
                    request.Headers.Referrer = new Uri(GithubRoute.AcceleratorReferer);

                using var response = await Http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.PartialContent)
                    throw new IOException($"服务端没按区间返回（HTTP {(int)response.StatusCode}）");

                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var sink = new FileStream(part, FileMode.Open, FileAccess.Write, FileShare.ReadWrite,
                    0, true);
                sink.Seek(start, SeekOrigin.Begin);

                var buffer = new byte[StreamBufferBytes];
                long got = 0;
                while (got < want)
                {
                    var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read <= 0) break;
                    if (read > want - got) read = (int)(want - got);

                    await sink.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    pump.Add(read);
                    billed += read;
                    got += read;
                }

                if (got != want)
                    throw new IOException($"区间 {start}-{end} 只收到 {got}/{want} 字节");
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                if (billed > 0) pump.Add(-billed);
                if (attempt < SegmentAttempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct).ConfigureAwait(false);
            }
        }

        throw last ?? new IOException("分片下载失败。");
    }

    
    
    
    
    private sealed class ProgressPump
    {
        private readonly IProgress<DownloadProgress>? _sink;
        private readonly long _total;
        private readonly Stopwatch _watch;
        private readonly object _gate = new();
        private long _received;
        private long _lastMs = long.MinValue / 2;

        public ProgressPump(IProgress<DownloadProgress>? sink, long total, Stopwatch watch)
        {
            _sink = sink;
            _total = total;
            _watch = watch;
        }

        public long Received => Interlocked.Read(ref _received);

        
        public void Reset()
        {
            lock (_gate) _lastMs = 0;
            _sink?.Report(new DownloadProgress(0, _total, 0));
        }

        public void Add(long bytes)
        {
            var now = Interlocked.Add(ref _received, bytes);
            var ms = _watch.ElapsedMilliseconds;

            lock (_gate)
            {
                if (ms - _lastMs < 120 && now < _total) return;
                _lastMs = ms;
            }

            var seconds = _watch.Elapsed.TotalSeconds;
            _sink?.Report(new DownloadProgress(now, _total,
                seconds > 0 && now > 0 ? now / seconds : 0));
        }

        
        public void Complete()
            => _sink?.Report(new DownloadProgress(_total > 0 ? _total : Received, _total, 0));
    }

    

    
    
    
    
    
    

    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".appx", ".appxbundle", ".msixbundle",
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".xz", ".bz2", ".zst",
        ".cab", ".iso", ".img", ".bin", ".dmg", ".pkg", ".deb", ".rpm", ".apk",
        ".apks", ".jar", ".whl", ".crx",
    };

    
    
    
    
    
    
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
                
                
                
                return WithKnownExtension(name, url);
        }

        return fromUrl.Length > 0 ? fromUrl : "download.bin";
    }

    
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

    
    public static bool HasKnownExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var ext = Path.GetExtension(fileName);
        return ext.Length > 1 && FileExtensions.Contains(ext);
    }

    
    private static string WithKnownExtension(string name, string source)
    {
        if (HasKnownExtension(name)) return name;

        var ext = Path.GetExtension(FileNameFromUrl(source));
        return ext.Length > 1 && FileExtensions.Contains(ext) ? name + ext : name;
    }

    
    private static bool LooksLikeJunk(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length < 2) return true;
        if (stem.Equals("download", StringComparison.OrdinalIgnoreCase)) return true;
        if (stem.Equals("file", StringComparison.OrdinalIgnoreCase)) return true;
        if (stem.Equals("setup", StringComparison.OrdinalIgnoreCase)) return false;

        
        return stem.Length >= 24 && stem.All(c => Uri.IsHexDigit(c) || c == '-' || c == '_');
    }

    private static string Sanitize(string name)
    {
        name = name.Trim().Trim('"');
        foreach (var bad in Path.GetInvalidFileNameChars())
            name = name.Replace(bad, '_');
        return name.Trim(' ', '.');
    }

    
    
    
    
    
    
    
    
    private static string ReserveUniquePath(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        if (TryReserve(path)) return path;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (TryReserve(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    private static bool TryReserve(string path)
    {
        try
        {
            using var slot = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return true;
        }
        catch (IOException) { return false; }          
        catch (UnauthorizedAccessException) { return false; }
    }

    
    private static void ReleaseIfEmpty(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 0) return;
            File.Delete(path);
        }
        catch {  }
    }

    
    private static void TryDelete(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
        }
        catch {  }
    }
}
