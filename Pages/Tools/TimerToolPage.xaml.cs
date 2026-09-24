using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>课堂计时器：倒计时 / 秒表，到点响铃（大字号方便投影）。</summary>
public sealed partial class TimerToolPage : Page
{
    private enum Mode { Countdown, Stopwatch }

    private const double BarWidth = 520;

    [DllImport("kernel32.dll")]
    private static extern bool Beep(uint dwFreq, uint dwDuration);

    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueueTimer _blink;
    private readonly Stopwatch _sw = new();

    private Mode _mode = Mode.Countdown;
    private long _totalMs = 5 * 60 * 1000;
    private long _remainMs = 5 * 60 * 1000;
    private long _baseMs;
    private long _endAtMs;
    private bool _running;
    private bool _finished;

    public TimerToolPage()
    {
        InitializeComponent();
        MinBox.ValueChanged += Time_ValueChanged;
        SecBox.ValueChanged += Time_ValueChanged;
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => OnTick();

        // 到点后大数字闪烁（网页版是 0.9s 一次，这里用 450ms 明暗交替）
        _blink = DispatcherQueue.CreateTimer();
        _blink.Interval = TimeSpan.FromMilliseconds(450);
        _blink.IsRepeating = true;
        _blink.Tick += (_, _) => Display.Opacity = Display.Opacity < 0.9 ? 1 : 0.35;

        // 离开页面就停表（定时器的委托会把页面钉在内存里）
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            _blink.Stop();
            Display.Opacity = 1;
        };

        ApplyTime();
        UpdateDisplay();
    }

    private static Microsoft.UI.Xaml.Media.Brush Res(string key, Windows.UI.Color fallback)
    {
        try { return (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key]; }
        catch { return new Microsoft.UI.Xaml.Media.SolidColorBrush(fallback); }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(ToolsPage));
    }

    /// <summary>把计时器丢到工具浮窗里跑。</summary>
    private void OpenPalette_Click(object sender, RoutedEventArgs e)
        => Views.ToolPaletteWindow.ShowTool("timer");

    private void Mode_Countdown_Click(object sender, RoutedEventArgs e) => SetMode(Mode.Countdown);

    private void Mode_Stopwatch_Click(object sender, RoutedEventArgs e) => SetMode(Mode.Stopwatch);

    private void SetMode(Mode mode)
    {
        _timer.Stop();
        _running = false;
        _finished = false;
        _mode = mode;
        _remainMs = _totalMs;
        _baseMs = 0;
        _sw.Reset();

        BtnCountdown.Content = mode == Mode.Countdown ? "● 倒计时" : "倒计时";
        BtnStopwatch.Content = mode == Mode.Stopwatch ? "● 秒表" : "秒表";
        SetupPanel.Visibility = mode == Mode.Countdown ? Visibility.Visible : Visibility.Collapsed;
        _blink.Stop();
        UpdateDisplay();
        SyncInputs();
    }

    private void StartPause_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            if (_mode == Mode.Countdown) _remainMs = Math.Max(0, _endAtMs - _sw.ElapsedMilliseconds);
            else _baseMs += _sw.ElapsedMilliseconds;
            _sw.Reset();
            _timer.Stop();
            _running = false;
            UpdateDisplay();
            SyncInputs();
            return;
        }

        if (_finished)
        {
            _remainMs = _totalMs;
            _baseMs = 0;
            _finished = false;
        }
        if (_mode == Mode.Countdown && _remainMs <= 0) _remainMs = _totalMs;
        if (_mode == Mode.Countdown) _endAtMs = _remainMs;

        _blink.Stop();
        _sw.Restart();
        _timer.Start();
        _running = true;
        UpdateDisplay();
        SyncInputs();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _blink.Stop();
        _running = false;
        _finished = false;
        _remainMs = _totalMs;
        _baseMs = 0;
        _sw.Reset();
        UpdateDisplay();
        SyncInputs();
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag || !int.TryParse(tag, out var minutes)) return;
        SetMode(Mode.Countdown);
        MinBox.Value = minutes;
        SecBox.Value = 0;
        ApplyTime();
    }

    private void Time_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => ApplyTime();

    private void ApplyTime()
    {
        var m = double.IsNaN(MinBox.Value) ? 0 : Math.Max(0, MinBox.Value);
        var s = double.IsNaN(SecBox.Value) ? 0 : Math.Clamp(SecBox.Value, 0, 59);
        _totalMs = (long)((m * 60 + s) * 1000);
        if (_totalMs <= 0) _totalMs = 1000;
        if (!_running)
        {
            _remainMs = _totalMs;
            _finished = false;
            UpdateDisplay();
        }
    }

    private void OnTick()
    {
        if (_mode == Mode.Countdown)
        {
            _remainMs = _endAtMs - _sw.ElapsedMilliseconds;
            if (_remainMs <= 0)
            {
                _remainMs = 0;
                Finish();
                return;
            }
        }
        UpdateDisplay();
    }

    private void Finish()
    {
        _timer.Stop();
        _running = false;
        _finished = true;
        UpdateDisplay();
        SyncInputs();
        _blink.Start();
        Beep(880, 250);
        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            await System.Threading.Tasks.Task.Delay(350);
            Beep(880, 250);
            await System.Threading.Tasks.Task.Delay(350);
            Beep(880, 250);
        });
    }

    /// <summary>运行时锁定时长设置与预设（对齐网页版），并同步"到点"的配色。</summary>
    private void SyncInputs()
    {
        MinBox.IsEnabled = !_running;
        SecBox.IsEnabled = !_running;
        foreach (var child in SetRow.Children)
            if (child is Button b) b.IsEnabled = !_running;

        Display.Opacity = 1;
        Display.Foreground = _finished
            ? Res("AccentTextFillColorPrimaryBrush", Windows.UI.Color.FromArgb(255, 0, 103, 192))
            : Res("TextFillColorPrimaryBrush", Windows.UI.Color.FromArgb(255, 0, 0, 0));
    }

    private void UpdateDisplay()
    {
        long ms;
        if (_mode == Mode.Countdown) ms = _running ? Math.Max(0, _remainMs) : _remainMs;
        else ms = _baseMs + (_running ? _sw.ElapsedMilliseconds : 0);

        var total = Math.Max(0, (long)Math.Round(ms / 1000.0));
        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;
        Display.Text = h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m:00}:{s:00}";

        StartButton.Content = _running ? "暂停" : (_finished ? "重新开始" : "开始");

        var pct = _mode == Mode.Countdown && _totalMs > 0
            ? Math.Clamp((double)_remainMs / _totalMs, 0, 1)
            : 0;
        ProgressFill.Width = BarWidth * pct;
    }
}
