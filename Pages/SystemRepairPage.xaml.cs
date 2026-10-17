using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 系统修复：7 张预设任务卡，选中一张 → 二次确认 → 提权执行 → 实时输出。
/// 提权与输出落盘的机制见 <see cref="SystemRepair"/> 的类注释。
/// </summary>
public sealed partial class SystemRepairPage : Page
{
    /// <summary>输出保留上限。超了从头砍 —— sfc / DISM 的输出能到几百 KB，不能无限堆。</summary>
    private const int MaxOutputChars = 200 * 1024;

    /// <summary>助手进程刚起来那两秒还没有心跳，别拿 IsAlive 判死（见 Poll）。</summary>
    private static readonly TimeSpan AliveGrace = TimeSpan.FromSeconds(15);

    private readonly StringBuilder _output = new();

    private DispatcherQueueTimer? _timer;
    private RepairSession? _session;
    private bool _stopRequested;
    private bool _dialogOpen;
    private string _statusLine = "";

    public SystemRepairPage()
    {
        InitializeComponent();

        // 两列布局（照首页「快速开始」）：偶数序号进左列、奇数进右列
        CardColumnLeft.ItemsSource = SystemRepair.Tasks.Where((_, i) => i % 2 == 0).ToList();
        CardColumnRight.ItemsSource = SystemRepair.Tasks.Where((_, i) => i % 2 == 1).ToList();

        CountLine.Text = $"共 {SystemRepair.Tasks.Count} 项任务　·　全部需要管理员授权　·　执行前会再确认一次";

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_session is not null) return;

        SystemRepair.CleanupOldRuns();

        // 上一轮页面被切走时任务还在跑 —— 回来就把它接上（不然用户会以为任务没了）
        var unfinished = SystemRepair.FindUnfinished();
        if (unfinished is null) return;

        AppLog.Info("repair", $"接上一次未结束的任务：{unfinished.Task.Name}");
        EnterRun(unfinished);
    }

    /// <summary>
    /// ⛔ 离开页面**不中止**任务：sfc / DISM 中途打断是比"多等一会儿"糟得多的结果。
    /// 这里只是停掉轮询；任务继续跑，回来时由 <c>OnLoaded</c> 接上。
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        StopTimer();
    }

    // ── 选卡 → 确认 → 起任务 ──────────────────────────────────────────

    private async void Card_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_session is not null || _dialogOpen) return;
        if ((sender as FrameworkElement)?.DataContext is not RepairTask task) return;

        if (!await ConfirmAsync(task)) return;

        var session = await SystemRepair.StartAsync(task, CancellationToken.None);
        if (session is null)
        {
            // ⚠️ StartAsync 返回 null ⇒ 进程根本没起来，所以"没有任何改动"是成立的
            await ShowMessageAsync("任务未开始",
                "已取消管理员授权，或提权进程未能启动。没有对系统做任何改动。");
            return;
        }

        EnterRun(session);
    }

    /// <summary>
    /// ⛔⛔ 这道确认框是**唯一的安全闸**（Nick 2026-10-16 定：不建还原点，改成执行前二次确认）。
    /// 所以它不能只问一句"确定吗" —— 具体命令、会发生什么、耗时、要不要授权全摆在同一屏，
    /// 高风险任务还把默认焦点放在「取消」上，免得顺手一个回车就跑起来。
    /// </summary>
    private async Task<bool> ConfirmAsync(RepairTask task)
    {
        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(Section("将要执行的命令", task.Command, mono: true));
        panel.Children.Add(Section("会发生什么", task.Effect));

        if (task.Caution.Length > 0)
        {
            panel.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Warning,
                IsOpen = true,
                IsClosable = false,
                Title = "特别注意",
                Message = task.Caution,
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = $"预计耗时{task.TimeHint}　·　{task.RiskText}　·　执行时会弹出一次管理员授权",
            FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "中途可以随时中止；中止只是停下命令，不保证把已经做出的改动回滚。",
            FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap,
        });

        var high = task.Risk == RepairRisk.High;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = task.Name,
            Content = new ScrollViewer { Content = panel, MaxHeight = 420 },
            PrimaryButtonText = high ? $"确认{task.Name}" : "确认执行",
            CloseButtonText = "取消",
            // 高风险任务的默认按钮是「取消」—— 回车不会误触发
            DefaultButton = high ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };

        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    private static UIElement Section(string title, string body, bool mono = false)
    {
        var block = new StackPanel { Spacing = 4 };
        block.Children.Add(new TextBlock { Text = title, FontSize = 13 });
        block.Children.Add(new TextBlock
        {
            Text = body,
            FontSize = mono ? 12 : 12.5,
            FontFamily = mono ? new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
                              : new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI Variable Text"),
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        return block;
    }

    // ── 运行中的界面切换 ──────────────────────────────────────────────

    private void EnterRun(RepairSession session)
    {
        _session = session;
        _stopRequested = false;
        _statusLine = "";
        _output.Clear();

        RunIcon.Glyph = session.Task.Glyph;
        RunTitle.Text = session.Task.Name;
        RunCommand.Text = session.Task.Command;
        OutputBox.Text = "";

        ResultBar.IsOpen = false;
        RunProgress.Value = 0;
        RunProgress.IsIndeterminate = true;

        StopButton.IsEnabled = true;
        StopButton.Content = "中止任务";
        BackButton.IsEnabled = false;

        CardArea.Visibility = Visibility.Collapsed;
        RunArea.Visibility = Visibility.Visible;

        UpdateStatus(session, DateTime.Now - session.StartedAt);

        _timer ??= CreateTimer();
        _timer.Start();
    }

    private DispatcherQueueTimer CreateTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(300);
        timer.Tick += (_, _) => Poll();
        return timer;
    }

    private void StopTimer()
    {
        _timer?.Stop();
    }

    private void Poll()
    {
        var session = _session;
        if (session is null)
        {
            StopTimer();
            return;
        }

        var chunk = session.ReadNewOutput();
        if (chunk.Length > 0) AppendOutput(chunk);

        var percent = session.ReadPercent();
        if (percent >= 0)
        {
            if (RunProgress.IsIndeterminate) RunProgress.IsIndeterminate = false;
            RunProgress.Value = percent;
        }

        UpdateStatus(session, DateTime.Now - session.StartedAt);

        var outcome = session.ReadOutcome();
        if (outcome is not null)
        {
            Finish(session, outcome);
            return;
        }

        // 请求中止之后助手没留下结局就消失了 ⇒ 按"已中止"收场，别让界面永远停在"正在中止…"。
        // ⚠️ 必须给一段宽限期：助手刚起来的那几秒还没写过心跳，IsAlive 会是 false。
        if (_stopRequested && DateTime.Now - session.StartedAt > AliveGrace && !session.IsAlive)
        {
            Finish(session, new RepairOutcome(true, -1, "已中止"));
        }
    }

    private void AppendOutput(string chunk)
    {
        _output.Append(chunk);

        if (_output.Length > MaxOutputChars)
        {
            var text = _output.ToString();
            var keepFrom = text.Length - MaxOutputChars;
            var cut = text.IndexOf('\n', keepFrom);
            _output.Clear();
            _output.Append(cut >= 0
                ? "……（前段输出已省略）\n" + text[(cut + 1)..]
                : text[keepFrom..]);
        }

        OutputBox.Text = _output.ToString();

        // 给只读 TextBox 设光标位置会把那一行带进视野 —— 这就是"自动滚到底"
        OutputBox.SelectionStart = OutputBox.Text.Length;
        OutputBox.SelectionLength = 0;
    }

    private void UpdateStatus(RepairSession session, TimeSpan elapsed)
    {
        var line = _stopRequested
            ? $"正在中止…　·　已运行 {Format(elapsed)}"
            : $"正在执行　·　已运行 {Format(elapsed)}";

        if (line == _statusLine) return;

        _statusLine = line;
        RunStatus.Text = line;
    }

    private static string Format(TimeSpan span)
        => span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";

    private void Finish(RepairSession session, RepairOutcome outcome)
    {
        StopTimer();

        StopButton.IsEnabled = false;
        BackButton.IsEnabled = true;

        var elapsed = DateTime.Now - session.StartedAt;
        RunProgress.IsIndeterminate = false;
        if (!outcome.Cancelled) RunProgress.Value = 100;

        RunStatus.Text = outcome.Cancelled
            ? $"已中止　·　共运行 {Format(elapsed)}"
            : $"已完成　·　用时 {Format(elapsed)}";

        if (outcome.Cancelled)
        {
            ResultBar.Severity = InfoBarSeverity.Warning;
            ResultBar.Title = "任务已中止";
            ResultBar.Message = "命令在中途被结束，系统可能停在一个中间状态。"
                                + "如果不确定，把同一个任务重新执行一次即可。";
        }
        else if (outcome.ExitCode == 0)
        {
            ResultBar.Severity = InfoBarSeverity.Success;
            ResultBar.Title = "命令正常结束";
            ResultBar.Message = "返回代码 0，过程中没有报错。具体结论见上面的输出。";
        }
        else
        {
            ResultBar.Severity = InfoBarSeverity.Error;
            ResultBar.Title = $"命令返回代码 {outcome.ExitCode}";
            ResultBar.Message = "过程中出现了错误，具体信息见上面的输出。"
                                + "反复失败可在「反馈中心」提交，把上面的输出一并附上。";
        }

        ResultBar.IsOpen = true;
        AppLog.Info("repair",
            $"「{session.Task.Name}」收尾：中止={outcome.Cancelled} 退出码={outcome.ExitCode} 用时={Format(elapsed)}");
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null || _stopRequested) return;

        _stopRequested = true;
        _statusLine = "";
        StopButton.IsEnabled = false;
        RunStatus.Text = "正在中止…";

        // ⛔ 这里只能写哨兵：普通权限杀不掉提权进程，真正杀子进程树的是助手那边
        session.RequestCancel();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        StopTimer();
        _session = null;
        _stopRequested = false;
        _statusLine = "";

        RunArea.Visibility = Visibility.Collapsed;
        CardArea.Visibility = Visibility.Visible;
    }

    // ── 卡片悬停反馈（容器 chrome 已关掉，见 CshNavCardStyle） ──────────

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Opacity = 0.88;
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Opacity = 1.0;
    }

    // ── 说明 ──────────────────────────────────────────────────────────

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 14 };

        void Item(string title, string body)
        {
            var block = new StackPanel { Spacing = 4 };
            block.Children.Add(new TextBlock { Text = title, FontSize = 13 });
            block.Children.Add(new TextBlock
            {
                Text = body, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(block);
        }

        Item("这些任务都是 Windows 自带的命令",
             "sfc、DISM、chkdsk、netsh 都是系统自带的组件，没有引入任何第三方工具。"
             + "确认框里会把即将执行的命令原样列出来，执行过程也全程可见。");

        Item("为什么只能选，不能自己敲命令",
             "这一页的定位是「系统出毛病了修一下」，不是「给一个终端」。"
             + "放开命令输入等于把判断风险的责任整个推给使用者，所以刻意没有做输入框。");

        Item("为什么全部都要管理员授权",
             "要改的是系统文件、组件存储、网络协议栈和系统服务，普通权限改不动。"
             + "每条任务按下之后会弹一次系统授权确认，取消就不会开始。");

        Item("确认框为什么写得那么细",
             "这一页不做系统还原点 —— 所以在正式执行前，会把「将要执行的命令」「会发生什么」"
             + "「耗时」「风险等级」全部摆出来让你再确认一次。这一道确认就是这里唯一的安全闸。");

        Item("中止不等于回滚",
             "运行中的「中止任务」只是把命令停下来，不会把已经做出的改动恢复回去。"
             + "停止之后系统可能停在一个中间状态，重新执行一次同一任务比停在中间更稳妥。");

        Item("离开这一页不会中止任务",
             "切到别的页面之后任务会继续跑完，回到这一页会自动接上下面的进度和输出。"
             + "之所以不让它跟着页面一起停：sfc 和 DISM 中途被打断是更糟的结果。");

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "系统修复：这些任务是怎么定的",
            Content = new ScrollViewer { Content = panel, MaxHeight = 460 },
            CloseButtonText = "关闭",
        };

        await ShowDialogAsync(dialog);
    }

    // ── 弹窗（ContentDialog 同时只能开一个，统一走这两个入口） ──────────

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (_dialogOpen) return ContentDialogResult.None;

        _dialogOpen = true;
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            AppLog.Info("repair", $"弹窗没弹出来：{ex.GetType().Name}");
            return ContentDialogResult.None;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = message, FontSize = 13, TextWrapping = TextWrapping.Wrap,
            },
            CloseButtonText = "关闭",
        };

        return ShowDialogAsync(dialog);
    }
}
