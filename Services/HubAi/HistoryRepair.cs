using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassSoftwareHub.Desktop.Services.HubAi;

/// <summary>
/// 收拾被中途打断的历史（2026-10-30）。
///
/// <para>
/// OpenAI 兼容协议有一条硬规矩：assistant 消息里的每个 <c>tool_calls[].id</c>，
/// 后面**必须**有一条 <c>role=tool</c> 消息回应它。少一条，下一次请求直接被 400 打回
/// （<c>Messages with role 'tool' must be a response to a preceding message with 'tool_calls'</c>）。
/// </para>
///
/// <para>
/// 而"打断"正好会破坏这条规矩：模型一轮要调两个工具，第一个跑完、第二个还没跑时
/// 用户点了停止（或 8 分钟预算到点），上下文里就只剩半截配对。
/// 下次在同一会话里追问，整个历史会被一起发出去 —— 于是那次请求必定失败。
/// 之前把 <c>ToolRunner</c> 换成取消令牌时没意识到这一点，这里补上。
/// </para>
///
/// <para>
/// ⛔ 只补**缺的那几条**，不动已有内容：工具结果是要给模型看的证据，不能凭空编。
/// 补的内容明确写"未执行"，让模型知道这一步没做成。
/// </para>
/// </summary>
public static class HistoryRepair
{
    /// <summary>中止时补进上下文的那句话。</summary>
    public const string InterruptedNote = "（本轮被中止，该工具未执行或未跑完，没有结果。）";

    /// <summary>
    /// 找出"最近一条 assistant 要调、但后面没有 tool 应答"的那些调用 id。
    /// 没有悬空就返回空列表。
    /// </summary>
    public static List<string> DanglingToolCallIds(IReadOnlyList<AiMessage> messages)
    {
        var answered = new HashSet<string>(StringComparer.Ordinal);

        // 从末尾往前：工具应答是一串连着的 tool 消息，跳过它们，再往前那条才是发起方
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];

            if (message.Role == "tool")
            {
                if (!string.IsNullOrEmpty(message.ToolCallId)) answered.Add(message.ToolCallId);
                continue;
            }

            // 不是 assistant（user / system）= 上一轮干净地结束了，没有悬空
            if (message.Role != "assistant" || message.ToolCalls is not { Count: > 0 })
                return new List<string>();

            return message.ToolCalls
                .Select(c => c.Id)
                .Where(id => !answered.Contains(id))
                .ToList();
        }

        return new List<string>();
    }

    /// <summary>
    /// 把悬空的工具应答补齐（补在末尾，紧跟在那串 tool 消息后面）。返回补了几条。
    /// <para>
    /// ⚠️ 幂等：补过之后再调不会重复补 —— 也正因为如此，收尾路径可以随便调，不用判状态。
    /// </para>
    /// </summary>
    public static int Close(List<AiMessage> messages, string note = InterruptedNote)
    {
        var missing = DanglingToolCallIds(messages);
        if (missing.Count == 0) return 0;

        foreach (var id in missing) messages.Add(AiMessage.Tool(id, note));
        return missing.Count;
    }
}
