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




public sealed record ActivateResult(bool Ok, string Message)
{
    public static readonly ActivateResult Success = new(true, "");
}





















public static class ClientAuth
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    
    public static event Action? ActivationRequired;

    
    public static event Action? StateChanged;

    
    
    
    
    
    
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
            
            AppLog.Exception("license", ex, "补授权请求头");
        }
    }

    
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
                
                expiresAt = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
            }

            if (LicenseStore.Save(token, expiresAt)) StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Exception("license", ex, "收取授权令牌");
        }
    }

    
    
    
    
    public static bool IsDenied(HttpStatusCode status, string? body)
    {
        if (status is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)) return false;
        return HasLicenseCode(body);
    }

    
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

    
    public static void RaiseActivationRequired()
    {
        try { ActivationRequired?.Invoke(); }
        catch (Exception ex) { AppLog.Exception("license", ex, "通知需要激活"); }
    }

    
    
    
    
    
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpRequestMessage request, CancellationToken ct = default)
    {
        Apply(request);

        var response = await client.SendAsync(request, ct).ConfigureAwait(false);

        Capture(response);

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            
            
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            response.Content = new StringContent(text, Encoding.UTF8, "application/json");

            if (HasLicenseCode(text)) RaiseActivationRequired();
        }

        return response;
    }

    

    
    
    
    
    
    
    
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

    
    
    
    
    public static string NormalizeCode(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";

        var chars = input
            .Where(c => char.IsLetterOrDigit(c))
            .Select(char.ToUpperInvariant)
            .ToArray();

        var text = new string(chars);

        
        
        if (text.StartsWith("CSH", StringComparison.Ordinal) && text.Length > 16)
            text = text[3..];

        return text;
    }

    
    public static string FormatCode(string? code)
    {
        var text = NormalizeCode(code);
        if (text.Length != 16) return code?.Trim() ?? "";

        return $"CSH-{text[..4]}-{text[4..8]}-{text[8..12]}-{text[12..]}";
    }

    

    private static void Set(HttpRequestMessage request, string name, string value)
    {
        request.Headers.Remove(name);
        request.Headers.TryAddWithoutValidation(name, value);
    }

    
    
    
    
    
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
