using System;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 「PowerPoint 触控优化」页（需求清单第 2 条，tag = <c>ppttouch</c>）。
///
/// 识别与补点击都在 <see cref="PptTouch"/>，这里只管开关与状态。
/// ⚠️ 状态每秒刷一次：放映是否在跑是外部状态，页面不做订阅，轮询最省事。
/// </summary>
public sealed partial class PptTouchPage : Page
{
    private readonly DispatcherQueueTimer _timer;

    /// <summary>构造期给 ToggleSwitch 设初值会触发 Toggled，铺完初值再放行。</summary>
    private bool _ready;

    public PptTouchPage()
    {
        InitializeComponent();

        EnableSwitch.IsOn = PptTouchConfig.Load().Enabled;
        _ready = true;

        UpdateState();

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => UpdateState();
        _timer.Start();

        Unloaded += (_, _) => _timer.Stop();
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

    private void UpdateState()
    {
        RunStateText.Text = PptTouch.IsActive
            ? "轻点翻页已开启，程序运行期间一直生效。"
            : "轻点翻页未开启。";

        ShowStateText.Text = PptTouch.IsSlideshowShowing()
            ? "当前检测到 PowerPoint 正在放映。"
            : "当前未检测到 PowerPoint 放映。";

        TapCountText.Text = PptTouch.Taps > 0 ? $"已翻页 {PptTouch.Taps} 次" : "";
    }
}
