using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>课堂计时器：倒计时 / 秒表，到点响铃（大字号方便投影）。</summary>
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
    private bool _syncingMode;      // SetMode 回写模式卡时挡一下 Checked 回调

    public TimerToolPage()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => SyncInputs();   // 主题换了重刷大号数字颜色

        // 2026-10-04（Nick）：TimePicker 撤掉 → 分/秒两个 NumberBox。
        // ⚠️ 事件参数类型在 WinUI 3 里名字对不上，统一走一个不带类型的处理函数。
        MinBox.ValueChanged += Duration_Changed;
        SecBox.ValueChanged += Duration_Changed;

        // 输入框显示 "05" 而不是 "5"（跟原来 TimePicker 那两栏的观感对齐）
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
            Services.TimerAlarm.Stop();
            Display.Opacity = 1;
        };

        ApplyTime();
        UpdateDisplay();

        // 初始时长 5 分 00 秒（初值一律在代码里赋，别写在 XAML 上）
        _syncingTime = true;
        MinBox.Value = 5;
        SecBox.Value = 0;
        _syncingTime = false;
        ApplyTime();

        // 初始模式：倒计时（卡片初值一律在代码里赋，不能写在 XAML 上）
        SetMode(Mode.Countdown);

        // 模式那一行右侧那两个控件：铃声按钮文案、时钟背景开关的初值（同样一律在代码里赋）
        SyncAlarmButton();
        _syncingToggles = true;
        ClockBgSwitch.IsOn = App.Settings.Current.TimerUseClockBackground;
        _syncingToggles = false;
    }

    private bool _syncingTime;       // 程序里改 TimePicker 时挡一下事件
    private bool _syncingToggles;    // 程序里回写开关状态时挡一下 Toggled

    private Microsoft.UI.Xaml.Media.Brush Res(string key, Windows.UI.Color fallback) => Services.ThemeBrush.Get(this, key);

    /// <summary>把计时器丢到工具浮窗里跑。</summary>
    private void OpenPalette_Click(object sender, RoutedEventArgs e)
        => Views.ToolPaletteWindow.ShowTool("timer");

    // ══════════ 铃声 / 全屏时钟背景（2026-10-04 Nick 加在模式行右侧的那两个控件）══════════

    /// <summary>
    /// 「自定义铃声」：一律先弹一个小菜单 —— 试听当前铃声 / 换一个 / 恢复默认。
    /// 试听是必须留的：默认时长最短也是 1 分钟，不给试听就得干等一轮才知道响的是什么。
    /// </summary>
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

            // ⚠️ unpackaged 应用必须先把选择器绑到窗口句柄上，否则直接抛异常（同 ClockToolPage）
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            Services.TimerAlarm.CustomPath = file.Path;
            SyncAlarmButton();
            Services.TimerAlarm.Play();     // 挑完立刻响一下试听，省得还要等一轮倒计时才知道好不好听
        }
        catch (Exception ex)
        {
            Core.AppLog.Info("timer", "挑选铃声失败：" + ex.Message);
        }
    }

    /// <summary>把按钮文案与提示刷成"当前用的是哪个铃声"。</summary>
    private void SyncAlarmButton()
    {
        var name = Services.TimerAlarm.DisplayName;
        var custom = !string.IsNullOrWhiteSpace(Services.TimerAlarm.CustomPath);

        // 文件名可能很长：按钮上只留一截，全名放提示里
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

    /// <summary>两张模式卡谁被选中就切到哪个模式（事件挂在 XAML 的 Checked 上）。</summary>
    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingMode) return;      // SetMode 自己在回写卡片状态，别绕回来
        SetMode(BtnCountdown.IsChecked == true ? Mode.Countdown : Mode.Stopwatch);
    }

    private void SetMode(Mode mode)
    {
        _timer.Stop();
        Services.TimerAlarm.Stop();     // 换模式就别让上一轮的铃追着响
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

        // 全屏按钮两种模式都显示，文案与动作跟着模式走
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
        // 0 分 0 秒：没得跑，直接不理（按钮本来就是灰的，这里兜一手键盘回车）
        if (_mode == Mode.Countdown && _totalMs <= 0) return;
        if (_mode == Mode.Countdown && _remainMs <= 0) _remainMs = _totalMs;
        if (_mode == Mode.Countdown) _endAtMs = _remainMs;

        _blink.Stop();
        Services.TimerAlarm.Stop();      // 重新开始 / 继续：把还在响的铃掐掉
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

    /// <summary>全屏投影：倒计时开全屏倒计时、秒表开全屏秒表（都是只读投影，跟着本页一起走）。</summary>
    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == Mode.Countdown)
            Views.TimerFullscreenWindow.Show(() => _remainMs, () => _totalMs, () => _running);
        else
            Views.StopwatchFullscreenWindow.Show(() => StopwatchElapsedMs, () => _running);
    }

    /// <summary>分 / 秒任一格被改动 → 重算总时长（两格共用一个处理函数）。</summary>
    private void Duration_Changed(object sender, NumberBoxValueChangedEventArgs e)
    {
        if (_syncingTime) return;

        // NumberBox 被清空时 Value 是 NaN —— 补成 0，别让它把总时长算成 NaN（那样进度条会消失）
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
        // 0 分 0 秒 ⇒ 就显示 00:00（旧版硬抬成 1 秒，输入是 0 显示却是 00:01，看着像秒没生效）。
        // 这时「开始」会被 SyncInputs 置灰，不会真的跑一个 0 秒倒计时。
        if (!_running)
        {
            _remainMs = _totalMs;
            _finished = false;
            UpdateDisplay();
            SyncInputs();       // 0 分 0 秒 ⇒ 顺手把「开始」置灰／恢复
        }
    }

    /// <summary>秒表已计毫秒（全屏窗口读的就是这个）。</summary>
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
        // 铃声：以前是 kernel32!Beep(880) 响三下（蜂鸣器音色，投影时后排听不见），
        // 2026-10-04 换成真录音 —— 默认是内嵌那段，用户也能在页顶挑自己的文件。
        // 细节见 Services/TimerAlarm.cs。
        Services.TimerAlarm.Play();
    }

    /// <summary>运行时锁定时长设置与预设（对齐网页版），并同步"到点"的配色。</summary>
    private void SyncInputs()
    {
        MinBox.IsEnabled = !_running;
        SecBox.IsEnabled = !_running;
        foreach (var child in PresetRow.Children)
            if (child is Button b) b.IsEnabled = !_running;

        // 0 分 0 秒时没得跑：把「开始」置灰（跑起来之后要留着当「暂停」，所以只在未运行时判）
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
