using System.Collections.Generic;
using ClassSoftwareHub.Desktop.Data;

namespace ClassSoftwareHub.Desktop.Services.HubAi;

/// <summary>
/// 「这次对话能用哪些工具」的唯一判定处（2026-10-30 从 <c>HubAiPage.LoadTools</c> 抽出来）。
///
/// <para>
/// 来由：Hub AI 的入口从一个变成四个（完整页 + 软件下载 / AppData / 卸载页的浮层）。
/// 工具清单要是各写一份，「联网」或「工具总闸」改了开关，就会出现"页面里有工具、浮层里没有"
/// 这种查不出来的怪事。
/// </para>
///
/// <para>
/// 联网工具与「工具总闸」**互相独立**：总闸关着，只要用户把「联网」拨上就能用
/// （Nick 要求跟「思考」一样，拨上即可用）。
/// </para>
/// </summary>
public static class ActiveTools
{
    /// <summary>按当前设置返回可用工具（顺序即提示词里的顺序）。</summary>
    public static IReadOnlyList<ToolDefinition> Load()
    {
        var tools = new List<ToolDefinition>();

        if (App.Settings.Current.HubAiToolsEnabled)
        {
            var pack = ToolPackStore.Load();
            if (pack?.Tools is { Count: > 0 } packTools) tools.AddRange(packTools);
        }

        if (App.Settings.Current.HubAiWebEnabled) tools.AddRange(BuiltinTools.Web);

        return tools;
    }
}
