using System;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassSoftwareHub.Desktop.Views.Palette;





public sealed partial class MiniTimer : UserControl
{
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

        
        ActualThemeChanged += (_, _) => UpdateDisplay();

        _tick = DispatcherQueue.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(100);
        _tick.IsRepeating = true;
        _tick.Tick += (_, _) => OnTick();

        _blink = DispatcherQueue.CreateTimer();
        _blink.Interval = TimeSpan.FromMilliseconds(450);
        _blink.IsRepeating = true;
        _blink.Tick += (_, _) => Display.Opacity = Display.Opacity < 0.9 ? 1 : 0.35;

        
        for (var i = 0; i < 24; i++) HourBox.Items.Add(i);
        for (var i = 0; i < 60; i++) { MinuteBox.Items.Add(i); SecondBox.Items.Add(i); }
        HourBox.SelectionChanged += (_, _) => TimeSettingChanged();
        MinuteBox.SelectionChanged += (_, _) => TimeSettingChanged();
        SecondBox.SelectionChanged += (_, _) => TimeSettingChanged();

        _suppressTime = true;
        HourBox.SelectedIndex = 0;
        MinuteBox.SelectedIndex = 5;
        SecondBox.SelectedIndex = 0;
        _suppressTime = false;
        _totalMs = 5 * 60 * 1000;
        _remainMs = _totalMs;

        UpdateDisplay();
        SyncInputs();
    }

    private bool _pendingResume;
    private bool _suppressTime;      

    
    private void TimeSettingChanged()
    {
        if (_suppressTime || _running) return;

        _blink.Stop();
        _finished = false;
        _sw.Reset();
        var h = Math.Max(0, HourBox.SelectedIndex);
        var m = Math.Max(0, MinuteBox.SelectedIndex);
        var s = Math.Max(0, SecondBox.SelectedIndex);
        _totalMs = Math.Max(0, (h * 3600L + m * 60L + s) * 1000);
        _remainMs = _totalMs;
        UpdateDisplay();
        SyncInputs();
    }

    private Brush Res(string key, Windows.UI.Color fallback) => Services.ThemeBrush.Get(this, key);

    
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

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
        => TimerFullscreenWindow.Show(() => _remainMs, () => _totalMs, () => _running);

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

        if (_totalMs <= 0) return;      

        if (_finished || _remainMs <= 0)
        {
            _remainMs = _totalMs;
            _finished = false;
        }

        _endAtMs = _remainMs;
        _blink.Stop();
        Services.TimerAlarm.Stop();     
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
        Services.TimerAlarm.Stop();
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
        
        
        Services.TimerAlarm.Play();
    }

    private void SyncInputs()
    {
        
        HourBox.IsEnabled = !_running;
        MinuteBox.IsEnabled = !_running;
        SecondBox.IsEnabled = !_running;
        StartButton.IsEnabled = _totalMs > 0 || _running;

        Display.Opacity = 1;
        if (_finished) Display.Foreground = Services.ThemeBrush.AccentText(this);   
        else Display.ClearValue(TextBlock.ForegroundProperty);                      
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
