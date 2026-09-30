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
    private bool _syncingMode;      // SetMode 回写模式卡时挡一下 Checked 回调

    public TimerToolPage()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => SyncInputs();   // 主题换了重刷大号数字颜色
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

        // 初始模式：倒计时（卡片初值一律在代码里赋，不能写在 XAML 上）
        SetMode(Mode.Countdown);
    }

    private Microsoft.UI.Xaml.Media.Brush Res(string key, Windows.UI.Color fallback) => Services.ThemeBrush.Get(this, key);

    /// <summary>把计时器丢到工具浮窗里跑。</summary>
    private void OpenPalette_Click(object sender, RoutedEventArgs e)
        => Views.ToolPaletteWindow.ShowTool("timer");

    /// <summary>两张模式卡谁被选中就切到哪个模式（事件挂在 XAML 的 Checked 上）。</summary>
    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingMode) return;      // SetMode 自己在回写卡片状态，别绕回来
        SetMode(BtnCountdown.IsChecked == true ? Mode.Countdown : Mode.Stopwatch);
    }

    private void SetMode(Mode mode)
    {
        _timer.Stop();
        _running = false;
        _finished = false;
        _mode = mode;
        _remainMs = _totalMs;
        _baseMs = 0;
        _sw.Reset();

        // 卡片状态跟着走（从「预设分钟数」按钮进来时也可能要切回倒计时）
        _syncingMode = true;
        BtnCountdown.IsChecked = mode == Mode.Countdown;
        BtnStopwatch.IsChecked = mode == Mode.Stopwatch;
        _syncingMode = false;

        // 设定时长那一栏：倒计时显示输入框，秒表显示说明 —— 两种模式共用同一个位置，
        // 不再是一边有内容、另一边留一大块空白（原来秒表模式下整块 Collapsed）。
        var countdown = mode == Mode.Countdown;
        SetupTitle.Text = countdown ? "设定时长" : "秒表模式";
        SetRow.Visibility = countdown ? Visibility.Visible : Visibility.Collapsed;
        StopwatchHint.Visibility = countdown ? Visibility.Collapsed : Visibility.Visible;

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
        if (_finished) Display.Foreground = Services.ThemeBrush.AccentText(this);
        else Display.ClearValue(TextBlock.ForegroundProperty);
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
