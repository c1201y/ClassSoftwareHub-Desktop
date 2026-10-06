using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>上传用途：软件包（私有、下载要过票据闸门）还是图标（小图、公开只读）。</summary>
public enum OssPurpose
{
    File,
    Icon,
}

/// <summary>上传进度（已发送字节 / 总字节）。</summary>
public readonly record struct UploadProgress(long Sent, long Total)
{
    /// <summary>0~1；总量未知时给 0（本链路总量总是已知，这里只是兜底）。</summary>
    public double Fraction => Total > 0 ? Math.Clamp(Sent / (double)Total, 0, 1) : 0;
}

/// <summary>上传失败分类：调用方据此选文案，避免把 OSS 的英文 XML 原始报错直接甩给用户。</summary>
public enum OssUploadErrorCode
{
    /// <summary>本地预检：超过 <see cref="OssUpload.MaxBytes"/>。</summary>
    TooLarge,
    /// <summary>签名接口明确拒绝（服务未配置 / 该用途被关掉）。</summary>
    SignFailed,
    /// <summary>签名接口连不上，或回的不是提交服务的响应。</summary>
    Unreachable,
    /// <summary>传输中断（断网等）。</summary>
    Network,
    /// <summary>用户取消。</summary>
    Aborted,
    /// <summary>OSS 拒绝了这次上传（403 / 400…）。</summary>
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

/// <summary>
/// 把本地文件传到本站的阿里云 OSS —— 客户端直传，不经任何后端中转。
///
/// 链路（与网页端 <c>src/gallery/ossUpload.ts</c> 完全同一套协议）：
///   ① 把 <c>{ name, size, contentType, purpose, fp }</c> 发给提交服务的 <c>/api/oss-sign</c>，
///      换回一条「只能写这一个对象、约一小时后过期」的预签名 PUT 地址；
///   ② 客户端自己 PUT 到 OSS（上传进度就是这一步的进度）；
///   ③ 回填的是**对象键**而不是公开直链 —— 桶是私有的，直链谁打开都是 403。
///
/// 两种 purpose，落两个前缀、走两条完全不同的读取路径：
///   File → <c>upload/…</c> 私有，下载要过 <c>/api/dl</c> 的短时票据闸门；
///   Icon → <c>icon/…</c>   小图，走 <c>/api/icon</c> 公开只读（审核工单里能直接点开）。
///
/// ⚠️ Content-Type 必须原样照抄签名接口回传的值：预签名地址把「请求形状」锁死了，
///    Content-Type 是签名的一部分，发的值和签的值对不上会被 OSS 打回 403 SignatureDoesNotMatch。
///
/// ⚠️ 上限：<see cref="MaxBytes"/>（2.5 GB，与网页端一致；天花板是 OSS 单次 PUT 的 5 GB，
///    再大必须改成分片上传）。比它大的东西引导用户改填官网 / GitHub Releases 直链。
/// </summary>
public static class OssUpload
{
    /// <summary>单文件上限：2.5 GB（改这里要同时改 Worker 的 OSS_MAX_BYTES，不能反着来）。</summary>
    public const long MaxBytes = 2684354560L;

    /// <summary>超过此大小给「文件较大、上传较慢」的提示，但不阻止上传。</summary>
    public const long WarnBytes = 1073741824L;

    /// <summary>图标还没压过就选进来的原图上限：再大就不值得解码了（与网页端一致）。</summary>
    public const long IconInputMaxBytes = 8L * 1024 * 1024;

    private const string DefaultContentType = "application/octet-stream";

    /// <summary>大文件上传可能很久，超时交给调用方的 CancellationToken 管。</summary>
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private sealed record SignedTarget(string Url, string Key, string ContentType, int OrphanMinutes);

    /// <summary>按扩展名给 Content-Type。**只对图片给准确值**（<c>/api/icon</c> 会把它转给访客的浏览器），
    /// 其余一律 octet-stream —— 猜错了反而添乱，浏览器的下载行为也不靠它。</summary>
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

    /// <summary>
    /// 把文件传到 OSS。
    /// </summary>
    /// <returns>
    /// <see cref="OssUploadResult.Url"/> 是要写进数据的「怎么读它」：
    /// 软件包 → <c>oss://对象键</c>（详情页点下载时换票据）；图标 → <c>/api/icon?k=键</c>（能直接塞进 &lt;img&gt;）。
    /// </returns>
    /// <exception cref="OssUploadException">见 <see cref="OssUploadErrorCode"/></exception>
    public static async Task<OssUploadResult> UploadAsync(
        string filePath,
        OssPurpose purpose,
        IProgress<UploadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new OssUploadException(OssUploadErrorCode.Network, "文件不存在：" + filePath);

        var info = new FileInfo(filePath);
        if (info.Length > MaxBytes)
            throw new OssUploadException(
                OssUploadErrorCode.TooLarge,
                $"file size {info.Length} exceeds limit {MaxBytes}");

        var contentType = ContentTypeFor(filePath);

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
                // 只有「这个入口本身不可用」才值得换下一个；服务已经明确拒绝就别再试了
                lastUnreachable = ex;
                continue;
            }

            await PutAsync(target, filePath, info.Length, progress, ct).ConfigureAwait(false);
            // 传完了才把入口记下来：签发成功但传输失败，说明这个入口未必好用
            SubmitEndpoint.Remember(baseUrl);

            var url = purpose == OssPurpose.Icon
                ? $"{baseUrl}/api/icon?k={Uri.EscapeDataString(target.Key)}"
                : "oss://" + target.Key;
            return new OssUploadResult(target.Key, url, target.OrphanMinutes, baseUrl);
        }

        throw lastUnreachable ?? new OssUploadException(OssUploadErrorCode.Unreachable, "没有可用的提交入口");
    }

    /// <summary>向单个入口要一条预签名地址；连不上、超时、或拿到别的东西（比如回源没生效时的 nginx HTML 页）都算它不可用。</summary>
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
            // 服务端据此在登记表里记下「这台设备传的」（只存哈希），将来按人追溯或清理时才有依据
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
                // 回的不是提交服务响应（HTML 错误页等）→ 换下一个入口
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

    /// <summary>把文件 PUT 到预签名地址，带上传进度。</summary>
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

    /// <summary>OSS 的报错是 XML，只把 &lt;Code&gt; 抠出来给人看（正文本身是英文长句，不适合直接展示）。</summary>
    private static string OssFailureText(int status, string body)
    {
        var code = Regex.Match(body ?? "", "<Code>([^<]+)</Code>").Groups[1].Value;
        return code.Length > 0 ? $"HTTP {status} {code}" : $"HTTP {status}";
    }

    /// <summary>流式读文件、边写边报进度（HttpClient 本身不给上传进度，只能自己包一层）。</summary>
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

            var buffer = new byte[1 << 20];   // 1 MB：上传进度不需要更细的粒度
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

/// <summary>上传成功的结果。<see cref="Url"/> 是要写进投稿数据的读取地址。</summary>
public sealed record OssUploadResult(string Key, string Url, int OrphanMinutes, string BaseUrl);
