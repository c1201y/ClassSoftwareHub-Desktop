using System;
using System.Collections.Generic;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Pages;








public sealed partial class PptTouchPage : Page
{
    private readonly DispatcherQueueTimer _timer;

    
    private bool _ready;

    public PptTouchPage()
    {
        InitializeComponent();

        var config = PptTouchConfig.Load();
        EnableSwitch.IsOn = config.Enabled;
        MenuSwitch.IsOn = config.CustomMenu;
        BuildExtras(config.MenuExtras);

        _ready = true;

        UpdateState();

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => UpdateState();
        _timer.Start();

        Unloaded += (_, _) => _timer.Stop();
    }

    
    
    
    
    private void BuildExtras(ICollection<string> selected)
    {
        ExtraHost.Children.Clear();

        foreach (var extra in PptMenu.Extras)
        {
            var box = new CheckBox
            {
                Content = extra.Text,
                IsChecked = selected.Contains(extra.Id),
                Tag = extra.Id,
                MinWidth = 0,
            };
            box.Checked += Extra_Changed;
            box.Unchecked += Extra_Changed;

            var tip = new TextBlock
            {
                Text = extra.Note,
                FontSize = 12,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(28, -4, 0, 8),
            };

            ExtraHost.Children.Add(box);
            ExtraHost.Children.Add(tip);
        }
    }

    private void EnableSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;

        var config = PptTouchConfig.Load();
        config.Enabled = EnableSwitch.IsOn;
        config.Save();

        if (config.Enabled) PptTouch.Start();
        else PptTouch.Stop();

        UpdateState();
    }

    private void MenuSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;

        var config = PptTouchConfig.Load();
        config.CustomMenu = MenuSwitch.IsOn;
        config.Save();

        PptTouch.ApplyConfig();
        UpdateState();
    }

    private void Extra_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;

        var picked = new List<string>();
        foreach (var child in ExtraHost.Children)
        {
            if (child is CheckBox { IsChecked: true, Tag: string id }) picked.Add(id);
        }

        var config = PptTouchConfig.Load();
        config.MenuExtras = picked;
        config.Save();
    }

    private void UpdateState()
    {
        if (PptTouch.IsActive)
        {
            RunStateText.Text = MenuSwitch.IsOn
                ? "触控手势与长按菜单已开启，程序运行期间一直生效。"
                : "触控手势已开启，长按菜单已关闭。";
        }
        else
        {
            RunStateText.Text = "触控手势未开启。";
        }

        var showing = PptTouch.IsSlideshowShowing();
        ShowStateText.Text = showing
            ? "当前检测到 PowerPoint 正在放映。"
            : "当前未检测到 PowerPoint 放映。";

        
        
        if (!showing) PptTouch.DescribePowerPointWindows();

        
        var taps = PptTouch.Taps;
        TapCountText.Text = taps > 0 ? $"已翻页 {taps} 次" : "";
    }
}
