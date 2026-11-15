using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.HubAi;













public static class AiClient
{
    
    
    
    
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        
        Timeout = TimeSpan.FromSeconds(120),
    };

    
    
    
    
    public static async Task<AiTestResult> TestAsync(AiConfig cfg, CancellationToken ct = default)
    {
        if (!cfg.IsUsable) return AiTestResult.Fail("请先填写服务地址和模型名。");

        var watch = Stopwatch.StartNew();

        try
        {
            using var request = BuildRequest(EndpointOf(cfg.BaseUrl), cfg, new
            {
                model = cfg.Model,
                messages = new[] { new { role = "user", content = "ping" } },
                max_tokens = 1,
                stream = false,
            });

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            watch.Stop();

            if (response.IsSuccessStatusCode)
            {
                AppLog.Step("hubai", $"连通性测试通过（{watch.ElapsedMilliseconds}ms）");
                return AiTestResult.Pass($"连接正常（{watch.ElapsedMilliseconds} ms）", watch.ElapsedMilliseconds);
            }

            var reason = Explain(response.StatusCode, body);
            AppLog.Warn("hubai", $"连通性测试失败：{response.StatusCode} {reason}");
            return AiTestResult.Fail(reason, watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            return AiTestResult.Fail("请求超时或已取消。若开启了代理，请确认代理可用。", watch.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            watch.Stop();
            AppLog.Exception("hubai", ex, "连通性测试（网络层）");
            return AiTestResult.Fail($"无法连接：{ex.Message}", watch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            watch.Stop();
            AppLog.Exception("hubai", ex, "连通性测试");
            return AiTestResult.Fail("测试失败，详情见日志。", watch.ElapsedMilliseconds);
        }
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    public static async Task<AiTurn> StreamTurnAsync(
        AiConfig cfg,
        IReadOnlyList<AiMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        Action<string>? onDelta,
        Action<string>? onReasoning = null,
        CancellationToken ct = default)
    {
        if (!cfg.IsUsable) throw new InvalidOperationException("尚未配置模型服务。");

        using var request = BuildRequest(EndpointOf(cfg.BaseUrl), cfg, BuildBody(cfg, messages, tools, stream: true));
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var reason = Explain(response.StatusCode, body);
            AppLog.Warn("hubai", $"流式请求失败：{response.StatusCode} {reason}");
            throw new InvalidOperationException(reason);
        }

        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var buffers = new Dictionary<int, ToolCallBuffer>();
        var usage = AiUsage.Empty;

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;

            
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line.AsSpan(5).Trim().ToString();
            if (payload.Length == 0) continue;
            if (payload == "[DONE]") break;

            var chunkUsage = AccumulateChunk(payload, content, reasoning, buffers, onDelta, onReasoning);
            if (chunkUsage is not null) usage = chunkUsage;
        }

        var calls = buffers
            .OrderBy(kv => kv.Key)
            .Select(kv => new AiToolCall(
                kv.Value.Id.Length > 0 ? kv.Value.Id : $"call_{kv.Key}",
                kv.Value.Name,
                kv.Value.Args.ToString()))
            .Where(c => c.Name.Length > 0)
            .ToList();

        return new AiTurn(content.ToString(), reasoning.ToString(), calls, usage);
    }

    
    private sealed class ToolCallBuffer
    {
        public string Id = "";
        public string Name = "";
        public StringBuilder Args = new();
    }

    
    private static AiUsage? AccumulateChunk(
        string json,
        StringBuilder content,
        StringBuilder reasoning,
        Dictionary<int, ToolCallBuffer> buffers,
        Action<string>? onDelta,
        Action<string>? onReasoning)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            
            
            AiUsage? usage = null;
            if (doc.RootElement.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                usage = new AiUsage(
                    u.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number ? pt.GetInt32() : 0,
                    u.TryGetProperty("completion_tokens", out var ctk) && ctk.ValueKind == JsonValueKind.Number ? ctk.GetInt32() : 0,
                    u.TryGetProperty("total_tokens", out var tt) && tt.ValueKind == JsonValueKind.Number ? tt.GetInt32() : 0);
            }

            if (!doc.RootElement.TryGetProperty("choices", out var choices))
                return usage;

            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return usage;

            if (!choices[0].TryGetProperty("delta", out var delta))
                return usage;

            
            if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
            {
                var think = rc.GetString();
                if (!string.IsNullOrEmpty(think))
                {
                    reasoning.Append(think);
                    onReasoning?.Invoke(think);
                }
            }

            
            if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            {
                var s = c.GetString();
                if (!string.IsNullOrEmpty(s))
                {
                    content.Append(s);
                    onDelta?.Invoke(s);
                }
            }

            
            if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcs.EnumerateArray())
                {
                    var index = tc.TryGetProperty("index", out var idx) && idx.ValueKind == JsonValueKind.Number
                        ? idx.GetInt32()
                        : 0;

                    if (!buffers.TryGetValue(index, out var buffer))
                    {
                        buffer = new ToolCallBuffer();
                        buffers[index] = buffer;
                    }

                    if (tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        buffer.Id = id.GetString() ?? buffer.Id;

                    if (!tc.TryGetProperty("function", out var fn)) continue;

                    if (fn.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                        buffer.Name = name.GetString() ?? buffer.Name;

                    if (fn.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String)
                        buffer.Args.Append(args.GetString());
                }
            }

            return usage;
        }
        catch (JsonException)
        {
            
            return null;
        }
    }

    
    private static Dictionary<string, object?> BuildBody(
        AiConfig cfg,
        IReadOnlyList<AiMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        bool stream)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = cfg.Model,
            ["messages"] = messages.Select(m => ToPayload(m, cfg)).ToList(),
            ["stream"] = stream,
            ["temperature"] = cfg.Temperature,
        };

        
        if (stream) body["stream_options"] = new { include_usage = true };

        if (tools is { Count: > 0 })
        {
            body["tools"] = tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Id,
                    description = t.Description,
                    parameters = t.Parameters,
                },
            }).ToList();
        }

        ApplyThinking(body, cfg);

        return body;
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    private static void ApplyThinking(Dictionary<string, object?> body, AiConfig cfg)
    {
        var profile = cfg.ThinkingProfile;

        
        var enabled = profile.ForcedThinking || cfg.Thinking;

        switch (profile.Style)
        {
            case ThinkingStyle.ThinkingObject:
                body["thinking"] = new { type = enabled ? "enabled" : "disabled" };
                break;

            case ThinkingStyle.EnableThinkingFlag:
                body["enable_thinking"] = enabled;
                
                if (enabled) body["incremental_output"] = true;
                break;

            case ThinkingStyle.ThinkingObjectKeep:
                
                body["thinking"] = new { type = enabled ? "enabled" : "disabled", keep = "all" };
                break;

            case ThinkingStyle.ThinkingObjectPreserve:
                body["thinking"] = new { type = enabled ? "enabled" : "disabled", clear_thinking = false };
                break;

            case ThinkingStyle.EffortOnly:
            case ThinkingStyle.None:
            default:
                break;
        }

        if (profile.SupportsEffort && enabled)
            body["reasoning_effort"] = NormalizeEffort(cfg.Effort, cfg.ProviderId);
    }

    
    private static string NormalizeEffort(string? effort, string providerId)
    {
        var value = (effort ?? "").Trim().ToLowerInvariant();
        if (value != "low" && value != "high" && value != "max") value = "high";

        if (providerId == "openai" && value == "max") return "high";
        return value;
    }

    
    
    
    
    private static object ToPayload(AiMessage m, AiConfig cfg)
    {
        if (m.Role == "assistant")
        {
            var payload = new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = m.Content.Length > 0 ? m.Content : null,
            };

            if (m.ToolCalls is { Count: > 0 })
            {
                payload["tool_calls"] = m.ToolCalls.Select(t => new
                {
                    id = t.Id,
                    type = "function",
                    function = new { name = t.Name, arguments = t.Arguments },
                }).ToList();
            }

            
            
            if (cfg.ThinkingProfile.RequiresReasoningEcho && !string.IsNullOrEmpty(m.Reasoning))
                payload["reasoning_content"] = m.Reasoning;

            return payload;
        }

        if (m.Role == "tool")
        {
            return new { role = "tool", tool_call_id = m.ToolCallId ?? "", content = m.Content };
        }

        return new { role = m.Role, content = m.Content };
    }

    
    
    

    private static string EndpointOf(string baseUrl)
    {
        var b = (baseUrl ?? "").Trim().TrimEnd('/');
        return b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? b
            : b + "/chat/completions";
    }

    private static HttpRequestMessage BuildRequest(string url, AiConfig cfg, object body)
    {
        var json = JsonSerializer.Serialize(body);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        
        if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey.Trim());

        return request;
    }

    
    private static string Explain(HttpStatusCode status, string? body)
    {
        var snippet = (body ?? "").Trim();
        if (snippet.Length > 160) snippet = snippet[..160] + "…";

        return status switch
        {
            HttpStatusCode.Unauthorized => "密钥无效或未填写。",
            HttpStatusCode.Forbidden => "密钥无权访问该模型，请检查服务商后台的权限设置。",
            HttpStatusCode.NotFound => "地址或模型名不存在，请检查服务地址与模型名称。",
            HttpStatusCode.TooManyRequests => "请求过于频繁或额度已用尽，请稍后再试。",
            HttpStatusCode.PaymentRequired => "该账户余额不足。",
            _ when (int)status >= 500 => $"服务商暂时不可用（HTTP {(int)status}）。",
            _ => $"请求失败（HTTP {(int)status}）" + (snippet.Length > 0 ? $"：{snippet}" : "。"),
        };
    }
}
