using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassSoftwareHub.Desktop.Views.Palette;

/// <summary>
/// 浮窗版课堂计时器（简版）：预设分钟 + 大号倒计时 + 开始/暂停/重置，到点响铃并闪烁。
/// 状态只在内存里（和「内置工具 → 课堂计时器」页一样，两边各自独立）。
/// </summary>
public sealed partial class MiniTimer : UserControl
{
    [DllImport("kernel32.dll")]
    private static extern bool Beep(uint dwFreq, uint dwDuration);

    private readonly DispatcherQueueTimer _tick;
    private readonly DispatcherQueueTimer _blink;
    private readonly Stopwatch _sw = new();

    private long _totalMs = 5 * 60 * 1000;
    private long _remainMs = 5 * 60 * 1000;
    private long _endAtMs;
    private bool _running;
    private bool _finished;

    public MiniTimer()
    {
        InitializeComponent();

        // 主题换了要重刷大号数字的颜色（代码里设的颜色不会自己跟着变）
        ActualThemeChanged += (_, _) => UpdateDisplay();

        _tick = DispatcherQueue.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(100);
        _tick.IsRepeating = true;
        _tick.Tick += (_, _) => OnTick();

        _blink = DispatcherQueue.CreateTimer();
        _blink.Interval = TimeSpan.FromMilliseconds(450);
        _blink.IsRepeating = true;
        _blink.Tick += (_, _) => Display.Opacity = Display.Opacity < 0.9 ? 1 : 0.35;

        // 自定义分/秒：改一下就立刻生效（不用再点"应用"）
        MinuteStepper.ValueChanged += (_, _) => TimeSettingChanged();
        SecondStepper.ValueChanged += (_, _) => TimeSettingChanged();

        _suppressTime = true;
        MinuteStepper.Value = 5;
        SecondStepper.Value = 0;
        _suppressTime = false;
        _totalMs = 5 * 60 * 1000;
        _remainMs = _totalMs;

        UpdateDisplay();
        SyncInputs();
    }

    private bool _pendingResume;
    private bool _suppressTime;      // 构造 / 程序里改步进器时不要再回头算一遍

    /// <summary>自定义时间改了：没在跑就直接换成新时长。</summary>
    private void TimeSettingChanged()
    {
        if (_suppressTime || _running) return;

        _blink.Stop();
        _finished = false;
        _sw.Reset();
        _totalMs = (MinuteStepper.Value * 60L + SecondStepper.Value) * 1000L;
        _remainMs = _totalMs;
        UpdateDisplay();
        SyncInputs();
    }

    /// <summary>预设按钮 → 把分/秒步进器设成对应值（然后由 TimeSettingChanged 统一生效）。</summary>
    private void SetPreset(int minutes)
    {
        _tick.Stop();
        _blink.Stop();
        _running = false;
        _finished = false;
        _sw.Reset();

        _suppressTime = true;
        MinuteStepper.Value = minutes;
        SecondStepper.Value = 0;
        _suppressTime = false;

        _totalMs = minutes * 60_000L;
        _remainMs = _totalMs;
        UpdateDisplay();
        SyncInputs();
    }

    private Brush Res(string key, Windows.UI.Color fallback) => Services.ThemeBrush.Get(this, key);

    /// <summary>浮窗收起：停掉刷新（计时状态和剩余时间都留着，再打开接着走）。</summary>
    public void Pause()
    {
        _tick.Stop();
        _blink.Stop();
        if (_running)
        {
            _remainMs = Math.Max(0, _endAtMs - _sw.ElapsedMilliseconds);
            _sw.Reset();
            _pendingResume = true;
        }
        Display.Opacity = 1;
    }

    /// <summary>浮窗再打开：还在跑的话接着跑。</summary>
    public void Resume()
    {
        if (!_pendingResume) return;
        _pendingResume = false;
        _endAtMs = _remainMs;
        _sw.Restart();
        _tick.Start();
        _running = true;
        UpdateDisplay();
        SyncInputs();
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag || !int.TryParse(tag, out var minutes)) return;
        SetPreset(minutes);
    }

    private void StartPause_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            _remainMs = Math.Max(0, _endAtMs - _sw.ElapsedMilliseconds);
            _sw.Reset();
            _tick.Stop();
            _running = false;
            UpdateDisplay();
            SyncInputs();
            return;
        }

        if (_totalMs <= 0) return;      // 时长是 0：先设个时间（按钮此时是灰的，这里兜个底）

        if (_finished || _remainMs <= 0)
        {
            _remainMs = _totalMs;
            _finished = false;
        }

        _endAtMs = _remainMs;
        _blink.Stop();
        _sw.Restart();
        _tick.Start();
        _running = true;
        UpdateDisplay();
        SyncInputs();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _tick.Stop();
        _blink.Stop();
        _running = false;
        _finished = false;
        _sw.Reset();
        _remainMs = _totalMs;
        UpdateDisplay();
        SyncInputs();
    }

    private void OnTick()
    {
        _remainMs = _endAtMs - _sw.ElapsedMilliseconds;
        if (_remainMs <= 0)
        {
            _remainMs = 0;
            Finish();
            return;
        }
        UpdateDisplay();
    }

    private void Finish()
    {
        _tick.Stop();
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

    private void SyncInputs()
    {
        foreach (var child in PresetRow.Children)
            if (child is Button b) b.IsEnabled = !_running;

        // 跑起来之后不让改时间（改了也说不清是"这一轮"还是"下一轮"），要改先暂停
        MinuteStepper.IsEnabled = !_running;
        SecondStepper.IsEnabled = !_running;
        StartButton.IsEnabled = _totalMs > 0 || _running;

        Display.Opacity = 1;
        if (_finished) Display.Foreground = Services.ThemeBrush.AccentText(this);   // 计到点了：主题色
        else Display.ClearValue(TextBlock.ForegroundProperty);                      // 平常：交回 XAML 里的 {ThemeResource ...}
    }

    private void UpdateDisplay()
    {
        var total = Math.Max(0, (long)Math.Round(_remainMs / 1000.0));
        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;
        Display.Text = h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
        StartButton.Content = _running ? "暂停" : (_finished ? "重新开始" : "开始");

        var pct = _totalMs > 0 ? Math.Clamp((double)_remainMs / _totalMs, 0, 1) : 0;
        Bar.Value = pct * 100;
    }
}
