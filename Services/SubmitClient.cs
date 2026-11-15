using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services.License;

namespace ClassSoftwareHub.Desktop.Services;
















internal static class SubmitClient
{
    
    internal sealed record Reply(bool Ok, string Message, string? Url);

    private static readonly HttpClient Http = CreateClient();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    
    
    
    
    internal static async Task<Reply?> PostAsync(
        string route, string payloadJson, string okFallbackMessage, CancellationToken ct = default)
    {
        foreach (var baseUrl in ShellConfig.OrderedSubmitEndpoints())
        {
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(TimeSpan.FromMilliseconds(ShellConfig.SubmitTimeoutMs));

                using var content = new StringContent(payloadJson, Utf8NoBom, "application/json");
                using var response = await Http
                    .PostAsync(baseUrl + route, content, budget.Token)
                    .ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var ok = root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;
                var hasError = root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String;
                if (!ok && !hasError)
                {
                    
                    
                    Core.AppLog.Step("submit", $"入口 {baseUrl} 回的不是提交服务响应 → 换下一个");
                    continue;
                }

                ShellConfig.RememberSubmitEndpoint(baseUrl);

                if (ok)
                {
                    var message = root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String
                        ? msg.GetString()
                        : null;
                    var url = root.TryGetProperty("url", out var link) && link.ValueKind == JsonValueKind.String
                        ? link.GetString()
                        : null;
                    return new Reply(true, string.IsNullOrWhiteSpace(message) ? okFallbackMessage : message!, url);
                }

                return new Reply(false, (hasError ? err.GetString() : null) ?? "服务端没有接受这条内容。", null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                
                
                
                Core.AppLog.Warn("submit",
                    $"入口 {baseUrl} 提交失败（{ex.GetType().Name}: {ex.Message}）→ 试下一个");
            }
        }

        Core.AppLog.Warn("submit", "所有提交入口都不通（网络或地区限制）");
        return null;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new AuthHandler(new HttpClientHandler { AllowAutoRedirect = true }))
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClassSoftwareHub/1.2 submit");
        return client;
    }
}
