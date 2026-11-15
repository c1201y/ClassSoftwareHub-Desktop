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










public sealed partial class ClockFullscreenWindow : Window
{
    
    private static ClockFullscreenWindow? _current;

    private ClockSettings _settings;
    private bool _dark;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;

    
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _exitTimer;
    private int _exitRemainMs;

    
    private const int ExitHoldMs = 4500;
    private const int ExitFadeMs = 900;

    private AppWindow? _appWindow;

    
    
    
    
    
    
    
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

            
            var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
            if (area is not null) _appWindow.MoveAndResize(area.OuterBounds);

            
            
            
            WindowChrome.RemoveBorder(hwnd, rounded: false, dark: _dark);
        }
        catch {  }

        Apply();

        Tick();
        _timer.Start();

        
        RevealExit();

        Root.Focus(FocusState.Programmatic);
    }

    
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

        
        var faceFont = new FontFamily(ClockRender.NormalizeFont(_settings.FontFamily));
        TimeText.FontFamily = faceFont;
        SecText.FontFamily = faceFont;
        DateText.FontFamily = faceFont;
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

    
    private void Layout()
    {
        var w = Root.ActualWidth;
        var h = Root.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var baseSize = (_settings.ShowSeconds ? Math.Min(w * 0.22, h * 0.34) : Math.Min(w * 0.30, h * 0.46));

        if (baseSize <= 1) return;

        TimeText.FontSize = baseSize;
        SecText.FontSize = baseSize * 0.5;
        SecText.Margin = new Thickness(0, 0, 0, baseSize * 0.18);
        DateText.FontSize = Math.Max(16, Math.Min(w * 0.024, h * 0.04) * _settings.Scale);
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
