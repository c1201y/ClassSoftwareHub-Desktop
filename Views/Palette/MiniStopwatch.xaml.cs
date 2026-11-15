using System;
using System.Diagnostics;
using ClassSoftwareHub.Desktop.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Views.Palette;








public sealed partial class MiniStopwatch : UserControl
{
    private readonly DispatcherQueueTimer _tick;
    private readonly Stopwatch _sw = new();

    private long _accMs;          
    private bool _running;
    private bool _pendingResume;  

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

    
    private void Fullscreen_Click(object sender, RoutedEventArgs e)
        => StopwatchFullscreenWindow.Show(() => ElapsedMs, () => _running);

    

    
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

    
    public void Resume()
    {
        if (!_pendingResume) return;
        _pendingResume = false;
        StartInternal();
    }

    

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
