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

/// <summary>全屏时钟窗口：铺满屏幕，按 Esc 或双击退出；外观完全跟随页面里的设置。</summary>
public sealed partial class ClockFullscreenWindow : Window
{
    private readonly ClockSettings _settings;
    private readonly bool _dark;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private AppWindow? _appWindow;

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
        }
        catch { /* 全屏失败也能当普通窗口用 */ }

        Apply();

        Tick();
        _timer.Start();
        Root.Focus(FocusState.Programmatic);
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

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) Close();
    }

    private void Root_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => Close();

    private void OnClosed(object sender, WindowEventArgs args) => _timer.Stop();
}
