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

/// <summary>
/// 模型调用（2026-10-18）。只认 OpenAI 兼容协议，见 <see cref="AiProviders"/> 的说明。
///
/// <para>
/// ⛔⛔ 三条铁律：
/// <list type="number">
///   <item><b>密钥不进日志</b>。任何分支都只记状态码与错误类型，⛔ 不把请求头、请求体打出去。</item>
///   <item><b>错误要说人话</b>。<c>401</c> 不能原样甩给用户，要翻成"密钥无效"。</item>
///   <item><b>流式必须可取消</b>。用户点「停止」要立刻停，不能等模型吐完。</item>
/// </list>
/// </para>
/// </summary>
public static class AiClient
{
    /// <summary>
    /// 单例 <see cref="HttpClient"/>。⛔ 别每次请求都 new —— 短连接会把端口耗光，
    /// 而 AI 请求是长连接、多次往返的，复用收益很明显。
    /// </summary>
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        // 思考型模型（如 deepseek-reasoner）首个 token 可能要等几十秒，给足余量
        Timeout = TimeSpan.FromSeconds(120),
    };

    /// <summary>
    /// 连通性测试：发一个 <c>max_tokens=1</c> 的最小请求。
    /// ⚠️ 只花一个 token 的钱，但足以分辨"密钥错 / 地址错 / 模型错 / 网络不通"。
    /// </summary>
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

    /// <summary>
    /// 走一轮对话：流式吐文本，同时把模型要求的工具调用收齐。
    ///
    /// <para>
    /// ⛔ 这个函数**只解析、不执行**。工具由调用方（<c>AiChatService</c>）执行完再回灌，
    /// 这样"模型能做什么"完全由宿主决定，模型没有任何直接能力。
    /// </para>
    /// <para>
    /// ⚠️ 工具调用在 SSE 里是**分片**到达的：第一个 chunk 给 id 与函数名，后续 chunk 只追加
    /// <c>arguments</c> 的字符串片段。必须按 <c>index</c> 累积，不能见到一个就跑一个。
    /// </para>
    /// </summary>
    /// <param name="onDelta">每收到一段文本回调一次（UI 用它做逐字渲染）。</param>
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

            // SSE：只关心 data: 行，注释行（以 : 开头）与 event: 行一律跳过
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

    /// <summary>流式响应的一块增量：正文字 + 工具调用分片。</summary>
    private sealed class ToolCallBuffer
    {
        public string Id = "";
        public string Name = "";
        public StringBuilder Args = new();
    }

    /// <summary>处理一块 SSE 数据。返回本块携带的用量（通常只有最后一块有，其余返回 null）。</summary>
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

            // ⚠️ usage 要在看 choices 之前处理：带用量那一块的 choices 往往是**空数组**，
            //    先判断 choices 就会把用量直接丢掉。
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

            // 思考过程（DeepSeek / 通义 / Kimi / GLM 都叫 reasoning_content，字段名是统一的）
            if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
            {
                var think = rc.GetString();
                if (!string.IsNullOrEmpty(think))
                {
                    reasoning.Append(think);
                    onReasoning?.Invoke(think);
                }
            }

            // 正文
            if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            {
                var s = c.GetString();
                if (!string.IsNullOrEmpty(s))
                {
                    content.Append(s);
                    onDelta?.Invoke(s);
                }
            }

            // 工具调用（分片）
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
            // 个别服务商会插非 JSON 的心跳，忽略即可
            return null;
        }
    }

    /// <summary>拼请求体。工具清单只在传了的时候才带，避免"没装工具包却收到空 tools 数组"。</summary>
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

        // 流式响应默认**不**返回用量，必须显式索取 —— 否则界面上"本次消耗 token"永远是 0。
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

    /// <summary>
    /// 按服务商翻译"思考开关"（2026-10-18，逐家对着官方文档核过）。
    ///
    /// <para>
    /// ⚠️ 同一个能力，四家四种写法：
    /// <list type="bullet">
    ///   <item>DeepSeek / Kimi / GLM：嵌套 <c>thinking: { type }</c>，Kimi 要 <c>keep</c>、GLM 要 <c>clear_thinking</c>；</item>
    ///   <item>通义：顶层布尔 <c>enable_thinking</c>，且**开启时必须带 <c>incremental_output</c>**，否则直接报错；</item>
    ///   <item>OpenAI o 系列：没有开关，只有 <c>reasoning_effort</c>。</item>
    /// </list>
    /// ⛔ 别把用户选的值原样透传 —— 发错字段轻则无效，重则 400。
    /// </para>
    /// </summary>
    private static void ApplyThinking(Dictionary<string, object?> body, AiConfig cfg)
    {
        var profile = cfg.ThinkingProfile;

        // 强制思考的模型（glm-5.3 等）关不掉，传 disabled 会直接报错
        var enabled = profile.ForcedThinking || cfg.Thinking;

        switch (profile.Style)
        {
            case ThinkingStyle.ThinkingObject:
                body["thinking"] = new { type = enabled ? "enabled" : "disabled" };
                break;

            case ThinkingStyle.EnableThinkingFlag:
                body["enable_thinking"] = enabled;
                // DashScope 在 enable_thinking=true 时不带这个参数会报错
                if (enabled) body["incremental_output"] = true;
                break;

            case ThinkingStyle.ThinkingObjectKeep:
                // keep=all：把历史思考留在上下文里，多步工具调用才连得上
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

    /// <summary>强度取值各家不同：DeepSeek/GLM 认 max，OpenAI 最高只到 high。</summary>
    private static string NormalizeEffort(string? effort, string providerId)
    {
        var value = (effort ?? "").Trim().ToLowerInvariant();
        if (value != "low" && value != "high" && value != "max") value = "high";

        if (providerId == "openai" && value == "max") return "high";
        return value;
    }

    /// <summary>
    /// 一条消息的线上格式。<c>assistant</c> 带工具调用时 <c>content</c> 必须是 null，
    /// <c>tool</c> 消息必须带 <c>tool_call_id</c> —— 少一样服务端直接 400。
    /// </summary>
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

            // ⚠️ 带 tools 的思考模型（DeepSeek / Kimi / GLM）要求历史 reasoning 原样回传，
            //    缺了会返回 400。不回传思考的服务商就别多塞这个字段。
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

    // ══════════════════════════════════════════════════════
    //  内部
    // ══════════════════════════════════════════════════════

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

        // 本地模型通常不校验密钥，空着就不发 Authorization 头（有些实现见到空 Bearer 会报错）
        if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey.Trim());

        return request;
    }

    /// <summary>把 HTTP 状态码翻成用户能看懂的一句话。</summary>
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
