using System;
using System.Diagnostics;
using ClassSoftwareHub.Desktop.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Views.Palette;

/// <summary>
/// 浮窗版秒表（正计时 + 全屏）：课堂上做限时活动、比赛计时用。
/// 状态只在内存里，收起浮窗时暂停、再打开接着走（跟其它小工具一致）。
///
/// 2026-10-03（Nick）：**计次/分段取消**（原来最多记三次，实测教室里没人用，还占掉半屏）。
/// 新增「全屏」—— 走 <see cref="StopwatchFullscreenWindow"/>，跟全屏时钟同一个套路。
/// </summary>
public sealed partial class MiniStopwatch : UserControl
{
    private readonly DispatcherQueueTimer _tick;
    private readonly Stopwatch _sw = new();

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

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _tick.Stop();
        _sw.Reset();
        _accMs = 0;
        _running = false;
        _pendingResume = false;
        StartButton.Content = "开始";
        UpdateDisplay();
    }

    /// <summary>全屏：把当前秒表时间交给全屏窗口，跟着一起走（关闭全屏不影响这里的表）。</summary>
    private void Fullscreen_Click(object sender, RoutedEventArgs e)
        => StopwatchFullscreenWindow.Show(() => ElapsedMs, () => _running);

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

        StateText.Text = _running ? "计时中" : (ElapsedMs > 0 ? "已暂停" : "未开始");
    }
}
