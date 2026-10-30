using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Services.Diagnostics;
using ClassSoftwareHub.Desktop.Services.HubAi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace ClassSoftwareHub.Desktop.Controls;

/// <summary>
/// Hub AI 提问浮层（2026-10-30 从软件下载页抽成公共控件，需求 1:23）。
///
/// <para>
/// 用法：页面根 Grid 里放一行 <c>&lt;ui:HubAiFlyout x:Name="AiFlyout" /&gt;</c>，
/// 页头放一个按钮，点击调 <see cref="Toggle"/>。控件自己负责位置（右下角）、动画和对话。
/// </para>
///
/// <para>
/// ⚠️⚠️ 2026-10-30 改了用途（Nick 提）：「每次都要跳到 Hub AI 页，能不能直接在这里回」。
/// 原来的浮层只收集问题再跳页面（2026-10-18 的决定）；现在**在浮层里直接回答**，
/// 对话跑的是同一套 <see cref="AiChatService"/>（服务层一份逻辑，界面两套壳）。
/// 关闭浮层不会丢对话，再打开可以接着问；「↗」仍然提供去完整页的出口。
/// </para>
/// </summary>
public sealed partial class HubAiFlyout : UserControl
{
    /// <summary>浮层自己的一份对话上下文（不落盘、不进会话存档）。</summary>
    private readonly AiChatService _chat = new();

    private CancellationTokenSource? _cts;

    private bool _busy;

    /// <summary>正在追加文本的那个助手气泡（工具调用换气泡时指向新的）。</summary>
    private TextBlock? _answer;

    /// <summary>增量文本的缓冲：模型逐字返回，直接往 UI 塞会把排版线程打满。</summary>
    private readonly StringBuilder _pending = new();
    private readonly object _pendingGate = new();
    private bool _flushQueued;

    public HubAiFlyout()
    {
        InitializeComponent();
    }

    /// <summary>浮层当前是否展开（页面想在切走时收起，可以问它）。</summary>
    public bool IsOpen => Card.Visibility == Visibility.Visible;

    /// <summary>展开，并把焦点给输入框 —— 用户点进来的目的就是打字。</summary>
    public void Show()
    {
        if (IsOpen)
        {
            Input.Focus(FocusState.Programmatic);
            return;
        }

        Card.Visibility = Visibility.Visible;

        // ⚠️ 必须先摆到起点：DoubleAnimation 只给 To 时会从**当前值**动画过去，
        //    不设起点就是 0→0，只剩淡入、看不出滑动。
        CardTransform.Y = 24;
        Card.Opacity = 0;

        Animate(toY: 0, toOpacity: 1, durationMs: 240);
        Input.Focus(FocusState.Programmatic);
    }

    /// <summary>收起。动画结束后才真正 Collapsed（过早设会让动画直接消失）。</summary>
    public void Hide()
    {
        if (!IsOpen) return;

        Animate(toY: 24, toOpacity: 0, durationMs: 170,
            onDone: () => Card.Visibility = Visibility.Collapsed);
    }

    /// <summary>展开 / 收起切换（按钮点一下用这个）。</summary>
    public void Toggle()
    {
        if (IsOpen) Hide();
        else Show();
    }

    // ── 按钮 ─────────────────────────────────────────────────────────────

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>升到完整页（那一页有历史、工具轨迹、推荐卡与用量统计）。</summary>
    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        App.MainWindow?.Shell.NavigateTo("hubai");
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        // 生成中这颗按钮就是「停止」
        if (_busy)
        {
            Stop();
            return;
        }

        _ = AskAsync();
    }

    private void Input_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;

        if (_busy) return;
        _ = AskAsync();
    }

    private void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        SetStatus("已停止");
    }

    // ── 一轮问答 ─────────────────────────────────────────────────────────

    private async Task AskAsync()
    {
        if (_busy) return;

        var text = Input.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        // 配置先查：没模型 / 配置不全时给一条明路，别让用户盯着转圈
        var model = AiModelStore.Active;
        if (model is null)
        {
            AddBubble("还没有添加模型。请到「设置 → Hub AI → 模型服务」添加一个。", mine: false);
            return;
        }

        var cfg = model.ToConfig(SecretStore.Load(model.EffectiveSecretId));
        if (!cfg.IsUsable)
        {
            AddBubble("当前模型的配置不完整。请到「设置 → Hub AI → 模型服务」填写服务地址与模型名称。", mine: false);
            return;
        }

        Input.Text = string.Empty;
        Intro.Visibility = Visibility.Collapsed;

        AddBubble(text, mine: true);
        _answer = AddBubble(string.Empty, mine: false);

        _busy = true;
        SetBusyUi(true);
        SetStatus("正在读取本机情况…");

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            // 每次提问现采一次：缓存下来的机器状态隔一会就不准了，
            // 而这一步只有几百毫秒（页面里也是这么做的）
            var snapshot = await MachineProbe.CollectAsync();
            token.ThrowIfCancellationRequested();

            SetStatus("正在思考…");

            await _chat.RunAsync(
                cfg,
                snapshot,
                ActiveTools.Load(),
                text,
                onDelta: AppendDelta,
                onStatus: status => Dispatch(() => SetStatus(status)),
                onNewBubble: () => Dispatch(() => _answer = AddBubble(string.Empty, mine: false)),
                ct: token);

            FlushDelta();
            SetStatus(string.Empty);

            // 工具调用之后可能一句话都没有 —— 留个空气泡比留一片空白好懂
            if (_answer is { Text.Length: 0 })
                _answer.Text = "（这一轮没有文字回答，可以换个问法或到完整页看工具轨迹。）";
        }
        catch (OperationCanceledException)
        {
            FlushDelta();
            SetStatus("已停止");
        }
        catch (Exception ex)
        {
            FlushDelta();
            AppLog.Warn("hubai", "浮层对话失败：" + ex.Message);
            AddBubble("出错了：" + ex.Message, mine: false);
            SetStatus(string.Empty);
        }
        finally
        {
            _busy = false;
            SetBusyUi(false);
            _answer = null;

            try { _cts?.Dispose(); } catch { }
            _cts = null;
        }
    }

    // ── 增量文本 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 模型每吐几个字就回调一次。这里只往缓冲区里塞，
    /// 由一次延迟的 UI 刷新统一写进去 —— 逐字改 TextBlock 会把排版线程打满。
    /// </summary>
    private void AppendDelta(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        lock (_pendingGate) _pending.Append(text);

        if (_flushQueued) return;
        _flushQueued = true;

        Dispatch(FlushDelta);
    }

    /// <summary>把缓冲区里的字写进当前气泡并滚到底。</summary>
    private void FlushDelta()
    {
        string chunk;
        lock (_pendingGate)
        {
            if (_pending.Length == 0) return;
            chunk = _pending.ToString();
            _pending.Clear();
        }
        _flushQueued = false;

        if (_answer is null) return;

        _answer.Text += chunk;
        ScrollToEnd();
    }

    // ── 界面小工具 ───────────────────────────────────────────────────────

    /// <summary>加一条气泡。<paramref name="mine"/> = 用户自己说的（右对齐）。</summary>
    private TextBlock AddBubble(string text, bool mine)
    {
        var line = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = mine
                ? ThemeBrush.Get(this, "TextOnAccentFillColorPrimaryBrush")
                : ThemeBrush.Get(this, "TextFillColorPrimaryBrush"),
        };

        var bubble = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9),
            MaxWidth = 310,
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Background = mine
                ? ThemeBrush.Get(this, "AccentFillColorDefaultBrush")
                : ThemeBrush.Get(this, "SubtleFillColorSecondaryBrush"),
            Child = line,
        };

        Messages.Children.Add(bubble);
        ScrollToEnd();
        return line;
    }

    private void ScrollToEnd()
    {
        try { MessagesScroll.ChangeView(null, MessagesScroll.ScrollableHeight, null, disableAnimation: true); }
        catch { }
    }

    private void SetStatus(string text)
    {
        StatusLine.Text = text ?? string.Empty;
        StatusLine.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>生成中：发送键变「停止」，输入框仍可打字（方便先想下一句）。</summary>
    private void SetBusyUi(bool busy)
    {
        SendGlyph.Glyph = busy ? "\uE71A" : "\uE724";       // ■ 停止 / ➤ 发送
        ToolTipService.SetToolTip(SendButton, busy ? "停止生成" : "发送");
    }

    /// <summary>切回 UI 线程执行（增量回调来自后台线程）。</summary>
    private void Dispatch(Action action)
    {
        var queue = DispatcherQueue;
        if (queue is null) return;

        if (queue.HasThreadAccess) action();
        else queue.TryEnqueue(() => action());
    }

    // ── 动画 ─────────────────────────────────────────────────────────────

    /// <summary>滑入滑出。⚠️ 位移与透明度都是**非合成动画**，必须开 <c>EnableDependentAnimation</c> 才会动。</summary>
    private void Animate(double toY, double toOpacity, int durationMs, Action? onDone = null)
    {
        var slide = new DoubleAnimation
        {
            To = toY,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            EasingFunction = new CubicEase
            {
                EasingMode = toOpacity > 0.5 ? EasingMode.EaseOut : EasingMode.EaseIn,
            },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(slide, CardTransform);
        Storyboard.SetTargetProperty(slide, "Y");

        var fade = new DoubleAnimation
        {
            To = toOpacity,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, Card);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);

        if (onDone is not null) storyboard.Completed += (_, _) => onDone();

        storyboard.Begin();
    }
}
