using System;
using System.Linq;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Services.Diagnostics;
using ClassSoftwareHub.Desktop.Services.HubAi;
using Microsoft.UI.Dispatching;
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
    /// 「正在思考」那个占位气泡（2026-10-31 Nick：长时间思考时卡片里毫无反应）。
    /// <para>
    /// 放在助手气泡**下面**、正文一到就撤掉 —— 它是独立控件而不是塞进气泡内部，
    /// 因为正文是 <see cref="MarkdownText.Apply"/> 每次整块重绘的，塞进去会被一起清掉。
    /// </para>
    /// </summary>
    private Border? _thinkingBubble;

    private ProgressRing? _thinkingRing;

    private TextBlock? _thinkingText;

    /// <summary>
    /// 每秒刷新一次"已等 N 秒"。
    /// ⛔ 必须存成字段：局部变量建出来的 <see cref="DispatcherQueueTimer"/> 会被 GC 收走，
    /// 收走之后**不再 tick**，界面就永远停在"正在思考 · 2 秒"（项目里踩过这个坑）。
    /// </summary>
    private DispatcherQueueTimer? _thinkingTimer;

    /// <summary>开始等待的时刻（<see cref="Environment.TickCount64"/>，毫秒）。</summary>
    private long _thinkingSince;

    /// <summary>
    /// 当前页面的现场数据生成器，由宿主页面在构造时挂上（2026-10-30 二版）。
    /// <para>
    /// Nick 的原话：「相关信息太少了……生成一份详细的 JSON 文件给 AI 读，这点信息够谁看」。
    /// 所以这里不再是一句摘要，而是每次发消息时**现采**一份结构化快照
    /// （<see cref="PageContextDoc"/>：页面名、角色、统计、清单、可执行操作）。
    /// </para>
    /// <para>
    /// ⛔ 必须是**发消息那一刻**调用，不能像原来那样在数据就绪时算一次存着 ——
    /// 筛选、勾选、扫描进度随时在变，缓存的很快就是错的。
    /// </para>
    /// <para>
    /// ⚠️ 走 system 通道（<see cref="HubAiRuntime.Start"/> 的 <c>pageContext</c>），
    /// 不进用户消息 —— 否则用户气泡和历史标题里全是机器文本。
    /// </para>
    /// </summary>
    public Func<PageContextDoc?>? ContextProvider { get; set; }

    public HubAiFlyout()
    {
        InitializeComponent();

        // 深色下换成原生底色 + 主题色描边（浅色那套 Nick 说好看，别动）
        ApplySurface();
        ActualThemeChanged += (_, _) =>
        {
            ApplySurface();
            RepaintConversation();
        };
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
        _answerPanel = null;
        _answerRendered = "";

        HideThinking();
        ShowStatusLine(null);

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
    /// <para>
    /// 2026-10-31：① 正文一到就撤掉「正在思考」气泡；
    /// ② 有正文但又在等工具时，阶段说明改到底部那行小字上（两处不会同时出现）。
    /// </para>
    /// </summary>
    private void RenderLive()
    {
        if (_runtime is null) return;

        var body = _runtime.LiveBody;

        if (body.Length > 0) HideThinking();
        else if (_busy) ShowThinking();          // 幂等：还空着就一直挂着

        ShowStatusLine(body.Length > 0 ? _runtime.LiveStatus : null);

        if (_answerPanel is null || body == _answerRendered) return;

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

        // 现场数据现采一份（页面此刻的样子）。失败就当没有，别挡住提问。
        var contextJson = BuildContextJson();

        Input.Text = string.Empty;
        Intro.Visibility = Visibility.Collapsed;

        AddBubble(text, mine: true);
        _answerPanel = AddBubble(string.Empty, mine: false);
        _answerRendered = "";

        _busy = true;
        SetBusyUi(true);

        // 从这里到第一个字回来之间可能是几十秒（读本机概况 + 等模型首包），
        // 期间界面上原本什么都没有，看着就像卡死了 —— 先挂上「正在思考」
        ShowThinking();

        try
        {
            // 每次现采一次本机概况：缓存下来的隔一会就不准，这一步也只要几百毫秒
            var snapshot = await MachineProbe.CollectAsync();

            runtime.Start(
                cfg,
                snapshot,
                ActiveTools.Load(),
                text,
                onCompleted: usage => Dispatch(() => Finish(runtime, sessionId, before, usage, error: null)),
                onFailed: ex => Dispatch(() => Finish(runtime, sessionId, before, AiUsage.Empty, error: ex)),
                pageContext: contextJson);
        }
        catch (Exception ex)
        {
            AppLog.Warn("hubai", "浮层提问失败：" + ex.Message);
            HideThinking();
            ShowStatusLine(null);
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
            HideThinking();
            ShowStatusLine(null);
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

            // 需求 27：回答末尾的按钮。放在存档之前 —— 它只动界面，跟存档成败无关。
            if (current) RenderActions(runtime);

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

    /// <summary>
    /// 现采这一页的现场数据，转成 JSON。
    /// <para>
    /// 任何异常都吞掉返回 null：数据没生成出来只是"AI 少知道点情况"，
    /// 不该害得用户问了半天没人回答。
    /// </para>
    /// </summary>
    private string? BuildContextJson()
    {
        if (ContextProvider is null) return null;

        try
        {
            var doc = ContextProvider();
            if (doc is null) return null;

            var json = doc.ToJson();
            AppLog.Step("hubai", $"页面现场数据 {doc.Page}：{json.Length} 字符");
            return json;
        }
        catch (Exception ex)
        {
            AppLog.Warn("hubai", "页面现场数据生成失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 回答末尾的按钮（需求 27）。挂在那条回答气泡里面 —— 浮层只有一列，单独一条面板会显得断开。
    /// </summary>
    private void RenderActions(HubAiRuntime runtime)
    {
        var actions = runtime.Chat.Session.Actions;
        if (actions.Count == 0 || _answerPanel is null) return;

        var panel = AiActionBar.Build(actions, RunAction);
        if (panel is null) return;

        panel.Margin = new Thickness(0, 6, 0, 0);
        _answerPanel.Children.Add(panel);
        ScrollToEnd();
    }

    /// <summary>
    /// 用户点了按钮。跳转前先收起浮层 —— 否则它会盖在刚打开的那一页上。
    /// 换主题不收：用户可能还想接着聊，改了主题背后能直接看到。
    /// </summary>
    private void RunAction(AiAction action)
    {
        if (action.Kind == AiActionKind.Navigate) Hide();

        AiActionRunner.Run(action);
    }

    /// <summary>
    /// 主题换了 → 按新主题重画气泡。
    /// <para>
    /// 气泡里的画笔是建的那一刻取的（见 <see cref="AddBubble"/>），不会自己跟着主题变 ——
    /// 在回答末尾点「切换为深色 / 浅色」之后，浮层里的字会和底色糊在一起。
    /// </para>
    /// <para>
    /// ⚠️ 只回放**最终正文**：浮层本来就不显示思考过程与工具轨迹（那些在完整页里）。
    /// </para>
    /// </summary>
    private void RepaintConversation()
    {
        if (_runtime is null || !IsOpen) return;

        var messages = _runtime.Chat.Snapshot();
        if (messages.Count == 0) return;

        HideThinking();
        Messages.Children.Clear();
        Intro.Visibility = Visibility.Collapsed;
        _answerPanel = null;

        foreach (var message in messages)
        {
            if (message.Role == "user")
                AddBubble(message.Content, mine: true);
            else if (message.Role == "assistant" && message.Content.Length > 0)
                _answerPanel = AddBubble(message.Content, mine: false);
        }

        // 正在生成：末尾要有一个接续面板，否则后面的增量刷不进来
        if (_runtime.IsRunning)
        {
            var live = _runtime.LiveBody;
            _answerPanel = AddBubble(live, mine: false);
            _answerRendered = live;
            if (live.Length == 0) ShowThinking();
        }
        else
        {
            _answerRendered = "";
        }

        RenderActions(_runtime);
    }

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
        try
        {
            // ⚠️ 先强制走一次布局：刚 Add 进来的子元素还没量高度，ScrollableHeight 是**旧的**，
            //    直接滚会差一截（需求 27 的按钮就正好卡在输入框后面）。
            Messages.UpdateLayout();
            MessagesScroll.ChangeView(null, MessagesScroll.ScrollableHeight, null, disableAnimation: true);
        }
        catch { }
    }

    /// <summary>生成中：发送键变「停止」。</summary>
    private void SetBusyUi(bool busy)
    {
        SendGlyph.Glyph = busy ? "\uE71A" : "\uE724";       // ■ 停止 / ➤ 发送
        ToolTipService.SetToolTip(SendButton, busy ? "停止生成" : "发送");
    }

    // ── 「正在思考」 ───────────────────────────────────────────────────────

    /// <summary>
    /// 挂上「正在思考 · N 秒」气泡（幂等，已经在就不重复建）。
    ///
    /// <para>
    /// 2026-10-31 Nick：「假如 AI 思考的很久的话，那这个卡片里是没有任何反应的」。
    /// 原来从发出去到第一个字回来之间完全是空白，看不出是"在想"还是"卡了"。
    /// </para>
    /// <para>
    /// 用原生 <see cref="ProgressRing"/> —— 转圈本身就是"它还活着"的证据，再加上秒数，
    /// 用户能判断是等一会儿还是按停止。⛔ 不自己画三点跳动那种假动画（那是自造控件）。
    /// </para>
    /// </summary>
    private void ShowThinking()
    {
        if (_thinkingBubble is not null)
        {
            UpdateThinkingText();
            return;
        }

        _thinkingRing = new ProgressRing
        {
            Width = 16,
            Height = 16,
            IsActive = true,
            IsIndeterminate = true,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _thinkingText = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrush.Get(this, "TextFillColorSecondaryBrush"),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(_thinkingRing);
        row.Children.Add(_thinkingText);

        _thinkingBubble = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = ThemeBrush.Get(this, "SubtleFillColorSecondaryBrush"),
            Child = row,
        };

        Messages.Children.Add(_thinkingBubble);

        _thinkingSince = Environment.TickCount64;
        UpdateThinkingText();
        StartThinkingTimer();
        ScrollToEnd();
    }

    /// <summary>撤掉「正在思考」气泡并停表。</summary>
    private void HideThinking()
    {
        try { _thinkingTimer?.Stop(); } catch { }

        if (_thinkingBubble is not null)
        {
            Messages.Children.Remove(_thinkingBubble);
            _thinkingBubble = null;
        }

        _thinkingRing = null;
        _thinkingText = null;
    }

    /// <summary>
    /// 每秒刷新一次文案。
    /// <para>
    /// 优先用运行时的阶段说明（「正在统计目录占用，可能需要一会儿」）——
    /// 那比笼统的"思考"更能解释为什么慢；没有阶段说明时才退回"正在思考"。
    /// </para>
    /// </summary>
    private void UpdateThinkingText()
    {
        if (_thinkingText is null) return;

        var status = _runtime?.LiveStatus;
        if (string.IsNullOrWhiteSpace(status) || status == "思考中") status = "正在思考";

        // 补一个省略号，读起来是"进行中"而不是一个静止的标签
        if (!status.EndsWith('…') && !status.EndsWith('.')) status += "…";

        var seconds = (int)((Environment.TickCount64 - _thinkingSince) / 1000);
        _thinkingText.Text = seconds >= 1 ? $"{status} {seconds} 秒" : status;
    }

    private void StartThinkingTimer()
    {
        var queue = DispatcherQueue;
        if (queue is null) return;

        _thinkingTimer ??= queue.CreateTimer();
        _thinkingTimer.Interval = TimeSpan.FromSeconds(1);

        // ⛔ 先减再加：重复调用 ShowThinking 时不能挂上两个处理函数
        _thinkingTimer.Tick -= OnThinkingTick;
        _thinkingTimer.Tick += OnThinkingTick;
        _thinkingTimer.Start();
    }

    private void OnThinkingTick(DispatcherQueueTimer sender, object args) => UpdateThinkingText();

    /// <summary>
    /// 底部那行阶段小字（有正文、又在等工具时才用）。
    /// 与「正在思考」气泡**互斥**：空正文时气泡说话，已有正文时这行说话，不会同时出现。
    /// </summary>
    private void ShowStatusLine(string? status)
    {
        var text = status ?? string.Empty;

        if (StatusLine.Text != text) StatusLine.Text = text;
        StatusLine.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
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
