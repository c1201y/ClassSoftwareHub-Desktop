using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.HubAi;


public enum AiActionKind
{
    
    Theme,

    
    Navigate,
}


public sealed record AiAction(string Id, string Label, string Glyph, AiActionKind Kind, string Target);
















public static class AiActionCatalog
{
    public static readonly IReadOnlyList<AiAction> All = new List<AiAction>
    {
        
        new("theme-dark", "切换为深色主题", "\uE708", AiActionKind.Theme, "dark"),
        new("theme-light", "切换为浅色主题", "\uE706", AiActionKind.Theme, "light"),
        new("theme-system", "跟随系统主题", "\uE895", AiActionKind.Theme, "system"),

        
        new("open-appearance", "打开外观设置", "\uE790", AiActionKind.Navigate, AppUri.ForSettings("appearance")),
        new("open-appdata", "打开 AppData 管理", "\uE8B7", AiActionKind.Navigate, AppUri.ForTool("appdata")),
        new("open-sysfeatures", "打开系统功能", "\uE713", AiActionKind.Navigate, AppUri.ForPage("sysfeatures")),

        
        
        
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
