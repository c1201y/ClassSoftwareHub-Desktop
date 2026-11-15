using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassSoftwareHub.Desktop.Services.HubAi;






















public static class HistoryRepair
{
    
    public const string InterruptedNote = "（本轮被中止，该工具未执行或未跑完，没有结果。）";

    
    
    
    
    public static List<string> DanglingToolCallIds(IReadOnlyList<AiMessage> messages)
    {
        var answered = new HashSet<string>(StringComparer.Ordinal);

        
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];

            if (message.Role == "tool")
            {
                if (!string.IsNullOrEmpty(message.ToolCallId)) answered.Add(message.ToolCallId);
                continue;
            }

            
            if (message.Role != "assistant" || message.ToolCalls is not { Count: > 0 })
                return new List<string>();

            return message.ToolCalls
                .Select(c => c.Id)
                .Where(id => !answered.Contains(id))
                .ToList();
        }

        return new List<string>();
    }

    
    
    
    
    
    
    public static int Close(List<AiMessage> messages, string note = InterruptedNote)
    {
        var missing = DanglingToolCallIds(messages);
        if (missing.Count == 0) return 0;

        foreach (var id in missing) messages.Add(AiMessage.Tool(id, note));
        return missing.Count;
    }
}
