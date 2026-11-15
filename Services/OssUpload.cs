using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using ClassSoftwareHub.Desktop.Services.License;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services;


public enum OssPurpose
{
    File,
    Icon,
}


public readonly record struct UploadProgress(long Sent, long Total)
{
    
    public double Fraction => Total > 0 ? Math.Clamp(Sent / (double)Total, 0, 1) : 0;
}


public enum OssUploadErrorCode
{
    
    TooLarge,
    
    SignFailed,
    
    Unreachable,
    
    Network,
    
    Aborted,
    
    Http,
}

public sealed class OssUploadException : Exception
{
    public OssUploadErrorCode Code { get; }
    public int Status { get; }

    public OssUploadException(OssUploadErrorCode code, string message, int status = 0) : base(message)
    {
        Code = code;
        Status = status;
    }
}




















public static class OssUpload
{
    
    public const long MaxBytes = 2684354560L;

    
    public const long WarnBytes = 1073741824L;

    
    public const long IconInputMaxBytes = 8L * 1024 * 1024;

    private const string DefaultContentType = "application/octet-stream";

    
    private static readonly HttpClient Http = new(new AuthHandler(new HttpClientHandler { AllowAutoRedirect = true }))
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private sealed record SignedTarget(string Url, string Key, string ContentType, int OrphanMinutes);

    
    
    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".ico" => "image/x-icon",
        ".svg" => "image/svg+xml",
        _ => DefaultContentType,
    };

    
    
    
    
    
    
    
    
    public static async Task<OssUploadResult> UploadAsync(
        string filePath,
        OssPurpose purpose,
        IProgress<UploadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            var missing = new OssUploadException(OssUploadErrorCode.Network, "文件不存在：" + filePath);
            Core.AppLog.Fail("upload", missing, "上传失败：源文件不存在");
            throw missing;
        }

        var info = new FileInfo(filePath);
        if (info.Length > MaxBytes)
        {
            var tooBig = new OssUploadException(
                OssUploadErrorCode.TooLarge,
                $"file size {info.Length} exceeds limit {MaxBytes}");
            Core.AppLog.Fail("upload", tooBig,
                $"上传「{info.Name}」失败：{Core.AppLog.Size(info.Length)} 超过上限 {Core.AppLog.Size(MaxBytes)}");
            throw tooBig;
        }

        var contentType = ContentTypeFor(filePath);

        
        
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Core.AppLog.Begin("upload", $"上传「{info.Name}」开始 · {Core.AppLog.Size(info.Length)} · 用途={purpose} · 类型={contentType}");

        OssUploadException? lastUnreachable = null;
        foreach (var baseUrl in SubmitEndpoint.Ordered())
        {
            SignedTarget target;
            try
            {
                target = await SignAsync(baseUrl, info.Name, info.Length, contentType, purpose, ct).ConfigureAwait(false);
            }
            catch (OssUploadException ex) when (ex.Code == OssUploadErrorCode.Unreachable)
            {
                
                lastUnreachable = ex;
                Core.AppLog.Warn("upload", $"入口 {baseUrl} 不可用（{ex.Message}）→ 换下一个");
                continue;
            }

            Core.AppLog.Step("upload", $"已拿到预签名（入口 {baseUrl}，对象键 {target.Key}）");
            await PutAsync(target, filePath, info.Length, progress, ct).ConfigureAwait(false);
            
            SubmitEndpoint.Remember(baseUrl);

            var url = purpose == OssPurpose.Icon
                ? $"{baseUrl}/api/icon?k={Uri.EscapeDataString(target.Key)}"
                : "oss://" + target.Key;
            Core.AppLog.Done("upload", $"上传「{info.Name}」完成 · 写入引用 {url}", sw.Elapsed);
            return new OssUploadResult(target.Key, url, target.OrphanMinutes, baseUrl);
        }

        var none = lastUnreachable ?? new OssUploadException(OssUploadErrorCode.Unreachable, "没有可用的提交入口");
        Core.AppLog.Fail("upload", none, $"上传「{info.Name}」失败：所有提交入口都不可用");
        throw none;
    }

    
    private static async Task<SignedTarget> SignAsync(
        string baseUrl,
        string name,
        long size,
        string contentType,
        OssPurpose purpose,
        CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["name"] = name,
            ["size"] = size,
            ["contentType"] = contentType,
            ["purpose"] = purpose == OssPurpose.Icon ? "icon" : "file",
            
            ["fp"] = DeviceFingerprint.Value,
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(SubmitEndpoint.TimeoutMs);

        HttpResponseMessage response;
        string text;
        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            response = await Http.PostAsync(baseUrl + "/api/oss-sign", content, cts.Token).ConfigureAwait(false);
            text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new OssUploadException(OssUploadErrorCode.Unreachable, baseUrl + " 超时", 0);
        }
        catch (OperationCanceledException)
        {
            throw new OssUploadException(OssUploadErrorCode.Aborted, "aborted");
        }
        catch (Exception ex)
        {
            throw new OssUploadException(OssUploadErrorCode.Unreachable, $"{baseUrl} 连不上（{ex.Message}）", 0);
        }

        using (response)
        {
            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(text);
                root = document.RootElement.Clone();
            }
            catch
            {
                
                throw new OssUploadException(OssUploadErrorCode.Unreachable,
                    $"{baseUrl} 返回的不是提交服务响应", (int)response.StatusCode);
            }

            var ok = root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;
            var url = root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            var key = root.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
            if (!ok || string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key))
            {
                var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                    ? e.GetString()
                    : $"签发上传地址失败（HTTP {(int)response.StatusCode}）";
                throw new OssUploadException(OssUploadErrorCode.SignFailed, error!, (int)response.StatusCode);
            }

            var signedType = root.TryGetProperty("contentType", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : contentType;
            var orphan = root.TryGetProperty("orphanMinutes", out var m) && m.ValueKind == JsonValueKind.Number
                ? m.GetInt32()
                : 0;

            return new SignedTarget(url!, key!, string.IsNullOrWhiteSpace(signedType) ? contentType : signedType!, orphan);
        }
    }

    
    private static async Task PutAsync(
        SignedTarget target,
        string filePath,
        long length,
        IProgress<UploadProgress>? progress,
        CancellationToken ct)
    {
        using var content = new FileUploadContent(filePath, length, progress);
        if (MediaTypeHeaderValue.TryParse(target.ContentType, out var mediaType))
            content.Headers.ContentType = mediaType;

        using var request = new HttpRequestMessage(HttpMethod.Put, target.Url) { Content = content };
        try
        {
            using var response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                progress?.Report(new UploadProgress(length, length));
                return;
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new OssUploadException(OssUploadErrorCode.Http,
                OssFailureText((int)response.StatusCode, body), (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OssUploadException(OssUploadErrorCode.Aborted, "aborted");
        }
        catch (HttpRequestException ex)
        {
            throw new OssUploadException(OssUploadErrorCode.Network, ex.Message);
        }
    }

    
    private static string OssFailureText(int status, string body)
    {
        var code = Regex.Match(body ?? "", "<Code>([^<]+)</Code>").Groups[1].Value;
        return code.Length > 0 ? $"HTTP {status} {code}" : $"HTTP {status}";
    }

    
    private sealed class FileUploadContent : HttpContent
    {
        private readonly string _path;
        private readonly long _length;
        private readonly IProgress<UploadProgress>? _progress;

        public FileUploadContent(string path, long length, IProgress<UploadProgress>? progress)
        {
            _path = path;
            _length = length;
            _progress = progress;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => CopyAsync(stream, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
            => CopyAsync(stream, ct);

        private async Task CopyAsync(Stream target, CancellationToken ct)
        {
            await using var source = new FileStream(
                _path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);

            var buffer = new byte[1 << 20];   
            long sent = 0;
            _progress?.Report(new UploadProgress(0, _length));

            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read <= 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                sent += read;
                _progress?.Report(new UploadProgress(sent, _length));
            }
        }
    }
}


public sealed record OssUploadResult(string Key, string Url, int OrphanMinutes, string BaseUrl);
