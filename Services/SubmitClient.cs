using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 自建提交服务（Cloudflare Worker <c>classhub</c>）的**通用调用层**。
///
/// 客户端只发内容，**GitHub 令牌在服务端** —— 所以这三条路都不用登录 GitHub：
///   · 提交软件   → <c>POST {入口}/api/submit</c>
///   · 回声洞投稿 → <c>POST {入口}/api/echocave</c>
///   · 反馈中心   → <c>POST {入口}/api/feedback</c>（见 <see cref="FeedbackSubmit"/>）
///
/// 入口有多个域名（见 <see cref="ShellConfig.OrderedSubmitEndpoints"/>，上次成功的排最前），
/// 按顺序试；**只有拿到本服务约定的 JSON**（成功回 <c>success:true</c>、校验失败回 <c>error</c>）
/// 才算这个入口可用 —— 回源还没生效时 nginx 会吐 HTML 错误页，那不是接口响应，得换下一个。
///
/// ⚠️ 这一层**不抛异常**（取消失常除外）：连不上、超时、响应不是 JSON，一律返回 <c>null</c>
///    表示"所有入口都不通"，由调用方决定怎么跟用户说 —— 三条路各自的兜底话术不一样。
/// </summary>
internal static class SubmitClient
{
    /// <summary>服务端响应。<see cref="Url"/> 只有服务端愿意回时才有（反馈那条约它带回议题地址）。</summary>
    internal sealed record Reply(bool Ok, string Message, string? Url);

    private static readonly HttpClient Http = CreateClient();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 往 <paramref name="route"/> 发一份 JSON。返回 <c>null</c> = 所有入口都不通。
    /// <paramref name="okFallbackMessage"/> 是成功但服务端没给 <c>message</c> 时的兜底文案。
    /// </summary>
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
                if (!ok && !hasError) continue;   // 不是这个接口的响应（回源还没生效时会返回 nginx 错误页）

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
            catch
            {
                // 连不上 / 超时 / 响应不是 JSON（路由还没上）→ 换下一个入口
            }
        }

        return null;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClassSoftwareHub/1.2 submit");
        return client;
    }
}
