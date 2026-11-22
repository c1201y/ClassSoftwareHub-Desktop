using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services.Diagnostics;

namespace ClassSoftwareHub.Desktop.Services.HubAi;
















public sealed class AiChatService
{
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
#if DEBUG
    private static readonly TimeSpan AnswerBudget = ResolveBudget();

    private static TimeSpan ResolveBudget()
    {
        var raw = Environment.GetEnvironmentVariable("CSH_AI_BUDGET_SECONDS");
        if (int.TryParse(raw, out var seconds) && seconds > 0)
        {
            AppLog.Warn("hubai", $"调试覆盖：本轮预算改为 {seconds} 秒（来自 CSH_AI_BUDGET_SECONDS）");
            return TimeSpan.FromSeconds(seconds);
        }

        return AiTimeBudget.Default;
    }
#else
    private static readonly TimeSpan AnswerBudget = AiTimeBudget.Default;
#endif

    
    private const int MaxHistoryMessages = 20;

    private readonly List<AiMessage> _messages = new();

    
    
    
    
    private readonly List<AiMessage> _pendingDrop = new();

    
    private string? _historySummary;

    
    private string? _baseSystemPrompt;

    
    private string? _promptTemplateId;

    
    private const int SummarizeTriggerChars = 3000;

    
    private const int SummarizeInputChars = 8000;

    private const string SummaryHeader = "【早前对话摘要】";

    
    
    
    
    
    
    
    private const string ContextOpen =
        "\n\n===== 当前页面的现场数据（程序按界面现状生成，回答涉及这些数字与清单时以此为准；" +
        "不要整段复述给用户，也不要再让用户自己去翻）=====\n";

    private const string ContextClose = "\n===== 现场数据结束 =====";

    private const string SummaryPrompt =
        """
        你是对话压缩器。把下面这段更早的对话压成不超过 200 字的要点，供后续继续对话使用。

        必须保留：用户的真实目标、已经确认的结论、用户给出的硬性约束、还没做完的事。
        不要保留：寒暄、重复的追问、工具返回的原始清单、已经作废的中间推理。
        只输出要点本身，不要开场白、不要评论、不要分点标题。
        """;

    
    public ToolSession Session { get; } = new();

    public void Reset()
    {
        _messages.Clear();
        _pendingDrop.Clear();
        _historySummary = null;
        _baseSystemPrompt = null;
        _promptTemplateId = null;
        Session.Reset();
    }

    
    public IReadOnlyList<AiMessage> Snapshot() => _messages.ToList();

    
    
    
    
    
    public void Restore(IEnumerable<AiMessage> messages)
    {
        _messages.Clear();
        
        _pendingDrop.Clear();
        _historySummary = null;
        _baseSystemPrompt = null;
        _promptTemplateId = null;
        Session.Reset();

        foreach (var message in messages)
        {
            if (message.Role == "system") continue;

            
            
            
            
            if (message.Role == "assistant" && string.IsNullOrWhiteSpace(message.Content)) continue;

            
            
            
            
            
            
            
            if (message.Role == "tool") continue;

            _messages.Add(message);
        }
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    public async Task<AiUsage> RunAsync(
        AiConfig cfg,
        MachineSnapshot snapshot,
        IReadOnlyList<ToolDefinition> tools,
        string userText,
        Action<string>? onDelta,
        Action<string>? onStatus,
        Action? onNewBubble,
        CancellationToken ct,
        Action<string>? onReasoning = null,
        Action<string>? onToolDone = null,
        bool appendUserMessage = true,
        Func<string, string, Task<bool>>? approveTool = null,
        string? pageContext = null)
    {
        Session.Reset();
        
        
        EnsureSystemPrompt(snapshot, tools);

        if (appendUserMessage) _messages.Add(AiMessage.User(userText));
        TrimHistory(cfg.ContextLimit);
        await SummarizeIfNeededAsync(cfg, ct);

        
        ApplyPageContext(pageContext);

        var total = AiUsage.Empty;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var stopReason = AiStopReason.None;

        
        
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(AnswerBudget);

        try
        {
            for (var round = 0; ; round++)
            {
                stopReason = AiTimeBudget.CheckBeforeRound(watch.Elapsed, round, AnswerBudget, AiTimeBudget.RoundGuard);
                if (stopReason != AiStopReason.None) break;

                var turn = await AiClient.StreamTurnAsync(cfg, _messages, tools, onDelta, onReasoning, deadline.Token);
                total = total.Plus(turn.Usage);

                if (!turn.WantsTools)
                {
                    if (turn.Content.Length > 0)
                    {
                        _messages.Add(new AiMessage("assistant", turn.Content) { Reasoning = turn.Reasoning });
                    }
                    return total;
                }

                
                
                _messages.Add(new AiMessage("assistant", turn.Content)
                {
                    ToolCalls = turn.ToolCalls,
                    Reasoning = turn.Reasoning,
                });

                foreach (var call in turn.ToolCalls)
                {
                    
                    deadline.Token.ThrowIfCancellationRequested();

                    
                    if (ToolRunner.RequiresApproval(call.Name) && approveTool is not null)
                    {
                        onStatus?.Invoke($"等待确认：{call.Name}");
                        AppLog.Step("hubai", $"请求授权：{call.Name}");

                        var approved = await approveTool(call.Name, call.Arguments);

                        if (!approved)
                        {
                            AppLog.Step("hubai", $"用户拒绝了 {call.Name}");
                            _messages.Add(AiMessage.Tool(call.Id,
                                "用户拒绝了这次操作。不要重试同一动作，如实说明，并给出不需要改动系统的替代方案。"));
                            onToolDone?.Invoke("已拒绝这次操作");
                            continue;
                        }
                    }

                    onStatus?.Invoke(DescribeTool(call.Name));
                    AppLog.Step("hubai", $"工具调用：{call.Name}（第 {round + 1} 轮，已用 {watch.Elapsed.TotalSeconds:0} 秒）");

                    
                    
                    var result = await ToolRunner.ExecuteAsync(call.Name, call.Arguments, Session, deadline.Token);
                    _messages.Add(AiMessage.Tool(call.Id, result));

                    
                    
                    onToolDone?.Invoke(DescribeToolDone(call.Name));
                }

                onNewBubble?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            
            
            
            
            stopReason = AiTimeBudget.ClassifyCancel(ct.IsCancellationRequested, watch.Elapsed, AnswerBudget, AiTimeBudget.Slack);

            
            
            
            var repaired = HistoryRepair.Close(_messages);
            if (repaired > 0)
                AppLog.Warn("hubai", $"中止后补齐 {repaired} 条未回应的工具结果（否则下次请求会被服务端拒绝）");

            
            if (stopReason == AiStopReason.None) throw;
        }

        if (stopReason == AiStopReason.None) return total;

        var used = watch.Elapsed;
        AppLog.Warn("hubai",
            $"本轮问答中止（{stopReason}）：已用 {used.TotalSeconds:0} 秒 / 预算 {AnswerBudget.TotalSeconds:0} 秒，" +
            $"上下文 {_messages.Count} 条，累计 {total.TotalTokens} token");

        
        var notice = AiTimeBudget.Describe(stopReason, AnswerBudget).TrimStart('\r', '\n');
        onDelta?.Invoke(AiTimeBudget.Describe(stopReason, AnswerBudget));
        _messages.Add(new AiMessage("assistant", notice));

        return total;
    }

    
    
    

    
    
    
    
    
    
    
    
    
    
    
    private async Task SummarizeIfNeededAsync(AiConfig cfg, CancellationToken ct)
    {
        if (_pendingDrop.Count == 0) return;
        if (_pendingDrop.Sum(TokenChars) < SummarizeTriggerChars) return;

        var material = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(_historySummary))
            material.AppendLine("（更早的对话已经压过一次，要点如下）").AppendLine(_historySummary).AppendLine();

        foreach (var message in _pendingDrop)
        {
            if (string.IsNullOrWhiteSpace(message.Content)) continue;
            material.AppendLine(message.Role == "user" ? $"用户：{message.Content}" : $"助手：{message.Content}");
        }

        if (material.Length == 0)
        {
            _pendingDrop.Clear();
            return;
        }

        var input = material.ToString();
        if (input.Length > SummarizeInputChars) input = input[..SummarizeInputChars];

        var count = _pendingDrop.Count;
        var chars = _pendingDrop.Sum(TokenChars);

        try
        {
            
            var cheap = cfg with { Thinking = false, Effort = "low" };

            var request = new List<AiMessage>
            {
                AiMessage.System(SummaryPrompt),
                AiMessage.User(input),
            };

            var sb = new StringBuilder();
            await AiClient.StreamTurnAsync(cheap, request, null, chunk => sb.Append(chunk), null, ct);

            var summary = sb.ToString().Trim();
            if (summary.Length == 0)
            {
                AppLog.Warn("hubai", "上下文摘要为空，本次退回截断");
                _pendingDrop.Clear();
                return;
            }

            _historySummary = summary;
            ApplySummary(summary);
            AppLog.Step("hubai", $"上下文摘要完成：压掉 {count} 条 / {chars} 字 → {summary.Length} 字");
            _pendingDrop.Clear();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Exception("hubai", ex, "上下文摘要");
            _pendingDrop.Clear();      
        }
    }

    
    
    
    
    private void ApplySummary(string summary)
    {
        if (_messages.Count == 0 || _messages[0].Role != "system") return;

        var basePrompt = _baseSystemPrompt ?? _messages[0].Content;
        _messages[0] = new AiMessage("system", $"{basePrompt}\n\n{SummaryHeader}\n{summary}");
    }

    
    
    

    
    
    
    
    
    
    
    private void ApplyPageContext(string? json)
    {
        if (_messages.Count == 0 || _messages[0].Role != "system") return;

        var content = StripPageContext(_messages[0].Content);
        if (!string.IsNullOrWhiteSpace(json))
            content = content + ContextOpen + json + ContextClose;

        if (!string.Equals(content, _messages[0].Content, StringComparison.Ordinal))
            _messages[0] = new AiMessage("system", content);
    }

    
    private static string StripPageContext(string content)
    {
        var start = content.IndexOf(ContextOpen, StringComparison.Ordinal);
        if (start < 0) return content;

        var close = content.IndexOf(ContextClose, StringComparison.Ordinal);
        return close < 0
            ? content[..start]
            : content[..start] + content[(close + ContextClose.Length)..];
    }

    
    
    

    private void EnsureSystemPrompt(MachineSnapshot snapshot, IReadOnlyList<ToolDefinition>? tools = null)
    {
        var templateId = App.Settings.Current.HubAiTemplate;

        if (_messages.Count > 0 && _messages[0].Role == "system")
        {
            
            if (string.Equals(_promptTemplateId, templateId, StringComparison.Ordinal)) return;
            _messages.RemoveAt(0);
            AppLog.Info("hubai", $"助手模板已切换为 {templateId}，重建系统提示");
        }

        var hasTools = tools is { Count: > 0 };

        
        var hasActions = tools?.Any(t => t.Id == "suggest_actions") == true;

        
        var prompt = BuildSystemPrompt(MachineDigest.ToPrompt(snapshot), App.Settings.Current.UserDisplayName,
                                       hasTools, hasActions, templateId);
        _baseSystemPrompt = prompt;
        _promptTemplateId = templateId;

        _messages.Insert(0, AiMessage.System(prompt));
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private static string BuildSystemPrompt(string digest, string? userName, bool hasTools, bool hasActions, string templateId) => $"""
        你是 ClassSoftwareHub 内置的 AI 助手，一个通用型的对话助手。

        能力范围：
        - 可以回答任何领域的问题，不限于电脑相关。
        - 这台电脑的一份快照已经放在下方「本机数据」里，可以直接据此回答与这台机器有关的问题。
        {(hasTools
            ? "- 需要更细的数据时（磁盘明细、已装软件清单、开机启动项等），可以调用工具查询。\n- 可以检索站内软件库，帮用户挑选软件。\n- 应用内有「文档库」教程文章：用户问到具体操作步骤时，可以先用 list_documents 找、再用 read_document 读正文，转述时注明文章标题。"
            : "- ⛔ 工具当前没有启用：不要声称要调用工具，不要输出任何 `<tool_call>`、XML 或 JSON 形式的调用语句，也不要描述你打算怎么调用。本机数据只有下面这一份，缺什么就直说没有。\n- ⛔ 无法联网检索、无法打开网页。")}
        {(hasActions
            ? "- 用户的问题涉及换主题、查看外观设置、清理 AppData、打开系统功能，并确实要动手时，调用 suggest_actions 把按钮放到回答末尾。只放与问题直接相关的 1~3 个。"
            : "")}

        必须遵守的三条：
        1. 你不能修改这台电脑：不删文件、不改设置、不装软件、不结束进程。需要用户动手的地方，告诉他去哪个页面。
        2. {(hasTools
            ? "涉及本机状态、而下方数据不足时，先调用工具取数据，不要凭印象猜测。"
            : "下方数据里没有的，如实说明「本次未读取到」，不要推测，不要编造。")}
        3. {(hasTools
            ? "推荐软件之前必须先调用 search_software_library，只能从检索结果里选，不要推荐站外软件。"
            : "推荐软件时只说出软件名称与用途，不要声称正在检索站内软件库。")}

        回答风格：
        - 简体中文书面语，不使用口语和感叹号。
        - 先给结论，再展开理由；用户问得深就给足细节，不要为了简短牺牲信息量。
        - 需要分点时用列表，不要把一段话堆在一起。
        - ⛔ 不要把「我要做什么」的过程写进回答里（例如"我可以尝试调用工具""让我先查一下"），直接给结果。

        {TemplateSection(templateId)}
        [使用者]
        {(string.IsNullOrWhiteSpace(userName) ? "称呼：未设置（用「你」即可）" : $"称呼：{userName}")}

        [本机数据]
        {digest}
        """;

    private static string TemplateSection(string templateId) => templateId switch
    {
        "course" => """
[助手模板：课件助手]
- 按课堂讲解组织回答：先给知识点，再给例题或板书提纲，最后留一句课堂小结。
- 语言面向学生，术语第一次出现时用一句话解释；不要堆无关背景。
- 数学、物理内容要写出规范表达式和已知条件。
""",
        "solver" => """
[助手模板：解题助手]
- 先给解题思路与步骤，再给最终答案；步骤之间标明依据。
- 数学题使用规范表达式，必要时给出定义域、单位和易错点。
- 题目条件不足时，先说明缺什么，不要强行假设。
""",
        "coder" => """
[助手模板：代码助手]
- 给出可运行的代码片段，说明依赖、入口和使用方式；注释保持简洁。
- 指出代码的边界条件、异常处理与可能风险；不要省略关键错误处理。
- 优先使用当前平台通用写法，避免引入用户环境里不存在的依赖。
""",
        _ => "",
    };

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private void TrimHistory(int contextLimitChars = 0)
    {
        var system = _messages.Count > 0 && _messages[0].Role == "system" ? _messages[0] : null;
        var body = system is null ? _messages.ToList() : _messages.Skip(1).ToList();

        
        var turns = new List<List<AiMessage>>();
        foreach (var message in body)
        {
            if (message.Role == "user" || turns.Count == 0) turns.Add(new List<AiMessage>());
            turns[^1].Add(message);
        }

        var drop = 0;
        while (turns.Count - drop > 1)
        {
            var count = (system is null ? 0 : 1) + turns.Skip(drop).Sum(t => t.Count);
            var chars = turns.Skip(drop).SelectMany(t => t).Sum(TokenChars);

            var overCount = count > MaxHistoryMessages;
            var overChars = contextLimitChars > 0 && chars > contextLimitChars;
            if (!overCount && !overChars) break;

            drop++;
        }

        if (drop > 0)
        {
            _messages.Clear();
            if (system is not null) _messages.Add(system);
            foreach (var turn in turns.Skip(drop)) _messages.AddRange(turn);

            
            
            foreach (var turn in turns.Take(drop)) _pendingDrop.AddRange(turn);

            AppLog.Step("hubai", $"历史裁剪：丢掉最早 {drop} 个回合，保留 {_messages.Count} 条");
        }

        EnsureToolPairing();
    }

    
    private static int TokenChars(AiMessage message) =>
        message.Content.Length + (message.Reasoning?.Length ?? 0);

    
    
    
    
    
    
    
    private void EnsureToolPairing()
    {
        if (_messages.All(m => m.Role != "tool")) return;

        var rebuilt = new List<AiMessage>(_messages.Count);
        HashSet<string>? awaiting = null;
        var awaitingOwner = -1;
        var dropped = 0;

        void CloseToolGroup()
        {
            if (awaiting is null) return;

            var pending = awaiting;
            var ownerIndex = awaitingOwner;
            awaiting = null;
            awaitingOwner = -1;

            if (pending.Count == 0 || ownerIndex < 0) return;

            
            var owner = rebuilt[ownerIndex];
            if (string.IsNullOrWhiteSpace(owner.Content))
            {
                rebuilt.RemoveAt(ownerIndex);
            }
            else
            {
                rebuilt[ownerIndex] = new AiMessage(owner.Role, owner.Content) { Reasoning = owner.Reasoning };
            }

            dropped++;
        }

        foreach (var message in _messages)
        {
            if (message.Role == "tool")
            {
                if (awaiting is not null && message.ToolCallId is { } id && awaiting.Remove(id))
                    rebuilt.Add(message);
                else
                    dropped++;

                continue;
            }

            CloseToolGroup();

            if (message.Role == "assistant" && message.ToolCalls is { Count: > 0 } calls)
            {
                awaiting = new HashSet<string>(calls.Select(c => c.Id), StringComparer.Ordinal);
                awaitingOwner = rebuilt.Count;
            }

            rebuilt.Add(message);
        }

        CloseToolGroup();

        if (dropped == 0) return;

        _messages.Clear();
        _messages.AddRange(rebuilt);
        AppLog.Step("hubai", $"历史修复：清掉 {dropped} 条无法配对的工具调用/结果");
    }

    private static string DescribeTool(string id) => id switch
    {
        "get_machine_snapshot" => "正在读取本机概况",
        "list_disk_folders" => "正在统计目录占用，可能需要一会儿",
        "search_software_library" => "正在检索软件库",
        "list_installed_apps" => "正在核对已安装的软件",
        "list_startup_items" => "正在读取开机启动项",
        "list_documents" => "正在浏览文档库",
        "read_document" => "正在阅读文档正文",
        _ => "正在处理",
    };

    
    private static string DescribeToolDone(string id) => id switch
    {
        "get_machine_snapshot" => "已读取本机概况",
        "list_disk_folders" => "已统计目录占用",
        "search_software_library" => "已检索软件库",
        "list_installed_apps" => "已核对已安装软件",
        "list_startup_items" => "已读取开机启动项",
        "list_documents" => "已浏览文档库",
        "read_document" => "已阅读文档正文",
        _ => "已完成一次工具调用",
    };
}
