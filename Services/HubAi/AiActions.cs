using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.HubAi;

/// <summary>回答末尾那个按钮点下去属于哪一类。</summary>
public enum AiActionKind
{
    /// <summary>当场就能做完、没有任何副作用（目前只有换主题）。</summary>
    Theme,

    /// <summary>只负责把用户送到对应页面，具体改动由用户自己在那里确认。</summary>
    Navigate,
}

/// <summary>一个可以直接点的下一步。<see cref="Target"/> 对主题是取值，对跳转是 <c>csh://</c> 链接。</summary>
public sealed record AiAction(string Id, string Label, string Glyph, AiActionKind Kind, string Target);

/// <summary>
/// 助手能建议的动作清单（需求 27，2026-11-01）。
///
/// <para>
/// 来由：Hub AI 原来只能在回答里推荐软件、点进详情页。Nick 提的是「把设置项直接移到对话结尾，
/// 用户可以快速开启或关闭」，也就是回答下方像软件推荐那样给按钮。
/// </para>
///
/// <para>
/// ⛔⛔ 按钮的**文字与去处都在这里写死**，模型只能挑 id，不能自己编标签或链接 ——
/// 模型给一段「点这里删除所有缓存」，而按钮实际做的是别的事，用户没法分辨。
/// 同理，凡是**会改系统**的动作（AppData 删除、系统功能开关、系统优化）一律只给跳转，
/// 让用户在那个页面上自己看清再动手（那是产品里已经做过确认流程的地方）。
/// </para>
/// </summary>
public static class AiActionCatalog
{
    public static readonly IReadOnlyList<AiAction> All = new List<AiAction>
    {
        // ── 三档主题：点了立即生效，没有确认步骤。改的只是本应用的显示偏好。 ──
        new("theme-dark", "切换为深色主题", "\uE708", AiActionKind.Theme, "dark"),
        new("theme-light", "切换为浅色主题", "\uE706", AiActionKind.Theme, "light"),
        new("theme-system", "跟随系统主题", "\uE895", AiActionKind.Theme, "system"),

        // ── 跳转：只负责把用户送到地方 ──
        new("open-appearance", "打开外观设置", "\uE790", AiActionKind.Navigate, AppUri.ForSettings("appearance")),
        new("open-appdata", "打开 AppData 管理", "\uE8B7", AiActionKind.Navigate, AppUri.ForTool("appdata")),
        new("open-sysfeatures", "打开系统功能", "\uE713", AiActionKind.Navigate, AppUri.ForPage("sysfeatures")),

        // ── 工具页（2026-11-07 补）：用户问「怎么开某个功能」时得有地方可送。
        //    原来只有 6 个动作，问到 PPT 触控、查单词、文档库这类页面，模型只能空口说路径，
        //    甚至声称「下面有按钮」却拿不出按钮（Nick 反馈的就是这个）。 ──
        new("open-ppttouch", "打开 PowerPoint 触控优化", "\uE786", AiActionKind.Navigate, AppUri.ForPage("ppttouch")),
        new("open-dictionary", "打开查阅单词", "\uE8D2", AiActionKind.Navigate, AppUri.ForPage("dictionary")),
        new("open-doclib", "打开文档库", "\uE8A5", AiActionKind.Navigate, AppUri.ForPage("docs")),
        new("open-machinecheck", "打开机器体检", "\uE9D9", AiActionKind.Navigate, AppUri.ForPage("machinecheck")),
        new("open-uninstaller", "打开应用卸载", "\uE74D", AiActionKind.Navigate, AppUri.ForPage("uninstaller")),
        new("open-startup", "打开开机启动项", "\uE7E8", AiActionKind.Navigate, AppUri.ForPage("startup")),
        new("open-hubai-settings", "打开模型配置", "\uE713", AiActionKind.Navigate, AppUri.ForPage("hubai-settings")),
    };

    public static AiAction? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(a => string.Equals(a.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 交给模型的工具声明。说明文字由清单生成 —— 加一个动作不必再改两处。
    /// <para>
    /// ⚠️ <c>Parameters</c> 是 <see cref="JsonElement"/>，必须 <c>Clone()</c>（同 <c>BuiltinTools</c>）。
    /// </para>
    /// </summary>
    public static ToolDefinition ToolDefinition { get; } = new()
    {
        Id = "suggest_actions",
        Name = "给出可点击的下一步",
        Description = BuildToolDescription(),
        Parameters = BuildSchema(),
        ReadOnly = true,
    };

    private static string BuildToolDescription()
    {
        var lines = All.Select(a =>
            $"- {a.Id}：{a.Label}（{(a.Kind == AiActionKind.Theme
                ? "点了立即生效"
                : "只跳到对应页面，是否改动由用户自己在那里确认")}）");

        return "在回答末尾放上可以直接点的按钮，让用户不必自己找入口。\n"
             + "用户的问题涉及下面这些操作、并且确实要动手时调用；只是随口问问、没有要动手就不要调用。\n"
             + string.Join("\n", lines) + "\n"
             + "只放与当前问题直接相关的 1~3 个，最多 4 个。按钮点了才生效，本身不改任何东西。\n"
             + "⛔ 只能从上面这些 id 里挑，不要自己编 id，也不要在正文里重复教用户怎么点。\n"
             + "⛔⛔ 只要正文里出现「点下面的按钮」「下面的入口」「点这里」这类说法，就必须在同一次回答里调用本工具；"
             + "清单里没有合适的 id 时，不要声称有按钮，改为直接写清入口在哪一页，让用户自己去找"
             + "（2026-11-07：模型写了「可以点下面的按钮直接切换」，底下却一个按钮都没有）。";
    }

    private static JsonElement BuildSchema()
    {
        var ids = string.Join(", ", All.Select(a => "\"" + a.Id + "\""));
        return JsonDocument.Parse($$"""
            {
              "type": "object",
              "properties": {
                "ids": {
                  "type": "array",
                  "items": { "type": "string", "enum": [{{ids}}] },
                  "minItems": 1,
                  "maxItems": 4,
                  "description": "要放到回答末尾的动作 id，按重要性排序。"
                }
              },
              "required": ["ids"]
            }
            """).RootElement.Clone();
    }
}

/// <summary>
/// 执行用户点的那个动作。⛔ 只在 UI 线程调用（两个调用方都在界面上）。
///
/// <para>
/// 换主题用 <see cref="MainWindow.SetTheme"/>，跳转用 <see cref="MainWindow.HandleAppUriAsync"/> ——
/// 后者本来就在处理 <c>csh://</c>，连带把主窗口从托盘叫出来，不必另外写一套导航。
/// </para>
/// </summary>
public static class AiActionRunner
{
    public static void Run(AiAction action)
    {
        try
        {
            if (action.Kind == AiActionKind.Theme)
            {
                App.MainWindow?.SetTheme(action.Target);
                AppLog.Step("hubai", $"按回答里的按钮切换主题 → {action.Target}");
                return;
            }

            AppLog.Step("hubai", $"按回答里的按钮跳转 → {action.Target}");
            _ = App.MainWindow?.HandleAppUriAsync(action.Target);
        }
        catch (Exception ex)
        {
            AppLog.Exception("hubai", ex, "执行回答末尾的动作");
        }
    }
}
