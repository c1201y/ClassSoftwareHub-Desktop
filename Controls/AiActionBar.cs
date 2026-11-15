using System;
using System.Collections.Generic;
using ClassSoftwareHub.Desktop.Services.HubAi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Controls;












public static class AiActionBar
{
    
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
            
            
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            MinWidth = 200,
            Padding = new Thickness(12, 8, 12, 8),
        };

        ToolTipService.SetToolTip(button, action.Kind == AiActionKind.Theme
            ? "立即切换，只影响本应用的显示"
            : "打开对应页面，改动由你自己确认");

        
        
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, action.Label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, "AiAction_" + action.Id);

        button.Click += (_, _) => onClick(action);
        return button;
    }
}
