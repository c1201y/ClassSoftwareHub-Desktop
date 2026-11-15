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











public sealed partial class StopwatchFullscreenWindow : Window
{
    
    private static StopwatchFullscreenWindow? _current;

    
    private readonly Func<long> _elapsed;

    
    private readonly Func<bool> _running;

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _exitTimer;

    
    private int _exitRemainMs;

    private const int ExitHoldMs = 4500;
    private const int ExitFadeMs = 900;

    private AppWindow? _appWindow;

    
    
    
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

        
        if (App.Settings.Current.TimerUseClockBackground)
            ClockBackdrop.Apply(Root, BgImage, BgVeil, null, TimeText, CsText, StateText);

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
        catch {  }

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
            
            _timer.Stop();
            Close();
        }
    }

    

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
