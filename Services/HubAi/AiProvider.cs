using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassSoftwareHub.Desktop.Services.HubAi;

/// <summary>
/// 思考开关在线上格式里的写法（2026-10-18 按各家官方文档核对）。
///
/// <para>
/// 同一个"开启思考"，四家四种写法：DeepSeek / Kimi / GLM 是嵌套对象 <c>thinking.type</c>，
/// 通义是顶层布尔 <c>enable_thinking</c>。所以 ⛔ 不能把用户选的"开/关"直接透传，
/// 必须按服务商翻译一遍 —— 发错字段轻则无效，重则 400。
/// </para>
/// </summary>
public enum ThinkingStyle
{
    /// <summary>不发送任何思考相关参数（模型没有这个能力，或用户不需要）。</summary>
    None,

    /// <summary><c>{"thinking": {"type": "enabled"}}</c> —— DeepSeek。</summary>
    ThinkingObject,

    /// <summary><c>enable_thinking: true</c> —— 通义千问；必须同时带 <c>incremental_output: true</c>。</summary>
    EnableThinkingFlag,

    /// <summary><c>{"thinking": {"type": "enabled", "keep": "all"}}</c> —— Kimi，顺带保留历史思考。</summary>
    ThinkingObjectKeep,

    /// <summary><c>{"thinking": {"type": "enabled", "clear_thinking": false}}</c> —— 智谱 GLM。</summary>
    ThinkingObjectPreserve,

    /// <summary>OpenAI 的 o 系列：只有 <c>reasoning_effort</c>，且不回传思考内容。</summary>
    EffortOnly,
}

/// <summary>各家的思考能力描述。</summary>
public sealed record ThinkingProfile(
    ThinkingStyle Style,
    bool SupportsToggle = true,
    bool SupportsEffort = false,
    /// <summary>带 tools 时必须把历史 <c>reasoning_content</c> 原样回传，否则服务端 400。</summary>
    bool RequiresReasoningEcho = false,
    /// <summary>强制开启思考、不能关闭（如 glm-5.3、kimi-k2.7-code）。</summary>
    bool ForcedThinking = false);

/// <summary>一个模型服务商的预设（2026-10-18）。</summary>
public sealed record AiProvider(
    string Id,
    string Name,
    string BaseUrl,
    string DefaultModel,
    IReadOnlyList<string> Models,
    string Note = "",
    ThinkingProfile? Thinking = null)
{
    public ThinkingProfile ThinkingProfile => Thinking ?? new ThinkingProfile(ThinkingStyle.ThinkingObject);
}

public static class AiProviders
{
    public const string CustomId = "custom";

    public static readonly IReadOnlyList<AiProvider> All = new[]
    {
        new AiProvider("deepseek", "DeepSeek", "https://api.deepseek.com/v1", "deepseek-flash",
            new[] { "deepseek-flash", "deepseek-v4-pro" },
            "国内直连，中文好，价格低。思考默认开启。",
            new ThinkingProfile(ThinkingStyle.ThinkingObject, SupportsToggle: true, SupportsEffort: true,
                RequiresReasoningEcho: true)),

        new AiProvider("tongyi", "通义千问", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus",
            new[] { "qwen-plus", "qwen-turbo", "qwen-max" },
            "阿里云百炼，有免费额度。",
            new ThinkingProfile(ThinkingStyle.EnableThinkingFlag, SupportsToggle: true, SupportsEffort: false)),

        new AiProvider("kimi", "Kimi", "https://api.moonshot.cn/v1", "kimi-k2.6",
            new[] { "kimi-k2.6", "kimi-k2.5" },
            "月之暗面，长文本见长。",
            new ThinkingProfile(ThinkingStyle.ThinkingObjectKeep, SupportsToggle: true, SupportsEffort: false,
                RequiresReasoningEcho: true)),

        new AiProvider("zhipu", "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4.7-flash",
            new[] { "glm-4.7-flash", "glm-4.6", "glm-5.3" },
            "glm-4.7-flash 免费。glm-5.3 强制思考、不能关闭。",
            new ThinkingProfile(ThinkingStyle.ThinkingObjectPreserve, SupportsToggle: true, SupportsEffort: true,
                RequiresReasoningEcho: true)),

        new AiProvider("minimax", "MiniMax", "https://api.minimax.chat/v1", "MiniMax-M2",
            new[] { "MiniMax-M2" },
            "思考参数按通用 thinking 对象发送。",
            new ThinkingProfile(ThinkingStyle.ThinkingObject)),

        new AiProvider("openai", "OpenAI", "https://api.openai.com/v1", "gpt-4o-mini",
            new[] { "gpt-4o-mini", "gpt-4o", "o3-mini" },
            "国内访问可能需要代理。o 系列只有推理强度，不返回思考过程。",
            new ThinkingProfile(ThinkingStyle.EffortOnly, SupportsToggle: false, SupportsEffort: true)),

        new AiProvider("local", "本地模型", "http://127.0.0.1:11434/v1", "qwen2.5:7b",
            Array.Empty<string>(),
            "Ollama / LM Studio 默认地址，密钥可留空。",
            new ThinkingProfile(ThinkingStyle.ThinkingObject, SupportsToggle: true, SupportsEffort: false)),

        new AiProvider(CustomId, "自定义", "", "",
            Array.Empty<string>(),
            "自行填写兼容 OpenAI 的地址与模型名。",
            new ThinkingProfile(ThinkingStyle.ThinkingObject, SupportsToggle: true, SupportsEffort: false)),
    };

    public static AiProvider ById(string? id)
        => All.FirstOrDefault(p => p.Id == id) ?? All[^1];

    /// <summary>
    /// 模型 ID → 给人看的显示名（2026-10-18）。
    ///
    /// <para>
    /// ⚠️ **两者不一样**：接口里的 ID 是 <c>deepseek-flash</c>，但它实际对应的模型版本叫
    /// <b>DeepSeek-V4.1-Flash</b>。界面上直接把 ID 摊给用户会显得很原始（Nick 就问了
    /// 「不是 V4.1 flash 吗，为什么写的是 V4」）。
    /// </para>
    /// <para>
    /// 认不出的 ID 原样返回 —— 用户手填的模型名不该被改写。
    /// </para>
    /// </summary>
    public static string ModelLabel(string providerId, string modelId) => modelId switch
    {
        "deepseek-flash" => "DeepSeek-V4.1-Flash",
        "deepseek-v4-pro" => "DeepSeek-V4-Pro",
        "qwen-plus" => "Qwen-Plus",
        "qwen-turbo" => "Qwen-Turbo",
        "qwen-max" => "Qwen-Max",
        "kimi-k2.6" => "Kimi-K2.6",
        "kimi-k2.5" => "Kimi-K2.5",
        "glm-4.7-flash" => "GLM-4.7-Flash",
        "glm-4.6" => "GLM-4.6",
        "glm-5.3" => "GLM-5.3",
        "MiniMax-M2" => "MiniMax-M2",
        "gpt-4o-mini" => "GPT-4o-mini",
        "gpt-4o" => "GPT-4o",
        "o3-mini" => "o3-mini",
        _ => modelId,
    };
}

/// <summary>一次请求需要的配置。⚠️ <see cref="ApiKey"/> 只在内存里流转，不落盘、不进日志。</summary>
public sealed record AiConfig(
    string ProviderId,
    string BaseUrl,
    string Model,
    string ApiKey,
    double Temperature = 0.7,
    bool Thinking = true,
    string Effort = "high",
    /// <summary>上下文上限（近似字符数，0 = 不限制）。超过就丢最早的历史。</summary>
    int ContextLimit = 0)
{
    /// <summary>地址是否够用（本地模型允许不带密钥）。</summary>
    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);

    public AiProvider Provider => AiProviders.ById(ProviderId);

    public ThinkingProfile ThinkingProfile => Provider.ThinkingProfile;
}

/// <summary>一条对话消息。<c>role</c> 取 <c>system</c> / <c>user</c> / <c>assistant</c> / <c>tool</c>。</summary>
public sealed record AiMessage(string Role, string Content)
{
    /// <summary>role=tool 时，指向它回应的是哪一次调用。</summary>
    public string? ToolCallId { get; init; }

    /// <summary>role=assistant 且模型要求调用工具时，这里装着它要调的东西。</summary>
    public IReadOnlyList<AiToolCall>? ToolCalls { get; init; }

    /// <summary>
    /// assistant 这轮的思考过程。
    /// ⚠️ 带 tools 的思考模型（DeepSeek / Kimi / GLM）要求它**原样回传**，少一个就 400。
    /// </summary>
    public string? Reasoning { get; init; }

    public static AiMessage System(string text) => new("system", text);
    public static AiMessage User(string text) => new("user", text);
    public static AiMessage Assistant(string text) => new("assistant", text);

    /// <summary>宿主执行完工具后回灌给模型的那条消息。</summary>
    public static AiMessage Tool(string callId, string content)
        => new("tool", content) { ToolCallId = callId };
}

/// <summary>模型要求执行的一次工具调用。参数是 JSON 字符串（模型给的原样，别自己转对象）。</summary>
public sealed record AiToolCall(string Id, string Name, string Arguments);

/// <summary>一次请求的用量。流式下由服务端在最后一个 chunk 里给（需要 <c>stream_options.include_usage</c>）。</summary>
public sealed record AiUsage(int PromptTokens, int CompletionTokens, int TotalTokens)
{
    public static readonly AiUsage Empty = new(0, 0, 0);

    public bool IsEmpty => TotalTokens == 0;

    public AiUsage Plus(AiUsage other) => new(
        PromptTokens + other.PromptTokens,
        CompletionTokens + other.CompletionTokens,
        TotalTokens + other.TotalTokens);
}

/// <summary>一轮模型响应：说了什么 + 想了什么 + 想调哪些工具 + 用了多少 token。</summary>
public sealed record AiTurn(
    string Content,
    string Reasoning,
    IReadOnlyList<AiToolCall> ToolCalls,
    AiUsage Usage)
{
    public bool WantsTools => ToolCalls.Count > 0;
}

/// <summary>连通性测试结果。<see cref="Message"/> 是给用户看的书面语。</summary>
public sealed record AiTestResult(bool Ok, string Message, long ElapsedMs)
{
    public static AiTestResult Fail(string message, long ms = 0) => new(false, message, ms);
    public static AiTestResult Pass(string message, long ms) => new(true, message, ms);
}
