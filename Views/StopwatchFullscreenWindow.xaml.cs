using System;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 全屏秒表窗口：铺满屏幕显示秒表时间，按 Esc / 双击屏幕 / 右上角那颗按钮退出。
///
/// ⚠️ 它是**只读投影**：时间由 <see cref="Show"/> 传进来的取时函数提供，窗口自己不含任何计时状态。
/// 好处是浮窗版秒表和应用端秒表能共用同一个窗口，不会出现"两个时间对不上"。
///
/// ⚠️ **同一时刻只允许存在一个**（跟 <see cref="ClockFullscreenWindow"/> 同一条铁律）：
/// 已经开着就只把它拉到前台，不叠新的 —— 触屏上连点几下就叠出十几个全屏窗口，
/// 现场反馈原话就是「点了一下然后卡死了/关不掉」。
/// </summary>
public sealed partial class StopwatchFullscreenWindow : Window
{
    /// <summary>当前开着的这一个（没有则为 null）。</summary>
    private static StopwatchFullscreenWindow? _current;

    /// <summary>取当前已计毫秒（由调用方提供）。</summary>
    private readonly Func<long> _elapsed;

    /// <summary>源秒表是否正在跑 —— 只用来显示"计时中/已暂停"。</summary>
    private readonly Func<bool> _running;

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _exitTimer;

    /// <summary>退出入口的淡出倒计时（毫秒）。</summary>
    private int _exitRemainMs;

    private const int ExitHoldMs = 4500;
    private const int ExitFadeMs = 900;

    private AppWindow? _appWindow;

    /// <summary>
    /// 唯一入口：开一个全屏秒表。已经开着就复用（拉到最前），**不会**再多出一个窗口。
    /// </summary>
    public static void Show(Func<long> elapsed, Func<bool> running)
    {
        if (_current is not null)
        {
            try
            {
                _current.Activate();
                _current.RevealExit();
                return;
            }
            catch
            {
                // 窗口已销毁但字段没清 —— 丢掉引用重新开
                _current = null;
            }
        }

        var window = new StopwatchFullscreenWindow(elapsed, running);
        _current = window;
        window.Start();
    }

    public StopwatchFullscreenWindow(Func<long> elapsed, Func<bool> running)
    {
        _elapsed = elapsed;
        _running = running;
        InitializeComponent();
        Title = "全屏秒表";

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(50);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Tick();

        _exitTimer = DispatcherQueue.CreateTimer();
        _exitTimer.Interval = TimeSpan.FromMilliseconds(60);
        _exitTimer.IsRepeating = true;
        _exitTimer.Tick += (_, _) => FadeExit();

        Closed += OnClosed;
    }

    public void Start()
    {
        Activate();

        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "全屏秒表";
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
            }
            _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

            var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
            if (area is not null) _appWindow.MoveAndResize(area.OuterBounds);

            WindowChrome.RemoveBorder(hwnd, rounded: false, dark: true);
        }
        catch { /* 全屏失败也能当普通窗口用 */ }

        Tick();
        _timer.Start();
        RevealExit();

        Root.Focus(FocusState.Programmatic);
    }

    private void Tick()
    {
        try
        {
            var ms = _elapsed();
            var hours = ms / 3_600_000;
            var minutes = ms % 3_600_000 / 60_000;
            var seconds = ms % 60_000 / 1000;
            var hundredths = ms % 1000 / 10;

            TimeText.Text = hours > 0 ? $"{hours}:{minutes:00}:{seconds:00}" : $"{minutes:00}:{seconds:00}";
            CsText.Text = $".{hundredths:00}";
            StateText.Text = _running() ? "计时中" : (ms > 0 ? "已暂停" : "未开始");
        }
        catch
        {
            // 取时的那一端已经被关掉了 —— 停表，别让空窗一直挂在屏上
            _timer.Stop();
            Close();
        }
    }

    // ══════════ 退出入口 ══════════

    private void RevealExit()
    {
        _exitRemainMs = ExitHoldMs;
        ExitBar.Opacity = 1;
        ExitBar.IsHitTestVisible = true;
        if (!_exitTimer.IsRunning) _exitTimer.Start();
    }

    private void FadeExit()
    {
        _exitRemainMs -= 60;
        if (_exitRemainMs > 0)
        {
            ExitBar.Opacity = _exitRemainMs >= ExitFadeMs ? 1 : _exitRemainMs / (double)ExitFadeMs;
            return;
        }

        _exitTimer.Stop();
        ExitBar.Opacity = 0;
        ExitBar.IsHitTestVisible = false;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_exitRemainMs < ExitHoldMs - 300 || ExitBar.Opacity <= 0) RevealExit();
    }

    private void Root_Tapped(object sender, TappedRoutedEventArgs e) => RevealExit();

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) Close();
    }

    private void Root_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => Close();

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _timer.Stop();
        _exitTimer.Stop();
        if (ReferenceEquals(_current, this)) _current = null;
    }
}
