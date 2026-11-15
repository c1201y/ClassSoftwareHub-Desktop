using System;
using System.Collections.Generic;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace ClassSoftwareHub.Desktop.Controls;

























public sealed class LevelChipBar
{
    private sealed class Chip
    {
        public LogLevel Level { get; init; }
        public string Label { get; init; } = "";
        public ToggleButton Button { get; init; } = null!;
        public StackPanel Content { get; init; } = null!;
        public TextBlock CountText { get; init; } = null!;
    }

    private readonly List<Chip> _chips = new();

    
    public event Action? SelectionChanged;

    
    public LevelChipBar(Panel host)
    {
        Add(host, LogLevel.Debug, "调试", "\uE721");    
        Add(host, LogLevel.Info, "信息", "\uE946");     
        Add(host, LogLevel.Warning, "警告", "\uE7BA");  
        Add(host, LogLevel.Error, "错误", "\uE783");    

        
        
        foreach (var c in _chips) c.Button.IsChecked = true;
    }

    
    public List<LogLevel> SelectedLevels
    {
        get
        {
            var list = new List<LogLevel>(_chips.Count);
            foreach (var c in _chips)
                if (c.Button.IsChecked == true) list.Add(c.Level);
            return list;
        }
    }

    
    public bool AllOn
    {
        get
        {
            foreach (var c in _chips) if (c.Button.IsChecked != true) return false;
            return true;
        }
    }

    
    public bool AllOff
    {
        get
        {
            foreach (var c in _chips) if (c.Button.IsChecked == true) return false;
            return true;
        }
    }

    
    
    
    
    
    public IReadOnlyCollection<LogLevel>? FilterLevels => AllOn ? null : SelectedLevels;

    
    public void SetCounts(LogStats stats)
    {
        foreach (var c in _chips)
        {
            var n = c.Level switch
            {
                LogLevel.Debug => stats.Debug,
                LogLevel.Warning => stats.Warning,
                LogLevel.Error => stats.Error,
                _ => stats.Info,
            };

            c.CountText.Text = n.ToString("N0");

            
            
            
            c.Button.Opacity = n == 0 ? 0.7 : 1.0;
            ToolTipService.SetToolTip(c.Button, n == 0 ? $"{c.Label}：本次没有" : $"{c.Label}：{n:N0} 条");
        }
    }

    private void Add(Panel host, LogLevel level, string label, string glyph)
    {
        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = LogReader.BrushOf(level),
        };

        var name = new TextBlock { Text = label, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };

        var count = new TextBlock
        {
            Text = "0",
            FontSize = 12,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        content.Children.Add(icon);
        content.Children.Add(name);
        content.Children.Add(count);

        var button = new ToggleButton
        {
            Content = content,
            MinWidth = 0,
            
            Padding = new Thickness(9, 3, 9, 3),
            VerticalAlignment = VerticalAlignment.Center,
        };
        
        ChipLook.Quiet(button);

        var chip = new Chip { Level = level, Label = label, Button = button, Content = content, CountText = count };

        button.Checked += (_, _) => Sync(chip);
        button.Unchecked += (_, _) => Sync(chip);
        button.Click += (_, _) => SelectionChanged?.Invoke();

        _chips.Add(chip);
        host.Children.Add(button);
    }

    private static void Sync(Chip chip) =>
        chip.Content.Opacity = chip.Button.IsChecked == true ? 1.0 : 0.45;
}
