using System;
using System.IO;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 全屏时钟窗口：铺满屏幕，按 Esc / 双击屏幕 / 右上角那颗按钮退出；外观完全跟随页面里的设置。
///
/// ⚠️ **同一时刻只允许存在一个**（2026-09-27 改）。
///    以前每次点「全屏时钟」都 new 一个，触屏上连点几下就叠出十几二十个全屏窗口 ——
///    它们长得一模一样、又都盖满屏幕，看起来就是「点了一下，然后卡死了 / 关不掉了」。
///    现场反馈原话：「为什么能开一堆全屏时钟」。
///    现在统一走 <see cref="Show"/>：已经开着就**就地更新设置并拉到前台**，不再叠新的。
/// </summary>
public sealed partial class ClockFullscreenWindow : Window
{
    /// <summary>当前开着的这一个（没有则为 null）。整个进程唯一的全屏时钟。</summary>
    private static ClockFullscreenWindow? _current;

    private ClockSettings _settings;
    private bool _dark;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;

    /// <summary>退出入口的淡出倒计时（毫秒）。到 0 就藏起来，别一直压着时钟。</summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _exitTimer;
    private int _exitRemainMs;

    /// <summary>退出按钮可见时给多久（毫秒）；末段用来做淡出。</summary>
    private const int ExitHoldMs = 4500;
    private const int ExitFadeMs = 900;

    private AppWindow? _appWindow;

    /// <summary>
    /// 唯一入口：开一个全屏时钟。已经开着就复用（拉到最前），**不会**再多出一个窗口。
    ///
    /// <paramref name="settings"/> 传 null = 「只把已经开着的那个调到前面来，别动它的外观」。
    /// 侧边栏那个小浮窗的「全屏时钟」按钮就是这样调的：它手上只有一套默认设置，
    /// 要是拿它去覆盖用户刚在工具页里调好的底色/字体，就变成「点一下外观被打回原形」了。
    /// </summary>
    public static void Show(ClockSettings? settings, bool darkTheme)
    {
        if (_current is not null)
        {
            try
            {
                if (settings is not null) _current.UpdateSettings(settings, darkTheme);
                _current.Activate();
                _current.RevealExit();
                return;
            }
            catch
            {
                // 极端情况下（窗口已销毁但字段没清）兜一下：丢掉引用重新开
                _current = null;
            }
        }

        var window = new ClockFullscreenWindow(settings ?? new ClockSettings(), darkTheme);
        _current = window;
        window.Start();
    }

    public ClockFullscreenWindow(ClockSettings settings, bool darkTheme)
    {
        _settings = settings;
        _dark = darkTheme;
        InitializeComponent();
        Title = "全屏时钟";

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
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
            _appWindow.Title = "全屏时钟";
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
            }
            _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

            // 铺满整个物理屏幕（用屏幕外框而不是工作区，差几个像素就会看起来"没盖住"）
            var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
            if (area is not null) _appWindow.MoveAndResize(area.OuterBounds);

            // ⚠️ Win11 上全屏窗口默认还带圆角 + 一圈细边框 → 屏幕四角/边上会露出底下的桌面，
            // 看着像"屏幕外面套了一圈"。这里把圆角关掉、边框设成无色。
            // （Win10 不认识这两个属性，调用失败无所谓。）
            WindowChrome.RemoveBorder(hwnd, rounded: false, dark: _dark);
        }
        catch { /* 全屏失败也能当普通窗口用 */ }

        Apply();

        Tick();
        _timer.Start();

        // 第一次进来自动把「怎么退出」亮几秒 —— 触屏用户没有 Esc 键，得让他看见
        RevealExit();

        Root.Focus(FocusState.Programmatic);
    }

    /// <summary>就地换一套外观设置（已经在全屏了就改这一份，不新开窗口）。</summary>
    private void UpdateSettings(ClockSettings settings, bool darkTheme)
    {
        _settings = settings;
        _dark = darkTheme;
        Apply();
        Tick();
    }

    private bool HasPhoto => !string.IsNullOrWhiteSpace(_settings.BackgroundImagePath)
                             && File.Exists(_settings.BackgroundImagePath);

    private void Apply()
    {
        var face = new SolidColorBrush(ClockRender.FaceColor(_settings, _dark, HasPhoto));
        TimeText.Foreground = face;
        SecText.Foreground = face;
        DateText.Foreground = face;

        if (HasPhoto)
        {
            try
            {
                Root.Background = new ImageBrush
                {
                    ImageSource = new BitmapImage(new Uri(_settings.BackgroundImagePath)),
                    Stretch = Stretch.UniformToFill,
                };
            }
            catch
            {
                Root.Background = ClockRender.BaseBrush(_settings, _dark);
            }
        }
        else
        {
            Root.Background = ClockRender.BaseBrush(_settings, _dark);
        }

        Veil.Background = ClockRender.VeilBrush(_settings);

        TimeText.FontFamily = new FontFamily(_settings.FontFamily);
        SecText.FontFamily = new FontFamily(_settings.FontFamily);
        DateText.FontFamily = new FontFamily(_settings.FontFamily);
    }

    private void Tick()
    {
        var now = DateTime.Now;
        TimeText.Text = ClockRender.TimeText(now, _settings.Hour12);
        SecText.Text = ClockRender.SecText(now);
        DateText.Text = ClockRender.DateText(now);
        SecText.Visibility = _settings.ShowSeconds ? Visibility.Visible : Visibility.Collapsed;
        DateText.Visibility = _settings.ShowDate ? Visibility.Visible : Visibility.Collapsed;
        Layout();
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => Layout();

    /// <summary>按屏幕尺寸算大字号（网页版：有时/分时 min(30vw,46vh)，带秒时 min(22vw,34vh)）。</summary>
    private void Layout()
    {
        var w = Root.ActualWidth;
        var h = Root.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var baseSize = (_settings.ShowSeconds ? Math.Min(w * 0.22, h * 0.34) : Math.Min(w * 0.30, h * 0.46))
                       * _settings.Scale;
        if (baseSize <= 1) return;

        TimeText.FontSize = baseSize;
        SecText.FontSize = baseSize * 0.5;
        SecText.Margin = new Thickness(0, 0, 0, baseSize * 0.18);
        DateText.FontSize = Math.Max(16, Math.Min(w * 0.024, h * 0.04) * _settings.Scale);
    }

    // ══════════ 退出入口 ══════════

    /// <summary>把「怎么退出」那块亮出来，并重新开始倒计时。</summary>
    private void RevealExit()
    {
        _exitRemainMs = ExitHoldMs;
        ExitBar.Opacity = 1;
        ExitBar.IsHitTestVisible = true;
        if (!_exitTimer.IsRunning) _exitTimer.Start();
    }

    /// <summary>倒计时收尾：最后一段淡出，到点就彻底藏掉（并交出命中测试，别挡住双击退出）。</summary>
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
        // 鼠标划过 / 手指按一下都算「他在找退出」，重新亮出来
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
