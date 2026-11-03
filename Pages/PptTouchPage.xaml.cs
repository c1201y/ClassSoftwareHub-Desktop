using System;
using System.Collections.Generic;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 「PowerPoint 触控优化」页（需求清单第 2 条，tag = <c>ppttouch</c>）。
///
/// 识别与补点击都在 <see cref="PptTouch"/>，菜单内容与动作在 <see cref="PptMenu"/>，
/// 这里只管开关、额外菜单项的勾选与状态。
/// ⚠️ 状态每秒刷一次：放映是否在跑是外部状态，页面不做订阅，轮询最省事。
/// </summary>
public sealed partial class PptTouchPage : Page
{
    private readonly DispatcherQueueTimer _timer;

    /// <summary>构造期给开关设初值会触发 Toggled / Checked，铺完初值再放行。</summary>
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

    /// <summary>
    /// 铺「额外菜单项」的勾选框。
    /// ⚠️ 清单只认 <see cref="PptMenu.Extras"/>，这里不再抄一份文案 —— 两处维护必然会写歪。
    /// </summary>
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

        // 没检测到时把 PowerPoint 现有的窗口类名写进日志（内部限频 30 秒），
        // 下次「明明在放映却显示未检测到」就能直接从日志看出是类名不符还是真没放映。
        if (!showing) PptTouch.DescribePowerPointWindows();

        // 只统计「真正补过动作」的次数：没补过就不显示，免得页面上挂一行 0。
        var taps = PptTouch.Taps;
        TapCountText.Text = taps > 0 ? $"已翻页 {taps} 次" : "";
    }
}
