using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services.Diagnostics;

namespace ClassSoftwareHub.Desktop.Services.HubAi;

/// <summary>
/// 一轮对话的编排（2026-10-18）：组装 system prompt、跑 Agent 循环、把工具结果回灌。
///
/// <para>
/// 循环就是「Agent」和「聊天」的全部差别：模型说要调工具 → 宿主执行 → 结果塞回 messages → 再问一次。
/// 这里最多跑 <see cref="MaxRounds"/> 轮，防止模型陷入"一直调工具不回答"的死循环。
/// </para>
/// <para>
/// ⛔ 本类不碰任何 UI，也不碰网络细节。UI 只通过三个回调观察：增量文本、状态提示、换气泡。
/// </para>
/// </summary>
public sealed class AiChatService
{
    /// <summary>一轮提问里最多几次工具往返。5 次足够覆盖"先查概况 → 再查磁盘 → 再检索软件"这种链路。</summary>
    private const int MaxRounds = 5;

    /// <summary>历史消息上限（含 system）。超了就丢最早的对话，保住 system prompt。</summary>
    private const int MaxHistoryMessages = 20;

    private readonly List<AiMessage> _messages = new();

    /// <summary>
    /// 被裁掉、但**还没压成摘要**的那些轮次（2026-10-23 加）。
    /// 攒够一定量才真正调模型压缩 —— 每轮都压一次不划算，而摘要本身也要花钱。
    /// </summary>
    private readonly List<AiMessage> _pendingDrop = new();

    /// <summary>已经压出来的历史要点（会拼进 system 提示）。</summary>
    private string? _historySummary;

    /// <summary>不含摘要的原始 system 提示 —— 每次重拼摘要都从它起算，避免摘要层层叠加。</summary>
    private string? _baseSystemPrompt;

    /// <summary>攒到多少字符才开始压缩。太小会"每轮都调一次模型"，太大等于没压。</summary>
    private const int SummarizeTriggerChars = 3000;

    /// <summary>送去压缩的原文上限（再长就截断，压不动也没有意义）。</summary>
    private const int SummarizeInputChars = 8000;

    private const string SummaryHeader = "【早前对话摘要】";

    private const string SummaryPrompt =
        """
        你是对话压缩器。把下面这段更早的对话压成不超过 200 字的要点，供后续继续对话使用。

        必须保留：用户的真实目标、已经确认的结论、用户给出的硬性约束、还没做完的事。
        不要保留：寒暄、重复的追问、工具返回的原始清单、已经作废的中间推理。
        只输出要点本身，不要开场白、不要评论、不要分点标题。
        """;

    /// <summary>本轮的工具上下文：命中过的软件、调用轨迹。UI 从这儿取推荐卡。</summary>
    public ToolSession Session { get; } = new();

    public void Reset()
    {
        _messages.Clear();
        _pendingDrop.Clear();
        _historySummary = null;
        _baseSystemPrompt = null;
        Session.Reset();
    }

    /// <summary>当前消息快照，用于存档。</summary>
    public IReadOnlyList<AiMessage> Snapshot() => _messages.ToList();

    /// <summary>
    /// 恢复一段历史继续聊。
    /// ⚠️ **跳过 system**：体检单是"当时的机器状态"，隔天再看已经不准；
    ///    留着旧 system 会让模型对着过期数据说话，不如让下次提问时按当前状态重新生成。
    /// </summary>
    public void Restore(IEnumerable<AiMessage> messages)
    {
        _messages.Clear();
        // 换了一支/换了一段，之前那份摘要就不再代表当前上下文
        _pendingDrop.Clear();
        _historySummary = null;
        _baseSystemPrompt = null;
        Session.Reset();

        foreach (var message in messages)
        {
            if (message.Role == "system") continue;

            // ⛔⛔ assistant 的 content 不能为空。存档里允许存「只有思考、没有正文」的那一轮（工具调用中间态），
            //    但把它原样塞回上下文，下一轮请求会被服务端判成 400：
            //    `Invalid assistant message: content or tool_calls must be set`（2026-10-21 实测）。
            //    这种消息对后续对话也没有价值，直接丢掉。
            if (message.Role == "assistant" && string.IsNullOrWhiteSpace(message.Content)) continue;

            // ⛔⛔ 2026-10-23：**`tool` 角色的消息必须在恢复时丢掉**。
            //    协议要求：`role=tool` 只能作为「上一条 assistant 的 tool_calls」的应答出现。
            //    而上面那行恰好把「带 tool_calls、但正文为空」的那一轮 assistant 丢了 ——
            //    它后面的 tool 消息就失去了配对，服务端直接 400：
            //    `Messages with role 'tool' must be a response to a preceding message with 'tool_calls'`
            //    （Nick 截图里那条报错就是这个）。
            //    tool 结果只是中间态，对后续对话没有价值，一并丢掉最稳妥。
            if (message.Role == "tool") continue;

            _messages.Add(message);
        }
    }

    /// <summary>
    /// 跑一轮完整问答。<paramref name="onDelta"/> 会在**每一轮**模型输出时被调用，
    /// 所以第一次工具调用之后要换气泡 —— 否则"我先看看磁盘"和最终回答会糊成一段。
    /// </summary>
    /// <returns>本轮所有请求的**累计用量**（含工具往返产生的每一轮）。</returns>
    /// <param name="appendUserMessage">
    /// 是否把 <paramref name="userText"/> 作为一条新的 user 消息追加进上下文。
    /// <para>
    /// ⚠️ 2026-10-23 加：「重新生成」时提问已经在上下文里了，只需要模型再答一次 ——
    /// 这时传 false，否则同一句话会在上下文里出现两遍（模型会以为自己被追问）。
    /// </para>
    /// </param>
    /// <param name="approveTool">
    /// 需要人工确认的工具（会改动系统的那些）执行前的闸门：返回 false 表示用户拒绝。
    /// 当前工具全部只读，所以这个回调暂时不会被调用 —— 但通道留着，加写类工具时不用再改这里。
    /// </param>
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
        Func<string, string, Task<bool>>? approveTool = null)
    {
        Session.Reset();
        // ⚠️ 必须把 tools 传下去：没有工具时提示词里**绝不能**提"可以调用工具" ——
        //    否则模型会自己编 `<tool_call>{...}</tool_call>` 的文本吐给用户（2026-10-19 实测截图）。
        EnsureSystemPrompt(snapshot, tools);

        if (appendUserMessage) _messages.Add(AiMessage.User(userText));
        TrimHistory(cfg.ContextLimit);
        await SummarizeIfNeededAsync(cfg, ct);

        var total = AiUsage.Empty;

        for (var round = 0; round < MaxRounds; round++)
        {
            var turn = await AiClient.StreamTurnAsync(cfg, _messages, tools, onDelta, onReasoning, ct);
            total = total.Plus(turn.Usage);

            if (!turn.WantsTools)
            {
                if (turn.Content.Length > 0)
                {
                    _messages.Add(new AiMessage("assistant", turn.Content) { Reasoning = turn.Reasoning });
                }
                return total;
            }

            // 把模型的"我要调工具"记进历史，否则下一轮的上下文对不上。
            // ⚠️ Reasoning 必须一起带上：DeepSeek / Kimi / GLM 在带 tools 时会校验它。
            _messages.Add(new AiMessage("assistant", turn.Content)
            {
                ToolCalls = turn.ToolCalls,
                Reasoning = turn.Reasoning,
            });

            foreach (var call in turn.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();

                // ⛔ 会改动系统的工具必须先问人（2026-10-23 加）。当前全部只读，不会走到这儿。
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
                AppLog.Step("hubai", $"工具调用：{call.Name}");

                var result = await ToolRunner.ExecuteAsync(call.Name, call.Arguments, Session, ct);
                _messages.Add(AiMessage.Tool(call.Id, result));

                // 界面上留一条"已读取 xxx"的痕迹 —— 否则工具调用前后两段回答之间完全是空的，
                // 看起来像对话断掉了（Nick 2026-10-18 指出）。
                onToolDone?.Invoke(DescribeToolDone(call.Name));
            }

            onNewBubble?.Invoke();
        }

        // 跑满上限还没给答案：如实说明，别假装成功
        onDelta?.Invoke("（本轮工具调用次数已达上限，如果还有未完成的部分，请再追问一次。）");
        return total;
    }

    // ══════════════════════════════════════════════════════
    //  上下文压缩（2026-10-23）
    // ══════════════════════════════════════════════════════

    /// <summary>
    /// 把这段时间裁掉的历史压成一段要点，拼进 system 提示。
    ///
    /// <para>
    /// ⛔ 只在**攒够量**时才真调模型：本方法每轮请求前都会跑，若每次都压，
    ///    每轮就多一次往返（时间和钱都翻倍），摘要也会退化成流水账。
    /// </para>
    /// <para>
    /// ⚠️ 失败不影响对话：压不出来就算了，这些轮次本来就已经被裁掉（等于回到旧的纯截断行为）。
    /// </para>
    /// </summary>
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
            // 摘要不需要深度思考：关掉能省一大截 token，也避免"只想不说"根本压不出东西
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
            _pendingDrop.Clear();      // 别每轮重试同一批，直接退回纯截断
        }
    }

    /// <summary>
    /// 把摘要拼到系统提示后面。
    /// ⚠️ 每次都从**原始提示**重拼，否则摘要会一层套一层，上下文反而越压越大。
    /// </summary>
    private void ApplySummary(string summary)
    {
        if (_messages.Count == 0 || _messages[0].Role != "system") return;

        var basePrompt = _baseSystemPrompt ?? _messages[0].Content;
        _messages[0] = new AiMessage("system", $"{basePrompt}\n\n{SummaryHeader}\n{summary}");
    }

    // ══════════════════════════════════════════════════════
    //  提示词
    // ══════════════════════════════════════════════════════

    private void EnsureSystemPrompt(MachineSnapshot snapshot, IReadOnlyList<ToolDefinition>? tools = null)
    {
        if (_messages.Count > 0 && _messages[0].Role == "system") return;

        var hasTools = tools is { Count: > 0 };

        // 摘要要拼在**原始提示**之后，所以先把原始提示记下来（见 ApplySummary）
        var prompt = BuildSystemPrompt(MachineDigest.ToPrompt(snapshot), App.Settings.Current.UserDisplayName, hasTools);
        _baseSystemPrompt = prompt;

        _messages.Insert(0, AiMessage.System(prompt));
    }

    /// <summary>
    /// 系统提示。
    ///
    /// <para>
    /// ⛔⛔ 改之前先看这段历史（2026-10-18）：最初这版把模型写死成"电脑顾问"——
    /// 只依据本机数据、只谈瓶颈、300 字以内、结论先行。结果是用户问"什么是猴子"直接被拒，
    /// 回答也变得干瘪、不敢展开（Nick：「一点都不细节，不像 DeepSeek 回答的」）。
    /// <b>API 是用户自己付钱，宿主没有任何理由限制它能聊什么。</b>
    /// </para>
    /// <para>
    /// 现在的边界只有三条，而且都是**产品上必须的**：
    /// <list type="number">
    ///   <item>不能修改电脑（只读）—— 这是安全承诺，写进了界面的红条；</item>
    ///   <item>涉及本机状态必须先调工具，不许编造机器数据 —— 编出来的"你有 8GB 内存"会误导人；</item>
    ///   <item>推荐软件只能从站内软件库检索结果里选 —— 否则会给出一堆本站下载不到的软件。</item>
    /// </list>
    /// 除此之外：**什么问题都可以答，想答多细就答多细**。
    /// </para>
    /// </summary>
    private static string BuildSystemPrompt(string digest, string? userName, bool hasTools) => $"""
        你是 ClassSoftwareHub 内置的 AI 助手，一个通用型的对话助手。

        能力范围：
        - 可以回答任何领域的问题，不限于电脑相关。
        - 这台电脑的一份快照已经放在下方「本机数据」里，可以直接据此回答与这台机器有关的问题。
        {(hasTools
            ? "- 需要更细的数据时（磁盘明细、已装软件清单、开机启动项等），可以调用工具查询。\n- 可以检索站内软件库，帮用户挑选软件。"
            : "- ⛔ 工具当前没有启用：不要声称要调用工具，不要输出任何 `<tool_call>`、XML 或 JSON 形式的调用语句，也不要描述你打算怎么调用。本机数据只有下面这一份，缺什么就直说没有。\n- ⛔ 无法联网检索、无法打开网页。")}

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

        [使用者]
        {(string.IsNullOrWhiteSpace(userName) ? "称呼：未设置（用「你」即可）" : $"称呼：{userName}")}

        [本机数据]
        {digest}
        """;

    /// <summary>
    /// 裁剪历史。
    /// <para>
    /// ⛔⛔ 2026-10-23 改成**按回合裁剪**。原来是从前面**逐条** <c>RemoveAt</c>，落点可以正好切在
    /// 「assistant(tool_calls) → tool(result)」这一组中间：assistant 被删掉、tool 留下，
    /// 下一次请求服务端直接 400 ——`Messages with role 'tool' must be a response to a preceding
    /// message with 'tool_calls'`。这类错误只在"攒够消息 + 恰好调过工具"时才复现，
    /// 手测几乎撞不上，所以裁剪单位必须是**整个回合**，不能是单条消息。
    /// </para>
    /// <para>
    /// 回合 = 一条 user 起到下一条 user 之前（含中间所有 assistant / tool）。
    /// 永远保住 system 与**最后一个回合**（用户这次问的话），否则等于答非所问。
    /// </para>
    /// </summary>
    /// <param name="contextLimitChars">用户给这条模型设的近似字符预算，0 = 不限。</param>
    private void TrimHistory(int contextLimitChars = 0)
    {
        var system = _messages.Count > 0 && _messages[0].Role == "system" ? _messages[0] : null;
        var body = system is null ? _messages.ToList() : _messages.Skip(1).ToList();

        // 开头若有不以 user 起始的残段（历史被更早的版本裁过 / 手工改过），单独算作第 0 个回合
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

            // 丢掉的这部分不直接销毁：攒起来，够量了压成一段要点拼进 system。
            // ⚠️ 别在这里同步调模型 —— 本方法在每轮请求前都会跑，压一次就多一次往返。
            foreach (var turn in turns.Take(drop)) _pendingDrop.AddRange(turn);

            AppLog.Step("hubai", $"历史裁剪：丢掉最早 {drop} 个回合，保留 {_messages.Count} 条");
        }

        EnsureToolPairing();
    }

    /// <summary>一条消息占的近似字符数（思考过程也要算，它同样占上下文）。</summary>
    private static int TokenChars(AiMessage message) =>
        message.Content.Length + (message.Reasoning?.Length ?? 0);

    /// <summary>
    /// 兜底：保证 <c>tool</c> 消息永远有配对的 <c>assistant.tool_calls</c>，反之亦然。
    /// <para>
    /// 按回合裁剪本身不会产生孤儿；这里防的是**历史里本来就有的破损结构**
    /// （更早的版本逐条裁过、或存档来自旧格式）。OpenAI 兼容接口对这类结构一律直接 400，不容错。
    /// </para>
    /// </summary>
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

            // 这组 tool_calls 没被答全 —— 留着它服务端照样报错，整条摘掉
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
        _ => "正在处理",
    };

    /// <summary>工具跑完后留在对话流里的痕迹文案（过去式，和 <see cref="DescribeTool"/> 的进行式区分）。</summary>
    private static string DescribeToolDone(string id) => id switch
    {
        "get_machine_snapshot" => "已读取本机概况",
        "list_disk_folders" => "已统计目录占用",
        "search_software_library" => "已检索软件库",
        "list_installed_apps" => "已核对已安装软件",
        "list_startup_items" => "已读取开机启动项",
        _ => "已完成一次工具调用",
    };
}
