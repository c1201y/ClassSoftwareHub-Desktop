using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace ClassSoftwareHub.Desktop.Pages.Tools;


public sealed partial class TimerToolPage : Page
{
    private enum Mode { Countdown, Stopwatch }

    private const double BarWidth = 520;

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
    private bool _syncingMode;      

    public TimerToolPage()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => SyncInputs();   

        
        
        MinBox.ValueChanged += Duration_Changed;
        SecBox.ValueChanged += Duration_Changed;

        
        var pad = new Windows.Globalization.NumberFormatting.DecimalFormatter
        {
            IntegerDigits = 2,
            FractionDigits = 0,
        };
        MinBox.NumberFormatter = pad;
        SecBox.NumberFormatter = pad;

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => OnTick();

        
        _blink = DispatcherQueue.CreateTimer();
        _blink.Interval = TimeSpan.FromMilliseconds(450);
        _blink.IsRepeating = true;
        _blink.Tick += (_, _) => Display.Opacity = Display.Opacity < 0.9 ? 1 : 0.35;

        
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            _blink.Stop();
            Services.TimerAlarm.Stop();
            Display.Opacity = 1;
        };

        ApplyTime();
        UpdateDisplay();

        
        _syncingTime = true;
        MinBox.Value = 5;
        SecBox.Value = 0;
        _syncingTime = false;
        ApplyTime();

        
        SetMode(Mode.Countdown);

        
        SyncAlarmButton();
        _syncingToggles = true;
        ClockBgSwitch.IsOn = App.Settings.Current.TimerUseClockBackground;
        _syncingToggles = false;
    }

    private bool _syncingTime;       
    private bool _syncingToggles;    

    private Microsoft.UI.Xaml.Media.Brush Res(string key, Windows.UI.Color fallback) => Services.ThemeBrush.Get(this, key);

    
    private void OpenPalette_Click(object sender, RoutedEventArgs e)
        => Views.ToolPaletteWindow.ShowTool("timer");

    

    
    
    
    
    private void Alarm_Click(object sender, RoutedEventArgs e)
    {
        var custom = !string.IsNullOrWhiteSpace(Services.TimerAlarm.CustomPath);
        var flyout = new MenuFlyout();

        var test = new MenuFlyoutItem { Text = "试听当前铃声" };
        test.Click += (_, _) => Services.TimerAlarm.Play();
        flyout.Items.Add(test);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var pick = new MenuFlyoutItem { Text = "选择自定义铃声…" };
        pick.Click += async (_, _) => await PickAlarmAsync();
        flyout.Items.Add(pick);

        var reset = new MenuFlyoutItem { Text = "恢复默认铃声", IsEnabled = custom };
        reset.Click += (_, _) =>
        {
            Services.TimerAlarm.CustomPath = string.Empty;
            SyncAlarmButton();
        };
        flyout.Items.Add(reset);

        flyout.ShowAt(AlarmButton);
    }

    private async Task PickAlarmAsync()
    {
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.MusicLibrary,
            };
            foreach (var ext in Services.TimerAlarm.Extensions) picker.FileTypeFilter.Add(ext);

            
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            Services.TimerAlarm.CustomPath = file.Path;
            SyncAlarmButton();
            Services.TimerAlarm.Play();     
        }
        catch (Exception ex)
        {
            Core.AppLog.Info("timer", "挑选铃声失败：" + ex.Message);
        }
    }

    
    private void SyncAlarmButton()
    {
        var name = Services.TimerAlarm.DisplayName;
        var custom = !string.IsNullOrWhiteSpace(Services.TimerAlarm.CustomPath);

        
        AlarmButton.Content = custom
            ? "铃声：" + (name.Length > 12 ? string.Concat(name.AsSpan(0, 12), "…") : name)
            : "自定义铃声";

        ToolTipService.SetToolTip(AlarmButton, custom
            ? $"当前铃声：{name}\n单击可以换一个，或恢复默认。"
            : "换一个自己的到点铃声（默认用内置的那段）。");
    }

    private void ClockBg_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingToggles) return;
        App.Settings.Current.TimerUseClockBackground = ClockBgSwitch.IsOn;
        App.Settings.Save();
    }

    
    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingMode) return;      
        SetMode(BtnCountdown.IsChecked == true ? Mode.Countdown : Mode.Stopwatch);
    }

    private void SetMode(Mode mode)
    {
        _timer.Stop();
        Services.TimerAlarm.Stop();     
        _running = false;
        _finished = false;
        _mode = mode;
        _remainMs = _totalMs;
        _baseMs = 0;
        _sw.Reset();

        
        _syncingMode = true;
        BtnCountdown.IsChecked = mode == Mode.Countdown;
        BtnStopwatch.IsChecked = mode == Mode.Stopwatch;
        _syncingMode = false;

        
        
        var countdown = mode == Mode.Countdown;
        SetupTitle.Text = countdown ? "设定时长" : "秒表模式";
        SetRow.Visibility = countdown ? Visibility.Visible : Visibility.Collapsed;
        StopwatchHint.Visibility = countdown ? Visibility.Collapsed : Visibility.Visible;

        
        FullscreenButton.Content = countdown ? "全屏倒计时" : "全屏秒表";
        ToolTipService.SetToolTip(FullscreenButton, countdown ? "把倒计时铺满整块屏幕，适合投影" : "把秒表铺满整块屏幕，适合投影或比赛计时");

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
        
        if (_mode == Mode.Countdown && _totalMs <= 0) return;
        if (_mode == Mode.Countdown && _remainMs <= 0) _remainMs = _totalMs;
        if (_mode == Mode.Countdown) _endAtMs = _remainMs;

        _blink.Stop();
        Services.TimerAlarm.Stop();      
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
        Services.TimerAlarm.Stop();
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
        _syncingTime = true;
        MinBox.Value = minutes;
        SecBox.Value = 0;
        _syncingTime = false;
        ApplyTime();
    }

    
    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == Mode.Countdown)
            Views.TimerFullscreenWindow.Show(() => _remainMs, () => _totalMs, () => _running);
        else
            Views.StopwatchFullscreenWindow.Show(() => StopwatchElapsedMs, () => _running);
    }

    
    private void Duration_Changed(object sender, NumberBoxValueChangedEventArgs e)
    {
        if (_syncingTime) return;

        
        if (double.IsNaN(MinBox.Value) || double.IsNaN(SecBox.Value))
        {
            _syncingTime = true;
            if (double.IsNaN(MinBox.Value)) MinBox.Value = 0;
            if (double.IsNaN(SecBox.Value)) SecBox.Value = 0;
            _syncingTime = false;
        }

        ApplyTime();
    }

    private void ApplyTime()
    {
        if (_syncingTime) return;

        var minutes = double.IsNaN(MinBox.Value) ? 0 : MinBox.Value;
        var seconds = double.IsNaN(SecBox.Value) ? 0 : SecBox.Value;

        _totalMs = (long)Math.Max(0, (minutes * 60 + seconds) * 1000);
        
        
        if (!_running)
        {
            _remainMs = _totalMs;
            _finished = false;
            UpdateDisplay();
            SyncInputs();       
        }
    }

    
    private long StopwatchElapsedMs => _baseMs + (_running ? _sw.ElapsedMilliseconds : 0);

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
        
        
        
        Services.TimerAlarm.Play();
    }

    
    private void SyncInputs()
    {
        MinBox.IsEnabled = !_running;
        SecBox.IsEnabled = !_running;
        foreach (var child in PresetRow.Children)
            if (child is Button b) b.IsEnabled = !_running;

        
        if (!_running) StartButton.IsEnabled = !(_mode == Mode.Countdown && _totalMs <= 0);

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
