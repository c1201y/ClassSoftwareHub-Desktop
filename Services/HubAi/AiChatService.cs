using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>本轮的工具上下文：命中过的软件、调用轨迹。UI 从这儿取推荐卡。</summary>
    public ToolSession Session { get; } = new();

    public void Reset()
    {
        _messages.Clear();
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
        Session.Reset();

        foreach (var message in messages)
        {
            if (message.Role == "system") continue;

            // ⛔⛔ assistant 的 content 不能为空。存档里允许存「只有思考、没有正文」的那一轮（工具调用中间态），
            //    但把它原样塞回上下文，下一轮请求会被服务端判成 400：
            //    `Invalid assistant message: content or tool_calls must be set`（2026-10-21 实测）。
            //    这种消息对后续对话也没有价值，直接丢掉。
            if (message.Role == "assistant" && string.IsNullOrWhiteSpace(message.Content)) continue;

            _messages.Add(message);
        }
    }

    /// <summary>
    /// 跑一轮完整问答。<paramref name="onDelta"/> 会在**每一轮**模型输出时被调用，
    /// 所以第一次工具调用之后要换气泡 —— 否则"我先看看磁盘"和最终回答会糊成一段。
    /// </summary>
    /// <returns>本轮所有请求的**累计用量**（含工具往返产生的每一轮）。</returns>
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
        Action<string>? onToolDone = null)
    {
        Session.Reset();
        // ⚠️ 必须把 tools 传下去：没有工具时提示词里**绝不能**提"可以调用工具" ——
        //    否则模型会自己编 `<tool_call>{...}</tool_call>` 的文本吐给用户（2026-10-19 实测截图）。
        EnsureSystemPrompt(snapshot, tools);

        _messages.Add(AiMessage.User(userText));
        TrimHistory(cfg.ContextLimit);

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
    //  提示词
    // ══════════════════════════════════════════════════════

    private void EnsureSystemPrompt(MachineSnapshot snapshot, IReadOnlyList<ToolDefinition>? tools = null)
    {
        if (_messages.Count > 0 && _messages[0].Role == "system") return;

        var hasTools = tools is { Count: > 0 };

        _messages.Insert(0, AiMessage.System(
            BuildSystemPrompt(MachineDigest.ToPrompt(snapshot), App.Settings.Current.UserDisplayName, hasTools)));
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
    /// 两条约束叠加：条数上限 <see cref="MaxHistoryMessages"/>（防上下文无限长），
    /// 以及用户给这条模型设的 <paramref name="contextLimitChars"/>（近似字符数，0 = 不限制）。
    /// ⚠️ 永远保住 system（第 0 条）与最后一条用户消息，否则模型会答非所问。
    /// </para>
    /// </summary>
    private void TrimHistory(int contextLimitChars = 0)
    {
        // 条数上限
        while (_messages.Count > MaxHistoryMessages && _messages.Count > 2)
        {
            var start = _messages[0].Role == "system" ? 1 : 0;
            if (start >= _messages.Count - 1) break;

            _messages.RemoveAt(start);
            if (start < _messages.Count) _messages.RemoveAt(start);
        }

        if (contextLimitChars <= 0) return;

        // 字符预算：从最新往回累加，超了就停，保留连续的一段
        var total = _messages.Sum(m => m.Content.Length + (m.Reasoning?.Length ?? 0));
        var startIndex = _messages[0].Role == "system" ? 1 : 0;

        while (total > contextLimitChars && startIndex < _messages.Count - 1)
        {
            total -= _messages[startIndex].Content.Length + (_messages[startIndex].Reasoning?.Length ?? 0);
            _messages.RemoveAt(startIndex);

            // 一次删两条太粗暴，这里逐条删；system 不在范围内所以不会误删
            if (total <= contextLimitChars) break;
        }
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
