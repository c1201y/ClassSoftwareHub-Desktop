using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassSoftwareHub.Desktop.Services.HubAi;










public enum ThinkingStyle
{
    
    None,

    
    ThinkingObject,

    
    EnableThinkingFlag,

    
    ThinkingObjectKeep,

    
    ThinkingObjectPreserve,

    
    EffortOnly,
}


public sealed record ThinkingProfile(
    ThinkingStyle Style,
    bool SupportsToggle = true,
    bool SupportsEffort = false,
    
    bool RequiresReasoningEcho = false,
    
    bool ForcedThinking = false);


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


public sealed record AiConfig(
    string ProviderId,
    string BaseUrl,
    string Model,
    string ApiKey,
    double Temperature = 0.7,
    bool Thinking = true,
    string Effort = "high",
    
    int ContextLimit = 0)
{
    
    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);

    public AiProvider Provider => AiProviders.ById(ProviderId);

    public ThinkingProfile ThinkingProfile => Provider.ThinkingProfile;
}


public sealed record AiMessage(string Role, string Content)
{
    
    public string? ToolCallId { get; init; }

    
    public IReadOnlyList<AiToolCall>? ToolCalls { get; init; }

    
    
    
    
    public string? Reasoning { get; init; }

    public static AiMessage System(string text) => new("system", text);
    public static AiMessage User(string text) => new("user", text);
    public static AiMessage Assistant(string text) => new("assistant", text);

    
    public static AiMessage Tool(string callId, string content)
        => new("tool", content) { ToolCallId = callId };
}


public sealed record AiToolCall(string Id, string Name, string Arguments);


public sealed record AiUsage(int PromptTokens, int CompletionTokens, int TotalTokens)
{
    public static readonly AiUsage Empty = new(0, 0, 0);

    public bool IsEmpty => TotalTokens == 0;

    public AiUsage Plus(AiUsage other) => new(
        PromptTokens + other.PromptTokens,
        CompletionTokens + other.CompletionTokens,
        TotalTokens + other.TotalTokens);
}


public sealed record AiTurn(
    string Content,
    string Reasoning,
    IReadOnlyList<AiToolCall> ToolCalls,
    AiUsage Usage)
{
    public bool WantsTools => ToolCalls.Count > 0;
}


public sealed record AiTestResult(bool Ok, string Message, long ElapsedMs)
{
    public static AiTestResult Fail(string message, long ms = 0) => new(false, message, ms);
    public static AiTestResult Pass(string message, long ms) => new(true, message, ms);
}
