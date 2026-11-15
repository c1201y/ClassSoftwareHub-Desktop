using System;
using System.Collections.Generic;
using ClassSoftwareHub.Desktop.Services.HubAi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Controls;

/// <summary>
/// 回答末尾的「可以直接点」按钮（需求 27）。
///
/// <para>
/// 两处都要用：完整页（<c>HubAiPage.RenderActions</c>）和浮层（<c>HubAiFlyout.Finish</c>）。
/// 放一份是因为按钮的字号、内边距、图标大小要一致 —— 各写一份迟早会长得不一样。
/// </para>
/// <para>
/// ⛔ 按钮文字来自 <see cref="AiActionCatalog"/>，不是模型给的文本。
/// </para>
/// </summary>
public static class AiActionBar
{
    /// <summary>没有动作时返回 <c>null</c>（调用方据此不插入任何东西）。</summary>
    public static StackPanel? Build(IEnumerable<AiAction> actions, Action<AiAction> onClick)
    {
        var panel = new StackPanel { Spacing = 6 };

        foreach (var action in actions)
        {
            if (panel.Children.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "可以继续",
                    FontSize = 12,
                    Opacity = 0.6,
                    Margin = new Thickness(2, 0, 0, 0),
                });
            }

            panel.Children.Add(MakeButton(action, onClick));
        }

        return panel.Children.Count > 0 ? panel : null;
    }

    private static Button MakeButton(AiAction action, Action<AiAction> onClick)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new FontIcon { Glyph = action.Glyph, FontSize = 14 },
                new TextBlock { Text = action.Label, FontSize = 13 },
            },
        };

        var button = new Button
        {
            Content = content,
            // 不铺满整行：回答正文那一列有 900 多像素宽，铺满会变成一根空得很长的条，
            // 看着更像横条不像按钮。定个下限让几个按钮宽度一致，靠左排。
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            MinWidth = 200,
            Padding = new Thickness(12, 8, 12, 8),
        };

        ToolTipService.SetToolTip(button, action.Kind == AiActionKind.Theme
            ? "立即切换，只影响本应用的显示"
            : "打开对应页面，改动由你自己确认");

        // ⚠️ Content 是 StackPanel 时按钮的 UIA Name 是**空的**（内容自绘的按钮都这样），
        //    读屏软件会念成「按钮」，也定位不到。名字必须显式给。
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, action.Label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, "AiAction_" + action.Id);

        button.Click += (_, _) => onClick(action);
        return button;
    }
}
