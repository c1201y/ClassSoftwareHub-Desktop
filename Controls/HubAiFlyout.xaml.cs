using System;
using System.Linq;
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
/// Hub AI 提问浮层（2026-10-30）。
///
/// <para>
/// 用法：页面根 Grid 里放一行 <c>&lt;ui:HubAiFlyout x:Name="AiFlyout" /&gt;</c>，
/// 页头放一个按钮，点击调 <see cref="Toggle"/>。
/// </para>
///
/// <para>
/// ⛔⛔ 这里**不是**另一套对话（Nick 2026-10-30 定：「本质上就是在 HubAI 界面跑，
/// 只不过把输出结果搬到这个窗口」）：
/// <list type="number">
///   <item>跑的是真正的 <see cref="HubAiRuntime"/>，与 Hub AI 页同一套上下文、
///         同一套工具、同一套存档格式，不是"另说一套"；</item>
///   <item>问答**写进会话存档**（<see cref="ChatSessionStore.SaveBranch"/>），
///         跳到 Hub AI 页能翻到，思考过程、工具轨迹都在那儿；</item>
///   <item>浮层只显示**最终正文**，思考过程与工具轨迹留在完整页（这里窄，塞不下也没必要）。</item>
/// </list>
/// </para>
///
/// <para>
/// ⭐ 2026-10-30 二改：**每次展开都另起一段对话**（Nick：「新开一个窗口，相当于新开一个对话，
/// 不是在旧的对话上面接下去」）。所以：
/// <list type="bullet">
///   <item>展开时拿一份新的草稿运行时（<see cref="HubAiRuntime.NewDraft"/>），
///         不带上一次的消息，也不去改 Hub AI 页正在看的那条会话；</item>
///   <item>这一次展开里的追问仍在同一条上接（<c>_sessionId</c> 收尾时记下来），收起再打开就是新的；</item>
///   <item>每一段都单独存档，历史列表里一条条列得出来 —— 这就是"可溯源"的部分。</item>
/// </list>
/// </para>
///
/// <para>
/// 正文走 <see cref="MarkdownText"/>，所以表格、列表、加粗、代码块在浮层里也能正常显示。
/// </para>
/// </summary>
public sealed partial class HubAiFlyout : UserControl
{
    private HubAiRuntime? _runtime;

    /// <summary>
    /// 本次展开这段对话的会话 id（null = 还没存档）。
    /// ⚠️ 每次展开重置 —— 收起再打开就是新的一条，不再挂在上一条后面。
    /// </summary>
    private string? _sessionId;

    /// <summary>正在追加的那条助手气泡的容器（Markdown 要往 StackPanel 里渲染）。</summary>
    private StackPanel? _answerPanel;

    /// <summary>已经渲染出来的正文，用来跳过重复刷新。</summary>
    private string _answerRendered = "";

    private bool _busy;

    /// <summary>
    /// 当前页面的上下文（一句话），由宿主页面在数据就绪后设置。
    /// <para>
    /// 2026-10-30 Nick 要的「每个页面有对应的角色」：在软件下载页问，它应该知道页上有什么清单，
    /// 而不是再去全盘扫一遍。这里只放**摘要**（页名 + 关键几条），别塞整份数据 ——
    /// 那会把上下文顶爆，也会让每轮回复都变慢。
    /// </para>
    /// <para>
    /// 只在本次展开后的**第一条**提问里带一次：后续追问已经在同一段对话里，重复带是噪音。
    /// </para>
    /// </summary>
    public string? PageContext { get; set; }

    /// <summary>本次展开是否已经把页面上下文发出去过。</summary>
    private bool _contextAttached;

    public HubAiFlyout()
    {
        InitializeComponent();

        // 深色下换成原生底色 + 主题色描边（浅色那套 Nick 说好看，别动）
        ApplySurface();
        ActualThemeChanged += (_, _) => ApplySurface();
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

        BeginConversation();

        Card.Visibility = Visibility.Visible;

        // ⚠️ 必须先摆到起点：DoubleAnimation 只给 To 时会从**当前值**动画过去，
        //    不设起点就是 0→0，只剩淡入、看不出滑动。
        // 起点：略小一点、略低一点。动画收回到原位 —— 比原来"整块往上飘 24px"克制得多
        CardTransform.ScaleX = 0.96;
        CardTransform.ScaleY = 0.96;
        CardTransform.TranslateY = 8;
        Card.Opacity = 0;

        PlayAppear(show: true);
        Input.Focus(FocusState.Programmatic);
    }

    /// <summary>收起。动画结束后才真正 Collapsed（过早设会让动画直接消失）。</summary>
    public void Hide()
    {
        if (!IsOpen) return;

        PlayAppear(show: false, onDone: () => Card.Visibility = Visibility.Collapsed);
    }

    /// <summary>展开 / 收起切换（按钮点一下用这个）。</summary>
    public void Toggle()
    {
        if (IsOpen) Hide();
        else Show();
    }

    // ── 按钮 ─────────────────────────────────────────────────────────────

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>
    /// 升到完整页：那里能看到这次对话的思考过程、工具轨迹、推荐卡与用量。
    /// 页面会主动切到这条会话（见 <c>HubAiPage.PendingReloadId</c>）。
    /// </summary>
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
        try { _runtime?.Stop(); } catch { }
    }

    // ── 运行时 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 开一段新对话：新的草稿运行时 + 清空气泡与上下文标记。
    /// <para>
    /// ⚠️ 上一段还在生成就先停掉 —— 取消也会走收尾存档（<see cref="Finish"/>），
    /// 已经写出来的部分不会白跑，只是不在这个窗口里显示了。
    /// </para>
    /// </summary>
    private void BeginConversation()
    {
        if (_runtime is not null)
        {
            if (_runtime.IsRunning) _runtime.Stop();
            _runtime.Changed -= OnRuntimeChanged;
        }

        _runtime = HubAiRuntime.NewDraft();
        _runtime.Changed += OnRuntimeChanged;

        _sessionId = null;
        _contextAttached = false;
        _answerPanel = null;
        _answerRendered = "";

        Messages.Children.Clear();
        Intro.Visibility = Visibility.Visible;

        _busy = false;
        SetBusyUi(false);
    }

    /// <summary>运行时内容变了（增量正文）→ 刷新当前气泡。</summary>
    private void OnRuntimeChanged() => Dispatch(RenderLive);

    /// <summary>
    /// 把运行时的正文画进气泡。
    /// <para>
    /// ⚠️ 走运行时而不是自己收增量：这样浮层与页面永远是同一份内容，
    /// 也才能做到"页面那边跑、浮层这边看得到"。
    /// </para>
    /// </summary>
    private void RenderLive()
    {
        if (_runtime is null || _answerPanel is null) return;

        var body = _runtime.LiveBody;
        if (body == _answerRendered) return;

        _answerRendered = body;
        MarkdownText.Apply(_answerPanel, body, ThemeBrush.Get(this, "TextFillColorPrimaryBrush"),
            fontSize: 12.5, lineHeight: 20);
        ScrollToEnd();
    }

    // ── 一轮问答 ─────────────────────────────────────────────────────────

    private async Task AskAsync()
    {
        if (_busy) return;

        var text = Input.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        // 配置先查：没模型 / 配置不全时给一条明路，别让用户盯着空白
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

        // 理论上 Show() 里已经建好了；万一宿主直接调 Ask（比如将来加了快捷键），补一份
        if (_runtime is null)
        {
            _runtime = HubAiRuntime.NewDraft();
            _runtime.Changed += OnRuntimeChanged;
        }

        var runtime = _runtime;
        if (runtime.IsRunning)
        {
            AddBubble("上一条还在回答，等它说完再问。", mine: false);
            return;
        }

        // 会话 id：这一段对话自己的（null = 第一次提问，收尾时才会生成存档 id）
        var sessionId = _sessionId;

        // 这一轮新增的消息从哪个下标开始 —— 收尾存档要用
        var before = runtime.Chat.Snapshot().Count;

        // 第一条提问带上"我在哪个页面" —— 各页的浮层就是那一页的专用助手（2026-10-30）
        var payload = text;
        if (!_contextAttached && !string.IsNullOrWhiteSpace(PageContext))
        {
            payload = $"（我现在在{PageContext}）\n\n{text}";
            _contextAttached = true;
        }

        Input.Text = string.Empty;
        Intro.Visibility = Visibility.Collapsed;

        AddBubble(text, mine: true);
        _answerPanel = AddBubble(string.Empty, mine: false);
        _answerRendered = "";

        _busy = true;
        SetBusyUi(true);

        try
        {
            // 每次现采一次本机概况：缓存下来的隔一会就不准，这一步也只要几百毫秒
            var snapshot = await MachineProbe.CollectAsync();

            runtime.Start(
                cfg,
                snapshot,
                ActiveTools.Load(),
                payload,
                onCompleted: usage => Dispatch(() => Finish(runtime, sessionId, before, usage, error: null)),
                onFailed: ex => Dispatch(() => Finish(runtime, sessionId, before, AiUsage.Empty, error: ex)));
        }
        catch (Exception ex)
        {
            AppLog.Warn("hubai", "浮层提问失败：" + ex.Message);
            AddBubble("出错了：" + ex.Message, mine: false);
            _busy = false;
            SetBusyUi(false);
        }
    }

    /// <summary>
    /// 一轮收尾：存档 + 复位界面。
    /// <para>
    /// ⚠️ 存档逻辑与 <c>HubAiPage.PersistSession</c> 是同一套判据：
    /// 只存「有正文的助手回合」，否则历史里会留下"只有思考、正文空白"的完成气泡。
    /// </para>
    /// </summary>
    private void Finish(HubAiRuntime runtime, string? sessionId, int before, AiUsage usage, Exception? error)
    {
        // ⚠️ 这段可能在浮层已经收起、甚至已经另起一段对话之后才回来（取消上一段就是要走这条路）。
        //    界面状态只归"当前这一段"管，存档照旧 —— 否则旧回合会把新对话的气泡/忙碌态搅乱。
        var current = ReferenceEquals(runtime, _runtime);

        if (current)
        {
            _busy = false;
            SetBusyUi(false);
        }

        if (error is not null)
        {
            AppLog.Warn("hubai", "浮层对话失败：" + error.Message);
            if (current) AddBubble("出错了：" + error.Message, mine: false);
            return;
        }

        try
        {
            var turns = runtime.Chat.Snapshot().Skip(before).ToList();
            if (turns.Count == 0) return;

            if (!turns.Any(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content)))
            {
                // 一句正文都没回来（只出了思考 / 工具调用）：给个提示，别留空气泡
                if (current && _answerPanel is { Children.Count: 0 })
                    AddBubble("这一轮没有文字回答，可以换个问法。", mine: false);
                return;
            }

            var title = turns.FirstOrDefault(m => m.Role == "user")?.Content ?? "未命名对话";

            var savedId = ChatSessionStore.SaveBranch(
                sessionId,
                parentId: null,
                turns,
                title,
                App.Settings.Current.HubAiModel,
                usage.TotalTokens);

            // 新对话第一次存档才拿到 id —— 运行时要从草稿键改挂到真实 id 上，
            // 这样跟到 Hub AI 页时是同一条（上下文接着聊，不是重新 Restore 一份）
            if (string.IsNullOrEmpty(sessionId) && !string.IsNullOrEmpty(savedId))
                HubAiRuntime.Adopt(runtime.SessionId, savedId);

            if (savedId is { Length: > 0 })
            {
                // 这一段往后就挂在保存下来的 id 上继续追加
                if (current) _sessionId = savedId;

                App.Settings.Current.HubAiLastSessionId = savedId;
                App.Settings.Save();

                // 让 Hub AI 页跳过来时直接切到这条会话（思考过程、工具轨迹都在那儿）
                Pages.HubAiPage.PendingReloadId = savedId;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("hubai", "浮层存档失败：" + ex.Message);
        }
    }

    // ── 界面 ─────────────────────────────────────────────────────────────

    /// <summary>加一条气泡。<paramref name="mine"/> = 用户自己说的（右对齐）。</summary>
    private StackPanel AddBubble(string text, bool mine)
    {
        var body = new StackPanel { Spacing = 6 };
        var foreground = mine
            ? ThemeBrush.Get(this, "TextOnAccentFillColorPrimaryBrush")
            : ThemeBrush.Get(this, "TextFillColorPrimaryBrush");

        if (text.Length > 0)
            MarkdownText.Apply(body, text, foreground, fontSize: 12.5, lineHeight: 20);

        var bubble = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9),
            MaxWidth = 310,
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Background = mine
                ? ThemeBrush.Get(this, "AccentFillColorDefaultBrush")
                : ThemeBrush.Get(this, "SubtleFillColorSecondaryBrush"),
            Child = body,
        };

        Messages.Children.Add(bubble);
        ScrollToEnd();
        return body;
    }

    private void ScrollToEnd()
    {
        try { MessagesScroll.ChangeView(null, MessagesScroll.ScrollableHeight, null, disableAnimation: true); }
        catch { }
    }

    /// <summary>生成中：发送键变「停止」。</summary>
    private void SetBusyUi(bool busy)
    {
        SendGlyph.Glyph = busy ? "\uE71A" : "\uE724";       // ■ 停止 / ➤ 发送
        ToolTipService.SetToolTip(SendButton, busy ? "停止生成" : "发送");
    }

    /// <summary>切回 UI 线程执行（运行时回调来自后台线程）。</summary>
    private void Dispatch(Action action)
    {
        var queue = DispatcherQueue;
        if (queue is null) return;

        if (queue.HasThreadAccess) action();
        else queue.TryEnqueue(() => action());
    }

    // ── 动画 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 出场 / 收起。
    /// <para>
    /// 2026-10-30 换掉：原来是从下往上飘 24px + 淡入，Nick 说"淡入上浮有点太丑"。
    /// 现在是**轻微放大（0.96→1）+ 8px 归位 + 淡入**，200ms、EaseOut —— 系统 Flyout 就是这种收放感。
    /// 收起 130ms、EaseIn，快一点，不拖泥带水。
    /// </para>
    /// ⚠️ 位移/缩放/透明度全是**非合成动画**，必须开 <c>EnableDependentAnimation</c> 才会动。
    /// </summary>
    private void PlayAppear(bool show, Action? onDone = null)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(show ? 200 : 130));
        var ease = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn };

        var storyboard = new Storyboard();

        void Add(DependencyObject target, string property, double to)
        {
            var animation = new DoubleAnimation
            {
                To = to,
                Duration = duration,
                EasingFunction = ease,
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }

        Add(Card, "Opacity", show ? 1 : 0);
        Add(CardTransform, "ScaleX", show ? 1 : 0.98);
        Add(CardTransform, "ScaleY", show ? 1 : 0.98);
        Add(CardTransform, "TranslateY", show ? 0 : 6);

        if (onDone is not null) storyboard.Completed += (_, _) => onDone();
        storyboard.Begin();
    }

    /// <summary>
    /// 深色下的表面处理（2026-10-30 Nick）：
    /// 背景用 WinUI 原生窗口底色，外面描一圈**主题色**边框。
    /// ⚠️ 浅色**不动** —— 他那句原话是"浅色模式的那个界面很好看"，别顺手也改了。
    /// </summary>
    private void ApplySurface()
    {
        try
        {
            if (ActualTheme == ElementTheme.Dark)
            {
                Card.Background = ThemeBrush.Get(this, "SolidBackgroundFillColorBaseBrush");
                Card.BorderBrush = ThemeBrush.Get(this, "AccentFillColorDefaultBrush");
            }
            else
            {
                // 交回 XAML 里那套 ThemeResource（浅色 / 高对比各自有值）
                Card.ClearValue(Border.BackgroundProperty);
                Card.ClearValue(Border.BorderBrushProperty);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("hubai", "浮层配色设置失败：" + ex.Message);
        }
    }
}
