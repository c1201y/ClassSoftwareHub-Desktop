using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace ClassSoftwareHub.Desktop.Controls;

/// <summary>
/// Hub AI 提问浮层（2026-10-30 从软件下载页抽成公共控件，需求 1:23）。
///
/// <para>
/// 用法：页面根 Grid 里放一行 <c>&lt;ui:HubAiFlyout x:Name="AiFlyout" /&gt;</c>，
/// 页头放一个按钮，点击调 <see cref="Show"/>。控件自己负责位置（右下角）、动画和发送。
/// </para>
///
/// <para>
/// ⚠️ 浮层**不做对话**（2026-10-18 定）：它只是"把问题带去完整页"的入口。
/// 完整页有历史、工具调用、推荐卡片、用量统计，在这里重做一套必然两边行为不一致。
/// </para>
/// </summary>
public sealed partial class HubAiFlyout : UserControl
{
    public HubAiFlyout()
    {
        InitializeComponent();
    }

    /// <summary>浮层当前是否展开（页面想在切走时收起，可以问它）。</summary>
    public bool IsOpen => Card.Visibility == Visibility.Visible;

    /// <summary>展开，并把焦点给输入框 —— 用户点进来的目的就是打字。</summary>
    public void Show()
    {
        if (IsOpen)
        {
            Input.Focus(FocusState.Programmatic);
            return;
        }

        Card.Visibility = Visibility.Visible;

        // ⚠️ 必须先摆到起点：DoubleAnimation 只给 To 时会从**当前值**动画过去，
        //    不设起点就是 0→0，只剩淡入、看不出滑动。
        CardTransform.Y = 24;
        Card.Opacity = 0;

        Animate(toY: 0, toOpacity: 1, durationMs: 240);
        Input.Focus(FocusState.Programmatic);
    }

    /// <summary>收起。动画结束后才真正 Collapsed（过早设会让动画直接消失）。</summary>
    public void Hide()
    {
        if (!IsOpen) return;

        Animate(toY: 24, toOpacity: 0, durationMs: 170,
            onDone: () => Card.Visibility = Visibility.Collapsed);
    }

    /// <summary>展开 / 收起切换（按钮点一下用这个）。</summary>
    public void Toggle()
    {
        if (IsOpen) Hide();
        else Show();
    }

    // ── 按钮 ─────────────────────────────────────────────────────────────

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>
    /// 升到完整页。走 <c>Shell.NavigateTo</c> 而不是 <c>Frame.Navigate</c> ——
    /// 后者只换内容区，导航栏的选中态还停在原处，看着像点错了。
    /// </summary>
    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        App.MainWindow?.Shell.NavigateTo("hubai");
    }

    private void Send_Click(object sender, RoutedEventArgs e) => Send();

    private void Input_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        Send();
    }

    /// <summary>
    /// 发送 = 把问题交给完整页，不在浮层里做对话（2026-10-18）。
    /// 完整页从 <c>HubAiPage.PendingQuestion</c> 取走之后会自动发出去。
    /// </summary>
    private void Send()
    {
        var text = Input.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        Input.Text = string.Empty;
        Hide();

        Pages.HubAiPage.PendingQuestion = text;
        App.MainWindow?.Shell.NavigateTo("hubai");
    }

    // ── 动画 ─────────────────────────────────────────────────────────────

    /// <summary>滑入滑出。⚠️ 位移与透明度都是**非合成动画**，必须开 <c>EnableDependentAnimation</c> 才会动。</summary>
    private void Animate(double toY, double toOpacity, int durationMs, Action? onDone = null)
    {
        var slide = new DoubleAnimation
        {
            To = toY,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            EasingFunction = new CubicEase
            {
                EasingMode = toOpacity > 0.5 ? EasingMode.EaseOut : EasingMode.EaseIn,
            },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(slide, CardTransform);
        Storyboard.SetTargetProperty(slide, "Y");

        var fade = new DoubleAnimation
        {
            To = toOpacity,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, Card);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);

        if (onDone is not null) storyboard.Completed += (_, _) => onDone();

        storyboard.Begin();
    }
}
