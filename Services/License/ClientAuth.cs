using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.License;

/// <summary>一次激活尝试的结果。</summary>
/// <param name="Ok">服务端是否下发了令牌。</param>
/// <param name="Message">给人看的话；成功时为空。</param>
public sealed record ActivateResult(bool Ok, string Message)
{
    public static readonly ActivateResult Success = new(true, "");
}

/// <summary>
/// 自建后端的防滥用接入（对应后端《防滥用接入指南》§2）。
///
/// <para>
/// 三件事，缺一不可：
/// </para>
/// <list type="number">
///   <item><b>每个受保护的请求都带头</b>（<see cref="Apply"/>）—— 闸门是逐请求判定的，
///         "只在激活时带"没用。</item>
///   <item><b>抓响应里的新令牌</b>（<see cref="Capture"/>）—— 官方包首次请求后服务端会下发一次，
///         存下来后续直接复用，省掉每次验签。</item>
///   <item><b>识别闸门拒绝</b>（<see cref="IsDenied"/>）—— 判据是正文 <c>code</c> 以
///         <c>csh_license</c> 开头，**不是状态码本身**：403 也可能来自别的业务拒绝。</item>
/// </list>
///
/// <para>
/// ⛔ 客户端无法自证清白，也不该试图自证：这里只负责"如实上报身份 + 如实展示结果"，
/// 所有判定都在服务端。别在客户端加什么"已激活"的本地短路。2026-11-04。
/// </para>
/// </summary>
public static class ClientAuth
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>某次请求被闸门挡下来了。UI 订阅它弹激活引导（注意：可能在后台线程触发）。</summary>
    public static event Action? ActivationRequired;

    /// <summary>激活状态变了（成功 / 清空），界面据此刷新。</summary>
    public static event Action? StateChanged;

    /// <summary>
    /// 给发往后端的请求补上身份头。**每个受保护请求都要调**。
    /// <para>
    /// 只对自建后端的域名加 —— 别的（GitHub、词典、任意第三方）不该看见这些值。
    /// </para>
    /// </summary>
    public static void Apply(HttpRequestMessage request)
    {
        try
        {
            if (!IsOwnBackend(request.RequestUri)) return;

            Set(request, "X-CSH-Build", BuildIdentity.BuildId);
            Set(request, "X-CSH-Device", DeviceFingerprint.Value);
            Set(request, "X-CSH-Ver", ShellConfig.ShellVersion);

            if (BuildIdentity.Signature.Length > 0)
                Set(request, "X-CSH-Sig", BuildIdentity.Signature);

            if (LicenseStore.Current is { } ticket)
                Set(request, "X-CSH-Lic", ticket.Token);
        }
        catch (Exception ex)
        {
            // 加头失败不该让请求发不出去 —— 让服务端去判，它自然会告诉我们需要什么
            AppLog.Exception("license", ex, "补授权请求头");
        }
    }

    /// <summary>把响应里的令牌收下来。每个收到响应的调用点都要调（成功的响应才有）。</summary>
    public static void Capture(HttpResponseMessage response)
    {
        try
        {
            if (!IsOwnBackend(response.RequestMessage?.RequestUri)) return;
            if (!response.Headers.TryGetValues("X-CSH-Lic", out var values)) return;

            var token = values.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(token)) return;

            long expiresAt = 0;
            if (response.Headers.TryGetValues("X-CSH-Lic-Exp", out var expValues)
                && long.TryParse(expValues.FirstOrDefault(), out var parsed))
            {
                expiresAt = parsed;
            }
            else
            {
                // 服务端没带到期时间时按文档给官方包的口径兜底：30 天
                expiresAt = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
            }

            if (LicenseStore.Save(token, expiresAt)) StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Exception("license", ex, "收取授权令牌");
        }
    }

    /// <summary>
    /// 这次响应是不是"需要激活"。判据：状态码 403/401 **且**正文 <c>code</c> 以
    /// <c>csh_license</c> 开头。
    /// </summary>
    public static bool IsDenied(HttpStatusCode status, string? body)
    {
        if (status is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)) return false;
        return HasLicenseCode(body);
    }

    /// <summary>只解析正文（已经读成字符串的那种），给拿不到状态码的调用点用。</summary>
    public static bool HasLicenseCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("code", out var code)) return false;

            return (code.GetString() ?? "")
                .StartsWith("csh_license", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>叫醒界面：这次请求被挡了，该引导用户激活。</summary>
    public static void RaiseActivationRequired()
    {
        try { ActivationRequired?.Invoke(); }
        catch (Exception ex) { AppLog.Exception("license", ex, "通知需要激活"); }
    }

    /// <summary>
    /// 一次完整收发：补头 → 发 → 收令牌 → 判闸门。
    /// 被挡下来时会触发 <see cref="ActivationRequired"/>，并把响应原样返回给调用方
    /// （调用方照常处理错误文案即可）。
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpRequestMessage request, CancellationToken ct = default)
    {
        Apply(request);

        var response = await client.SendAsync(request, ct).ConfigureAwait(false);

        Capture(response);

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            // 这里只读一份副本来判 code，读完把内容塞回 Content，
            // 调用方后面照样能读到同一份正文。
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            response.Content = new StringContent(text, Encoding.UTF8, "application/json");

            if (HasLicenseCode(text)) RaiseActivationRequired();
        }

        return response;
    }

    // ── 激活 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 用激活码兑换令牌（<c>POST /api/license/activate</c>）。
    /// <para>
    /// 这个端点**不过闸门**（兑换本身要求授权就死循环了），所以这里不带身份头，
    /// 只把 code / device / build / version 放在 body 里。
    /// </para>
    /// </summary>
    public static async Task<ActivateResult> ActivateAsync(string code, CancellationToken ct = default)
    {
        var normalized = NormalizeCode(code);

        if (normalized.Length != 16)
            return new ActivateResult(false, "激活码是 16 位，请检查有没有漏输或输错。");

        var payload = JsonSerializer.Serialize(new
        {
            code = normalized,
            device = DeviceFingerprint.Value,
            build = BuildIdentity.BuildId,
            version = ShellConfig.ShellVersion,
        });

        string? lastError = null;

        foreach (var baseUrl in ShellConfig.OrderedSubmitEndpoints())
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/api/license/activate")
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };

                using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (!response.IsSuccessStatusCode)
                {
                    // 服务端的话就是给人看的，直接用（文档 §6）
                    if (root.ValueKind == JsonValueKind.Object
                        && root.TryGetProperty("error", out var err)
                        && err.GetString() is { Length: > 0 } message)
                    {
                        return new ActivateResult(false, message);
                    }

                    lastError = $"服务端返回 {(int)response.StatusCode}";
                    continue;
                }

                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("token", out var tokenElement)
                    || tokenElement.GetString() is not { Length: > 0 } token)
                {
                    lastError = "服务端没有下发令牌。";
                    continue;
                }

                long expiresAt = 0;
                if (root.TryGetProperty("expiresAt", out var exp))
                {
                    if (exp.ValueKind == JsonValueKind.Number) expiresAt = exp.GetInt64();
                    else if (exp.ValueKind == JsonValueKind.String
                             && long.TryParse(exp.GetString(), out var parsed)) expiresAt = parsed;
                }

                if (!LicenseStore.Save(token, expiresAt))
                    return new ActivateResult(false, "令牌没能保存到本机，请检查磁盘权限。");

                AppLog.Info("license", $"激活成功，到期 {LicenseStore.Current?.ExpiresAtLocal:yyyy-MM-dd HH:mm}");
                StateChanged?.Invoke();
                return ActivateResult.Success;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                AppLog.Warn("license", $"激活请求失败（{baseUrl}）：{ex.Message}");
            }
        }

        return new ActivateResult(false, lastError ?? "连不上服务端，请检查网络后重试。");
    }

    /// <summary>
    /// 用户输入 → 规范化的 16 位码：去掉 <c>CSH</c> 前缀、短横线和空格，统一大写。
    /// （服务端会自动剥，这里先做一遍是为了本地就能报"长度不对"，不必白跑一次网络。）
    /// </summary>
    public static string NormalizeCode(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";

        var chars = input
            .Where(c => char.IsLetterOrDigit(c))
            .Select(char.ToUpperInvariant)
            .ToArray();

        var text = new string(chars);

        // 展示形态是 CSH-XXXX-XXXX-XXXX-XXXX，字母全是 Crockford base32，
        // 所以开头的 CSH 一定是前缀、不可能是码的一部分
        if (text.StartsWith("CSH", StringComparison.Ordinal) && text.Length > 16)
            text = text[3..];

        return text;
    }

    /// <summary>展示用：CSH-XXXX-XXXX-XXXX-XXXX。长度不对就原样返回。</summary>
    public static string FormatCode(string? code)
    {
        var text = NormalizeCode(code);
        if (text.Length != 16) return code?.Trim() ?? "";

        return $"CSH-{text[..4]}-{text[4..8]}-{text[8..12]}-{text[12..]}";
    }

    // ── 内部 ─────────────────────────────────────────────────────────────

    private static void Set(HttpRequestMessage request, string name, string value)
    {
        request.Headers.Remove(name);
        request.Headers.TryAddWithoutValidation(name, value);
    }

    /// <summary>
    /// 这条 URL 是不是我们自己的后端。
    /// ⚠️ 按主机名后缀判，不按完整字符串：入口清单里三个域名都是同一套后端，
    ///    以后加镜像也不该改这里。
    /// </summary>
    private static bool IsOwnBackend(Uri? uri)
    {
        if (uri is null) return false;

        foreach (var candidate in ServiceEndpoints.All)
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var backend)
                && string.Equals(uri.Host, backend.Host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
