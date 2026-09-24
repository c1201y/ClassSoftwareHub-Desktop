using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Views.Palette;

/// <summary>
/// 浮窗版秒表（正计时 + 计次）：课堂上用「谁先举手 / 谁先做完」这类场景。
/// 状态只在内存里，收起浮窗时暂停、再打开接着走（跟其它小工具一致）。
/// </summary>
public sealed partial class MiniStopwatch : UserControl
{
    /// <summary>最多留几次计次（多了也没地方显示）。</summary>
    private const int MaxLaps = 3;

    private readonly DispatcherQueueTimer _tick;
    private readonly Stopwatch _sw = new();
    private readonly List<long> _laps = new();      // 每次计次时的累计毫秒

    private long _accMs;          // 暂停前累计的毫秒
    private bool _running;
    private bool _pendingResume;  // 收起时还在跑，再打开要接着跑

    public MiniStopwatch()
    {
        InitializeComponent();

        _tick = DispatcherQueue.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(50);
        _tick.IsRepeating = true;
        _tick.Tick += (_, _) => UpdateDisplay();

        LapButton.IsEnabled = false;
        UpdateDisplay();
    }

    private long ElapsedMs => _accMs + (_running ? _sw.ElapsedMilliseconds : 0);

    // ── 控制 ────────────────────────────────────────────────

    private void StartPause_Click(object sender, RoutedEventArgs e)
    {
        if (_running) PauseInternal();
        else StartInternal();
    }

    private void StartInternal()
    {
        _sw.Restart();
        _running = true;
        _tick.Start();
        StartButton.Content = "暂停";
        LapButton.IsEnabled = true;
        UpdateDisplay();
    }

    private void PauseInternal()
    {
        if (_running)
        {
            _accMs += _sw.ElapsedMilliseconds;
            _sw.Reset();
        }
        _running = false;
        _tick.Stop();
        StartButton.Content = ElapsedMs > 0 ? "继续" : "开始";
        UpdateDisplay();
    }

    private void Lap_Click(object sender, RoutedEventArgs e)
    {
        if (!_running) return;

        _laps.Add(ElapsedMs);
        while (_laps.Count > MaxLaps) _laps.RemoveAt(0);
        RefreshLaps();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _tick.Stop();
        _sw.Reset();
        _accMs = 0;
        _running = false;
        _pendingResume = false;
        _laps.Clear();
        StartButton.Content = "开始";
        LapButton.IsEnabled = false;
        LapText.Text = "";
        UpdateDisplay();
    }

    // ── 收起 / 再打开（浮窗隐藏时别白烧 CPU） ──────────────────

    /// <summary>浮窗收起：停表但留着状态。</summary>
    public void Pause()
    {
        _tick.Stop();
        if (_running)
        {
            _accMs += _sw.ElapsedMilliseconds;
            _sw.Reset();
            _running = false;
            _pendingResume = true;
        }
        UpdateDisplay();
    }

    /// <summary>浮窗再打开：刚才在跑就接着跑。</summary>
    public void Resume()
    {
        if (!_pendingResume) return;
        _pendingResume = false;
        StartInternal();
    }

    // ── 显示 ────────────────────────────────────────────────

    private void UpdateDisplay()
    {
        var ms = ElapsedMs;
        var hours = ms / 3_600_000;
        var minutes = ms % 3_600_000 / 60_000;
        var seconds = ms % 60_000 / 1000;
        var hundredths = ms % 1000 / 10;

        TimeText.Text = hours > 0
            ? $"{hours}:{minutes:00}:{seconds:00}"
            : $"{minutes:00}:{seconds:00}";
        CsText.Text = $".{hundredths:00}";
    }

    private void RefreshLaps()
    {
        if (_laps.Count == 0)
        {
            LapText.Text = "";
            return;
        }

        // 从新到旧列出来，只留最近三次
        var lines = new List<string>();
        for (var i = _laps.Count - 1; i >= 0; i--)
        {
            var ms = _laps[i];
            var index = _laps.Count - i;
            lines.Add($"第 {index} 次  {Format(ms)}");
        }
        LapText.Text = string.Join("　·　", lines);
    }

    private static string Format(long ms)
    {
        var minutes = ms / 60_000;
        var seconds = ms % 60_000 / 1000;
        var hundredths = ms % 1000 / 10;
        return minutes > 0 ? $"{minutes}:{seconds:00}.{hundredths:00}" : $"{seconds}.{hundredths:00}";
    }
}
