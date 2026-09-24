using System;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Views.Palette;

/// <summary>
/// 浮窗版时钟（简版）：大号时间 + 日期，按钮直接进全屏时钟。
/// 外观先按默认值（等宽工业风 Bahnschrift、带秒和日期）；以后要让浮窗跟随设置页那套外观再说。
/// </summary>
public sealed partial class MiniClock : UserControl
{
    private readonly DispatcherQueueTimer _timer;
    private readonly ClockSettings _settings = new();

    public MiniClock()
    {
        InitializeComponent();

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Tick();

        Tick();
        _timer.Start();
    }

    /// <summary>浮窗收起时停表（省电省内存，收起状态下没人看）。</summary>
    public void Pause()
    {
        _timer.Stop();
    }

    /// <summary>浮窗再打开时恢复。</summary>
    public void Resume()
    {
        Tick();
        _timer.Start();
    }

    private void Tick()
    {
        var now = DateTime.Now;
        TimeText.Text = ClockRender.TimeText(now, _settings.Hour12);
        SecText.Text = ClockRender.SecText(now);
        DateText.Text = ClockRender.DateText(now);
        SecText.Visibility = _settings.ShowSeconds ? Visibility.Visible : Visibility.Collapsed;
        DateText.Visibility = _settings.ShowDate ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        var dark = ActualTheme == ElementTheme.Dark;
        new Views.ClockFullscreenWindow(new ClockSettings(), dark).Start();
    }

    /// <summary>
    /// 「详细设置」：浮窗就这么大，放不下字体/底色/遮罩那一套 —— 直接把人送到应用里的时钟工具页。
    /// 顺手把浮窗收起来，免得挡着主界面。
    /// </summary>
    private void Details_Click(object sender, RoutedEventArgs e)
    {
        Views.ToolPaletteWindow.HidePalette();
        App.MainWindow?.OpenToolSettings(typeof(Pages.Tools.ClockToolPage));
    }
}
