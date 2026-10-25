using System.Diagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Services.Diagnostics;
using ClassSoftwareHub.Desktop.Services.HubAi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// Hub AI 完整对话页（2026-10-18）。
///
/// <para>
/// ⚠️ **当前没有接模型**：<see cref="Send"/> 只回一句占位。AiClient、诊断采集（硬件 / 负载 /
/// 存储 / 已装软件）都还没写，这一版只用来把形态定下来（尺寸、配色、消息流手感）。
/// </para>
/// <para>
/// 接模型时这里是唯一需要改的地方：把占位回复换成 <c>AiClient.StreamAsync</c> 的流式回调。
/// 与「软件下载」页右下浮层共用同一套逻辑，⛔ 别各写一套。
/// </para>
/// </summary>
public sealed partial class HubAiPage : Page
{
    public HubAiPage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 从「软件下载」页右下浮层带过来的问题。浮层自己不做对话，把问题交到这里统一处理。
    /// ⚠️ 用完必须立刻清空，否则每次进这一页都会重发一次。
    /// </summary>
    public static string? PendingQuestion;

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // ⛔⛔ 只在**首次**导航时初始化。切走再回来时 OnNavigatedTo 会再跑一遍，
        //    如果又去重建 ItemsSource，SelectedItem 变化会在**下一拍**才触发 SelectionChanged，
        //    那时 _switchingSession 已经复位 ⇒ 直接把当前对话覆盖成历史回放（Nick 反复遇到的“回来就丢”）。
        if (!_uiReady)
        {
            _uiReady = true;

            ApplyEnabledState();
            LoadSessions();
            LoadPickers();

            WebToggle.IsChecked = App.Settings.Current.HubAiWebEnabled;

            HistoryPanel.Visibility = App.Settings.Current.HubAiHistoryCollapsed
                ? Visibility.Collapsed : Visibility.Visible;
        }

        // 布局实测（排查"内容偏右"这类问题只能靠量，靠推断会翻车）
        // ⚠️ 必须在 LayoutUpdated 里量 —— OnNavigatedTo 阶段所有 ActualWidth 都还是 0。
        void DumpLayout(object? s, object? args)
        {
            HubAiMessages.LayoutUpdated -= DumpLayout;

            AppLog.Info("hubai-layout",
                $"历史={HistoryPanel.ActualWidth:0} 侧栏={SidePanel.ActualWidth:0} " +
                $"消息区={HubAiMessages.ActualWidth:0} 输入={ChatInput.ActualWidth:0} " +
                $"历史可见={HistoryPanel.Visibility} 侧栏可见={SidePanel.Visibility}");
        }

        HubAiMessages.LayoutUpdated += DumpLayout;

        var pending = PendingQuestion;
        PendingQuestion = null;

        if (string.IsNullOrEmpty(pending) || !App.Settings.Current.HubAiEnabled) return;

        HubAiInput.Text = pending;

        // 页面刚构造完，控件还没真正布局好，直接发话会出现"消息进了但滚不动"，延后一拍再发
        DispatcherQueue.TryEnqueue(() => _ = SendAsync());
    }

    // ══════════════════════════════════════════════════════
    //  历史对话
    // ══════════════════════════════════════════════════════

    private List<ChatSession> _sessions = new();

    /// <summary>当前对话的存档 id。新建时为空，第一次回答后生成。</summary>
    private string? _currentSessionId;

    /// <summary>刷新列表时压住 <c>SelectionChanged</c>，否则每刷一次就会重载一遍当前对话。</summary>
    private bool _switchingSession;

    /// <summary>页面是否已初始化。⚠️ 与 NavigationCacheMode=Required 配套，见 OnNavigatedTo 的注释。</summary>
    private bool _uiReady;

    private void LoadSessions()
    {
        _sessions = ChatSessionStore.LoadAll();

        // ⛔⛔ 2026-10-23：页面被重建时 _currentSessionId 是空的 —— 从存档把「上次看的会话」捡回来。
        //    Nick 报的「聊到一半切页，回来对话被吞」就是这么来的：页面一重建，内存状态全丢，
        //    列表里又没有选中项 ⇒ 回来的是一张空白页。
        if (string.IsNullOrEmpty(_currentSessionId))
        {
            var last = App.Settings.Current.HubAiLastSessionId;
            if (!string.IsNullOrEmpty(last) && _sessions.Any(s => s.Id == last))
                _currentSessionId = last;
        }

        _switchingSession = true;
        try
        {
            SessionList.ItemsSource = null;
            SessionList.ItemsSource = _sessions;

            if (_currentSessionId is not null)
                SessionList.SelectedItem = _sessions.FirstOrDefault(s => s.Id == _currentSessionId);
        }
        finally
        {
            _switchingSession = false;
        }

        // ⛔⛔ 2026-10-23：**把选中项重设一次，让 SelectionChanged 真正跑一遍**。
        //    上面那段是在 _switchingSession=true 下选中会话的，事件被守卫吃掉了 ——
        //    于是页面重建后「列表里选中了、消息区却是空的」。这里清一下再设回去，触发渲染。
        if (SessionList.SelectedItem is ChatSession restored)
        {
            SessionList.SelectedItem = null;
            SessionList.SelectedItem = restored;
        }
    }

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        BindRuntime(HubAiRuntime.For(null));
        _liveBubble = null;

        // 「↑ 编辑上一条」的状态必须在这里断掉（2026-10-23 修）：
        // 否则用户拉回上一条后改主意点了"新对话"，下一条消息会去截断一个已经空了的会话。
        _editingFromHistory = false;

        _runtime.Reset();
        _currentSessionId = null;
        App.Settings.Current.HubAiLastSessionId = "";
        App.Settings.Save();

        _switchingSession = true;
        try { SessionList.SelectedItem = null; }
        finally { _switchingSession = false; }

        ClearConversation();
        WelcomePanel.Visibility = Visibility.Visible;   // 回到空对话 ⇒ 欢迎屏重新露出
        HubAiInput.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// 历史项右键 → 删除。
    /// ⚠️ 走 ListView 的 RightTapped 手动弹，不用 `ContextFlyout` ——
    ///    挂到 DataTemplate 的元素上会被 ListViewItem 吞掉，右键毫无反应（实测过）。
    /// </summary>
    private void SessionList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (SessionOf(e.OriginalSource) is not { } session) return;

        var flyout = new MenuFlyout();
        var delete = new MenuFlyoutItem { Text = "删除这次对话", Tag = session.Id };
        delete.Click += DeleteSession_Click;
        flyout.Items.Add(delete);

        flyout.ShowAt((FrameworkElement)sender, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Position = e.GetPosition((UIElement)sender),
        });

        e.Handled = true;
    }

    /// <summary>从被点中的元素往上找它属于哪条会话（点在文字上时 DataContext 在祖先上）。</summary>
    private static ChatSession? SessionOf(object? source)
    {
        var element = source as FrameworkElement;

        while (element is not null)
        {
            if (element.DataContext is ChatSession session) return session;
            element = VisualTreeHelper.GetParent(element) as FrameworkElement;
        }

        return null;
    }

    /// <summary>右键历史项 → 删除。删的是自己那条，如果正在看它，顺手开个新对话。</summary>
    private void DeleteSession_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string id) return;

        ChatSessionStore.Delete(id);

        // 存档删了，内存里那条运行时也要放掉（否则上下文一直挂着；在跑的顺手停掉）
        HubAiRuntime.Drop(id);

        if (_currentSessionId == id)
            NewChat_Click(sender, e);

        LoadSessions();
        AppLog.Step("hubai", "删除历史对话");
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switchingSession) return;
        if (SessionList.SelectedItem is not ChatSession session) return;

        // ⛔⛔ 2026-10-23（方案 B）：切会话**不再被拦住**。
        //    每个会话有独立的运行时，正在跑的那条会在后台继续，不会被这里的 Restore 覆盖。
        //    这里只把自己切到目标会话的运行时上，并把它现有的内容画出来。
        BindRuntime(HubAiRuntime.For(session.Id));
        _liveBubble = null;

        // 换了会话 / 重新进页，「↑ 编辑上一条」的状态就作废 ——
        // 否则下一条消息会把这段会话的最后一轮误删（2026-10-23 修）。
        _editingFromHistory = false;

        // ⚠️ 下面这两行**必须**在判断 `IsRunning` 之前落定（2026-10-23 修）：
        //    原来它们写在"正在生成"的早退分支后面，切到生成中的会话时 id 还停在上一条上，
        //    等那一轮跑完，存档就写进**别的会话**里去了。
        _currentSessionId = session.Id;
        App.Settings.Current.HubAiLastSessionId = session.Id;   // 重建后靠它选回来
        App.Settings.Save();

        var running = _runtime.IsRunning;

        // ⚠️ 正在生成时**不能 Restore**：运行中的上下文归它自己管，
        //    拿存档覆盖等于把在飞的那一轮抹掉。
        //    （`LoadSessions` 每次收尾都会回到这里，所以这条不是理论风险。）
        // 恢复时只回放文本轮次（存档里本来也只有这些）。
        // ⭐ 2026-10-23：用 `Branch()` 而不是 `Entries` —— 上下文必须与界面上显示的那一支一致，
        //    否则切到 ‹1/2› 的另一支、上下文还是原来那支，模型会"答非所见"。
        if (!running)
        {
            _runtime.Chat.Restore(session.Branch().Select(ToMessage));
        }

        ClearConversation();

        // 看历史会话时不出现欢迎屏，但本机概况要在（它是这段对话的第一条信息）
        WelcomePanel.Visibility = Visibility.Collapsed;
        SidePanel.Visibility = Visibility.Visible;

        // ⚠️ ⚠️ 2026-10-20：**相邻的 assistant 记录必须合并成一个气泡**。
        //    一次工具调用会存下两条 assistant（“想一段” + “结论”），
        //    若逐条渲染，历史里就会出现两个「已完成」两段深度思考，中间还夹着一大块空白。
        // ⭐ 2026-10-23：数据源从 `session.Entries` 换成 `session.Branch()` —— 只画**当前那一支**；
        //    同一位置的多个版本（编辑 / 重试产生的兄弟节点）用 ‹1/2› 切换。
        var branch = session.Branch();
        _branch = branch;
        AppLog.Step("hubai", $"渲染分支：current={session.CurrentId} 节点={branch.Count} 存档={session.Entries.Count}");

        var index = 0;
        while (index < branch.Count)
        {
            var entry = branch[index];

            if (entry.Role == "user")
            {
                HubAiMessages.Children.Add(MakeBubble(entry.Content, alignRight: true));
                AddBranchSwitcher(session, entry);
                index++;
                continue;
            }

            var head = entry;      // 这一坨的起点：切分支 / 重试都以它为准
            var mergedContent = new StringBuilder();
            var mergedReasoning = new StringBuilder();

            while (index < branch.Count && branch[index].Role != "user")
            {
                var part = branch[index];

                if (!string.IsNullOrWhiteSpace(part.Content))
                {
                    if (mergedContent.Length > 0) mergedContent.Append("\n\n");
                    mergedContent.Append(part.Content);
                }

                if (!string.IsNullOrEmpty(part.Reasoning))
                {
                    if (mergedReasoning.Length > 0) mergedReasoning.Append("\n\n");
                    mergedReasoning.Append(part.Reasoning);
                }

                index++;
            }

            // 正文和思考都空的一轮（旧存档里出现过：只存下角色、什么都没存到）——
            // 画出来就是一个没有内容的「已完成」气泡，直接跳过（2026-10-23 补）。
            if (mergedContent.Length == 0 && mergedReasoning.Length == 0) continue;

            var bubble = NewAssistantBubble();
            bubble.Box.Visibility = Visibility.Visible;

            // ⚠️ 只有真的攒到思考内容才画折叠块 —— 空的一块展开后什么都没有（Nick 截图里的"思考被腰斩"）
            if (mergedReasoning.Length > 0)
            {
                // ⚠️ 2026-10-23：历史回放也换成**轻量折叠**（原来这里还是 Expander）——
                //    实时气泡早就不用了，历史却留着那条系统灰底下拉框，切回旧会话又是那个样子。
                // ⚠️ 存档里只存了思考文本，没有工具调用记录 ⇒ 回放只有一段思考，这属于正常。
                var think = MakeCollapsible("深度思考", null);
                think.Body.Children.Add(MakeThinkSegment(mergedReasoning.ToString()));
                bubble.Steps.Children.Add(think.Root);
            }

            bubble.Status.Text = "已完成";
            MarkdownText.Apply(bubble.Body, mergedContent.ToString(), bubble.BodyForeground);

            // ⚠️ 历史里只存了文本，但**元数据行不能少** —— Nick 要求“切回来原封不动”，
            //    没有复制/重试和模型用量，看起来就是“全丢了”。
            var histPalette = BubblePalette();
            var copyText = mergedContent.ToString();

            // ⚠️ 格式必须与实时对话**一模一样**：
            //    `模型名 · N tokens · 参考价`。历史里存的是模型 **Id**，要反查成展示名。
            if (!string.IsNullOrWhiteSpace(session.Model))
            {
                var displayName = AiModelStore.All
                    .FirstOrDefault(m => string.Equals(m.Id, session.Model, StringComparison.OrdinalIgnoreCase))
                    ?.Name ?? session.Model;

                // AiUsage 是 record，只能走三参构造（输入 / 输出 / 合计）
                var cost = EstimateCost(new AiUsage(
                    session.Tokens * 7 / 10,
                    session.Tokens * 3 / 10,
                    session.Tokens));

                bubble.MetaText.Text = session.Tokens > 0
                    ? $"{displayName} · {session.Tokens:N0} tokens · {cost}"
                    : displayName;
            }

            bubble.MetaActions.Children.Add(MakeIconButton("\uE8C8", "复制", histPalette.SubText, () => CopyText(copyText)));

            // ⭐ 2026-10-23：「重试」改成**在这一轮的提问下面长出一个兄弟回答**，
            //    不再以"再问一遍"的方式追加一条新提问（那样两个回答会串在一条线上）。
            bubble.MetaActions.Children.Add(MakeIconButton("\uE72C", "重试", histPalette.SubText, () => RetryFrom(head)));
            AddBranchSwitcher(session, head);
        }

        // 页脚属于"这一段对话的存档"，正在生成时不能压在实时气泡上面
        if (!running && !string.IsNullOrEmpty(session.Model) && session.Tokens > 0)
            AddHistoryFooter(session.Model, session.Tokens);

        if (running)
        {
            // 在飞的那一轮的**提问**还没进存档（新对话的首轮尤其明显）—— 补一个用户气泡，
            // 否则切回来只能看到一段光秃秃的回答，不知道自己问的是什么。
            var asked = _runtime.Chat.Snapshot().LastOrDefault(m => m.Role == "user");
            if (asked?.Content is { Length: > 0 } question
                && _branch.LastOrDefault(x => x.Role == "user")?.Content != question)
            {
                HubAiMessages.Children.Add(MakeBubble(question, alignRight: true));
            }

            // 这段正在后台生成：历史照常画，末尾再接一个实时气泡。
            // ⛔ 别像原来那样 `ClearConversation()` 之后只画实时那一段 ——
            //    那样切回来看到的是"前面的对话全没了"，而这正是方案 B 要解决的场景。
            var resumed = NewAssistantBubble();
            _liveBubble = resumed;
            resumed.Box.Visibility = Visibility.Visible;
            SetBusy(true);
            PaintLive(force: true);
        }
        else if (_busy)
        {
            // ⚠️ 必须复位（2026-10-23 修）：从"生成中的会话"切到空闲会话时，原来这里什么都不做，
            //    输入框一直是灰的、按钮还显示"停止"，点下去也没有反应 —— 用户被卡在里面。
            SetBusy(false);
        }

        ScrollToBottom();
    }

    /// <summary>清掉对话气泡，**保留开头那两张卡**（本机概况 / 初步判断）。</summary>
    /// <summary>
    /// 清掉对话气泡，**保留欢迎屏**。
    /// ⚠️ 2026-10-20 重做后：诊断卡已搬到右栏，消息流里只剩「欢迎屏（静态）+ 动态消息」，
    /// 所以这里改成保留 1 个。以前保留 2 个是因为消息流里还压着两张卡片。
    /// </summary>
    /// <summary>存档节点 → 上下文消息（分支切换、分叉前置恢复都用它）。</summary>
    private static AiMessage ToMessage(SessionEntry entry) =>
        entry.Role == "user"
            ? AiMessage.User(entry.Content)
            : AiMessage.Assistant(entry.Content) with { Reasoning = entry.Reasoning };

    /// <summary>
    /// 在气泡下面挂一条「‹ 1/2 ›」—— 同一个位置有多个版本时才出现。
    /// ⛔ 别自作主张把旧版本删掉：那是用户"编辑 / 重试"之前的内容，删了就找不回来。
    /// </summary>
    private void AddBranchSwitcher(ChatSession session, SessionEntry entry)
    {
        var siblings = session.Siblings(entry);
        if (siblings.Count < 2) return;

        var position = siblings.FindIndex(e => e.Id == entry.Id);
        if (position < 0) return;

        var palette = BubblePalette();

        Button MakeArrow(string glyph) => new()
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 10 },
            Background = null,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 1, 4, 1),
            MinHeight = 0,
            MinWidth = 0,
            Foreground = palette.SubText,
        };

        var prev = MakeArrow("\uE76B");   // ChevronLeft
        var next = MakeArrow("\uE76D");   // ChevronRight

        prev.IsEnabled = position > 0;
        next.IsEnabled = position < siblings.Count - 1;

        prev.Click += (_, _) => SwitchBranch(session, siblings[Math.Max(0, position - 1)]);
        next.Click += (_, _) => SwitchBranch(session, siblings[Math.Min(siblings.Count - 1, position + 1)]);

        var label = new TextBlock
        {
            Text = $"{position + 1}/{siblings.Count}",
            FontSize = 11,
            Foreground = palette.SubText,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = entry.Role == "user" ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(entry.Role == "user" ? 0 : 8, 0, entry.Role == "user" ? 8 : 0, 4),
        };

        row.Children.Add(prev);
        row.Children.Add(label);
        row.Children.Add(next);

        HubAiMessages.Children.Add(row);
    }

    /// <summary>
    /// 切到同一位置的另一个版本：把 currentId 挪到那一支的**叶子**上再整页重画。
    /// ⚠️ 必须落到叶子，否则只显示半截，后面的回合看起来像"没了"。
    /// </summary>
    private void SwitchBranch(ChatSession session, SessionEntry target)
    {
        ChatSessionStore.SetCurrent(session.Id, session.LeafOf(target.Id));
        LoadSessions();
        AppLog.Step("hubai", "切换历史分支");
    }

    /// <summary>
    /// 「重试」：以这条回答的**提问**为父节点，重新生成一个兄弟回答。
    /// 旧回答不删 —— 它变成同一位置的另一个版本，用 ‹1/2› 切回来。
    /// </summary>
    private void RetryFrom(SessionEntry assistant)
    {
        if (_busy) return;

        var user = _branch.FirstOrDefault(e => e.Id == assistant.ParentId);
        if (user is null || user.Role != "user") return;

        ForkForRetry(user);
        HubAiInput.Text = user.Content;
        _ = SendAsync();
    }

    /// <summary>编辑重发：新的提问挂在**旧提问的父节点**下（与旧提问并列成两支）。</summary>
    private void ForkForEdit(SessionEntry oldUser)
    {
        var index = _branch.FindIndex(e => e.Id == oldUser.Id);

        _forkPending = true;
        _forkAppendUser = true;
        _forkParentId = oldUser.ParentId;
        _forkKeep = index < 0 ? 0 : index;
    }

    /// <summary>重新生成：新的回答挂在**同一个提问**下（与旧回答并列成两支）。</summary>
    private void ForkForRetry(SessionEntry user)
    {
        var index = _branch.FindIndex(e => e.Id == user.Id);

        _forkPending = true;
        _forkAppendUser = false;             // 提问已经在上下文里，别再加一遍
        _forkParentId = user.Id;
        _forkKeep = index < 0 ? _branch.Count : index + 1;
    }

    private void ClearConversation()
    {
        while (HubAiMessages.Children.Count > 1)
            HubAiMessages.Children.RemoveAt(HubAiMessages.Children.Count - 1);
    }

    /// <summary>回答结束后存档。⛔ 没等到回答（比如刚发就取消）不存，免得列表里堆半截空对话。</summary>
    /// <param name="rt">发起这一轮的那个运行时。⛔ 不要读页面上的 `_runtime`。</param>
    /// <param name="sessionId">发起时那一段的存档 id；新对话为 null（存档完才拿到）。</param>
    /// <param name="forkParentId">新节点挂到哪个父节点下；null = 从根起。</param>
    /// <param name="before">这一轮开始前的消息条数，之后多出来的才是新产生的轮次。</param>
    /// <remarks>
    /// ⚠️ 2026-10-23 改成"参数传入"而不是读页面字段：这一轮可能在后台跑完，
    ///    那时用户早就切到别的会话了，读 `_runtime` / `_currentSessionId` 会把结果写进别人那段。
    /// <para>
    /// ⭐ 同时改成**只追加这一轮的新节点**（树）：不再拿整篇快照覆盖存档，
    ///    于是重试 / 编辑重发产生的旧分支不会被执行"替换"掉。
    /// </para>
    /// </remarks>
    private void PersistSession(HubAiRuntime rt, string? sessionId, string? forkParentId, int before, AiUsage usage)
    {
        var snapshot = rt.Chat.Snapshot();

        // 这一轮真正新增的部分（用户的那条提问 + 模型的回答，含工具往返产生的中间 assistant）
        var turns = snapshot.Skip(before).ToList();
        if (turns.Count == 0) return;

        // ⛔ 判据是「**有正文的**助手回合」，不是「有 assistant 角色」。
        //    2026-10-23 修：原来只判 `All(m => m.Role != "assistant")`，于是"只出了思考、正文一个字没回来"
        //    的那一轮也会被存档 —— 历史里就留下一个展开只有思考、正文空白的「已完成」气泡。
        if (!turns.Any(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content))) return;

        var fallbackTitle = turns.FirstOrDefault(m => m.Role == "user")?.Content ?? "未命名对话";

        var savedId = ChatSessionStore.SaveBranch(
            sessionId,
            forkParentId,
            turns,
            fallbackTitle,
            App.Settings.Current.HubAiModel,
            usage.TotalTokens);

        // 新对话第一次存档才拿到 id —— 运行时要从 draft 键改挂到真实 id 上。
        // ⛔ 不挂过去的话，同一个会话会有两个运行时：draft 那个带着完整上下文永不释放，
        //    列表里点进来的又是另一个空的（2026-10-23 修，`AdoptDraft` 原先写了没人调用）。
        if (string.IsNullOrEmpty(sessionId) && !string.IsNullOrEmpty(savedId))
            HubAiRuntime.AdoptDraft(savedId);

        // 只有"页面还停在这条运行时上"才回写页面状态（存盘失败时 savedId 为 null，那时不动）
        if (savedId is { Length: > 0 } && ReferenceEquals(rt, _runtime))
        {
            _currentSessionId = savedId;
            App.Settings.Current.HubAiLastSessionId = savedId;
            App.Settings.Save();
        }

        LoadSessions();
    }

    /// <summary>恢复历史时，把这次对话的模型与累计用量也补在底下。</summary>
    /// <summary>历史页脚的标记，发送新消息时按它清掉。</summary>
    private const string HistoryFooterTag = "history-footer";

    private void AddHistoryFooter(string model, int tokens)
    {
        var palette = BubblePalette();

        HubAiMessages.Children.Add(new TextBlock
        {
            Text = $"{model} · 本次对话共 {tokens:N0} tokens",
            FontSize = 11,
            Foreground = palette.SubText,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 4, 0),
            Tag = HistoryFooterTag,
        });

        ScrollToBottom();
    }

    /// <summary>
    /// 按总闸切换"能聊"和"去启用"两种形态。
    /// ⚠️ 每次导航都重算 —— 本页 NavigationCacheMode=Enabled，用户开关一次再回来不会重建。
    /// </summary>
    private void ApplyEnabledState()
    {
        var on = App.Settings.Current.HubAiEnabled;

        // 配置好模型之后就不该再挂"尚未配置"的横幅了
        var configured = !string.IsNullOrWhiteSpace(App.Settings.Current.HubAiBaseUrl)
                         && !string.IsNullOrWhiteSpace(App.Settings.Current.HubAiModel);

        DisabledPanel.Visibility = on ? Visibility.Collapsed : Visibility.Visible;

        // ⚠️ 收起时不仅要 IsOpen=false，还要 Visibility=Collapsed ——
        //    InfoBar 关掉内容后控件本身仍占一点高度，会在这里留出一条莫名其妙的空白。
        NotWiredBar.IsOpen = on && !configured;
        NotWiredBar.Visibility = (on && !configured) ? Visibility.Visible : Visibility.Collapsed;

        ChatScroll.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ChatInput.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        // 首次进页才采一次。采集本身只有几百毫秒，但没必要每次切回来都重跑 ——
        // 概况不是实时监控，用户要看最新数据重开一次页面即可。
        if (on && !_snapshotLoaded)
        {
            _snapshotLoaded = true;
            LoadSnapshot();
        }
    }

    /// <summary>采集是否已经跑过（本页实例长驻，只跑一次）。</summary>
    private bool _snapshotLoaded;

    /// <summary>
    /// 读一次本机概况并填进卡片。**不经过网络、不需要模型** —— 这一块就是采集层的自检。
    /// ⚠️ 采集失败也不能让整页空着：失败项写进 Warnings，卡片照常显示能拿到的那几行。
    /// </summary>
    private async void LoadSnapshot()
    {
        SnapshotLoading.IsActive = true;
        SnapshotLoading.Visibility = Visibility.Visible;
        SnapshotBody.Visibility = Visibility.Collapsed;
        SnapshotStatus.Text = "正在读取";

        try
        {
            var snap = await MachineProbe.CollectAsync();
            _snapshot = snap;   // 对话时直接复用这份，不再重采
            RenderSnapshot(snap);
            RenderFindings(MachineDiagnosis.Analyze(snap));

            // 把"准备发给模型的那段文字"记进调试日志 —— 接模型前唯一能离线核对格式与长度的地方。
            // 用户报"AI 说的不像我的电脑"时，先看这行。
            AppLog.Debug("hubai", "体检单内容：\n" + MachineDigest.ToPrompt(snap));
        }
        catch (Exception ex)
        {
            AppLog.Exception("hubai", ex, "采集本机概况");
            SnapshotLoading.IsActive = false;
            SnapshotLoading.Visibility = Visibility.Collapsed;
            SnapshotStatus.Text = "读取失败";
            SnapCpu.Text = "读取失败，详情见日志。";
            SnapshotBody.Visibility = Visibility.Visible;
        }
    }

    private void RenderSnapshot(MachineSnapshot s)
    {
        SnapshotLoading.IsActive = false;
        SnapshotLoading.Visibility = Visibility.Collapsed;
        SnapshotBody.Visibility = Visibility.Visible;

        SnapshotStatus.Text = s.Warnings.Count == 0
            ? $"读取完成 · {s.ElapsedMs} ms"
            : $"{s.Warnings.Count} 项未取到 · {s.ElapsedMs} ms";

        // CPU：型号 · 线程数 · 主频 · 当前占用（占用的 -1 表示本次没取到，不显示成 0%）
        var cpu = s.CpuName.Length == 0 ? "未取到" : s.CpuName;
        if (s.CpuName.Length > 0)
        {
            cpu += $" · {s.CpuThreads} 线程";

            // ⚠️ 型号串里常常已经带「@ 2.60GHz」（如 i7-9750H），再拼一次就变成
            //    「… @ 2.60GHz · 2.6 GHz」，重复且显得没做功课。
            if (s.CpuMhz > 0 && !s.CpuName.Contains("GHz", StringComparison.OrdinalIgnoreCase))
                cpu += $" · {s.CpuMhz / 1000.0:0.0} GHz";

            if (s.CpuLoadPercent >= 0) cpu += $" · 当前 {s.CpuLoadPercent:0}%";
        }
        SnapCpu.Text = cpu;

        SnapMemory.Text = s.MemoryTotalMb == 0
            ? "未取到"
            : $"{Size(s.MemoryTotalMb)} 总计 · 可用 {Size(s.MemoryFreeMb)} · 占用 {s.MemoryLoadPercent}%";

        SnapOs.Text = s.OsName.Length == 0
            ? "未取到"
            : s.OsBuild > 0
                ? $"{s.OsName} · 构建 {s.OsBuild}" + (s.OsDisplayVersion.Length > 0 ? $" · {s.OsDisplayVersion}" : "")
                : s.OsName;

        // 磁盘：这台机器上挂了好几个盘/U 盘很常见，全列出来会顶出单行。
        // 只报前两块 + 总量，够判断"是不是快满了"。
        if (s.Disks.Count == 0)
        {
            SnapDisk.Text = "未取到";
        }
        else
        {
            var shown = s.Disks.Take(2).Select(d =>
                $"{d.Drive} {Size(d.TotalMb)} 可用 {Size(d.FreeMb)}（{d.UsedPercent}% 已用）");
            SnapDisk.Text = string.Join(" · ", shown)
                + (s.Disks.Count > 2 ? $" · 等 {s.Disks.Count} 块" : "");
        }

        var top = s.TopProcesses.Count == 0
            ? ""
            : " · 占用前三：" + string.Join("、", s.TopProcesses.Take(3).Select(p => $"{p.Name} {p.WorkingSetMb} MB"));
        SnapProcess.Text = $"{s.ProcessCount} 个{top}";

        SnapStartup.Text = $"{s.StartupCount} 项";
        SnapApps.Text = s.InstalledAppCount > 0
            ? $"{s.InstalledAppCount} 款"
            : "未取到";
    }

    /// <summary>MB 转人话。小于 1 GB 就报 MB，别显示「0.8 GB」这种不直观的数。</summary>
    private static string Size(long mb)
        => mb >= 1024 ? $"{mb / 1024.0:0.#} GB" : $"{mb} MB";

    /// <summary>
    /// 渲染本地规则结论（<see cref="MachineDiagnosis"/>）。
    /// ⚠️ 级别只靠字形 + 颜色区分，**不给每条加边框卡片** —— 一次 3~5 条，加边框会变成一堵色块墙。
    /// </summary>
    private void RenderFindings(IReadOnlyList<HealthFinding> findings)
    {
        FindingsPanel.Children.Clear();

        foreach (var f in findings)
        {
            var (glyph, brushKey) = f.Level switch
            {
                HealthLevel.Warn => ("\uE7BA", "SystemFillColorCautionBrush"),
                HealthLevel.Good => ("\uE930", "SystemFillColorSuccessBrush"),
                _ => ("\uE946", "TextFillColorSecondaryBrush"),
            };

            var icon = new FontIcon
            {
                Glyph = glyph,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 0, 0),
            };
            if (LookupBrush(brushKey) is { } brush) icon.Foreground = brush;

            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(icon);
            head.Children.Add(new TextBlock
            {
                Text = f.Title,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });

            // 说明文字缩进 21px = 图标宽 13 + 间距 8，让标题和正文左边缘对齐
            var detail = new TextBlock
            {
                Text = f.Detail,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
                Opacity = 0.75,
                Margin = new Thickness(21, 3, 0, 0),
            };

            var row = new StackPanel();
            row.Children.Add(head);
            row.Children.Add(detail);
            FindingsPanel.Children.Add(row);
        }
    }

    private static Brush? LookupBrush(string key)
    {
        try
        {
            return Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;
        }
        catch (Exception ex)
        {
            AppLog.Exception("hubai", ex, $"取画刷 {key}");
            return null;
        }
    }

    // ══════════════════════════════════════════════════════
    //  模型 / 思考参数就地切换（2026-10-18）
    // ══════════════════════════════════════════════════════

    /// <summary>回填下拉时压住 SelectionChanged，否则进页就写一遍盘。</summary>
    private bool _loadingPickers;

    private void LoadPickers()
    {
        _loadingPickers = true;
        try
        {
            AiModelStore.EnsureMigrated();
            EnsureEffortItems();

            // 只列**用户真的配过**的模型（2026-10-18：从"预设列表"改成"已配置列表"）
            ModelPicker.Items.Clear();
            foreach (var entry in AiModelStore.All)
                ModelPicker.Items.Add(new ComboBoxItem { Content = entry.Name, Tag = entry.Id });

            var active = AiModelStore.Active;

            if (active is null)
            {
                ModelPicker.PlaceholderText = "尚未配置模型";
                ThinkToggle.IsEnabled = false;
                EffortPicker.IsEnabled = false;
                return;
            }

            ModelPicker.SelectedItem = ModelPicker.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(i => (string?)i.Tag == active.Id);

            ThinkToggle.IsChecked = active.Thinking;
            EffortPicker.SelectedIndex = active.Effort switch { "low" => 0, "max" => 2, _ => 1 };

            UpdatePickerAvailability();
        }
        finally
        {
            _loadingPickers = false;
        }
    }

    private void EnsureEffortItems()
    {
        if (EffortPicker.Items.Count > 0) return;

        EffortPicker.Items.Add(new ComboBoxItem { Content = "轻度", Tag = "low" });
        EffortPicker.Items.Add(new ComboBoxItem { Content = "标准", Tag = "high" });
        EffortPicker.Items.Add(new ComboBoxItem { Content = "最深", Tag = "max" });
    }

    /// <summary>按当前模型的能力决定哪些控件可点。<c>glm-5.3</c> 这类强制思考的模型连开关都要锁死。</summary>
    private void UpdatePickerAvailability()
    {
        var entry = AiModelStore.Active;
        if (entry is null) return;

        var profile = AiProviders.ById(entry.ProviderId).ThinkingProfile;

        ThinkToggle.IsEnabled = profile.SupportsToggle && !profile.ForcedThinking;
        EffortPicker.IsEnabled = profile.SupportsEffort && entry.Thinking;

        if (profile.ForcedThinking) ThinkToggle.IsChecked = true;

        ToolTipService.SetToolTip(ThinkToggle, profile switch
        {
            { ForcedThinking: true } => "该模型强制思考，无法关闭",
            { SupportsToggle: false } => "该服务商没有思考开关",
            _ => "开启后模型会先思考再回答，思考过程显示在气泡上方",
        });
    }

    private void ModelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPickers) return;
        if (ModelPicker.SelectedItem is not ComboBoxItem { Tag: string id }) return;

        AiModelStore.Activate(id);

        _loadingPickers = true;
        try
        {
            var entry = AiModelStore.Active;
            if (entry is not null)
            {
                ThinkToggle.IsChecked = entry.Thinking;
                EffortPicker.SelectedIndex = entry.Effort switch { "low" => 0, "max" => 2, _ => 1 };
            }

            UpdatePickerAvailability();
        }
        finally
        {
            _loadingPickers = false;
        }

        AppLog.Step("hubai", $"切换模型：{AiModelStore.Active?.Name}");
    }

    private void ThinkToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingPickers || AiModelStore.Active is not { } entry) return;

        entry.Thinking = ThinkToggle.IsChecked == true;
        AiModelStore.SaveEntry(entry);

        UpdatePickerAvailability();
    }

    private void EffortPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPickers || AiModelStore.Active is not { } entry) return;
        if (EffortPicker.SelectedItem is not ComboBoxItem { Tag: string effort }) return;

        entry.Effort = effort;
        AiModelStore.SaveEntry(entry);
    }

    private void GoEnable_Click(object sender, RoutedEventArgs e)
        => App.MainWindow?.Shell.NavigateTo("hubai-settings");

    private void HubAiSend_Click(object sender, RoutedEventArgs e)
    {
        // 生成过程中这个按钮就是"停止"
        // ⛔⛔ 2026-10-23：原来这里调 `_streamCts?.Cancel()`，而 `_streamCts` 在换成会话运行时之后
        //    **再没有任何赋值点**（编译器 CS0649 点出来的就是它）—— 点下去什么都不会发生。
        //    取消现在归运行时管。
        if (_busy)
        {
            _runtime.Stop();
            return;
        }

        _ = SendAsync();
    }

    /// <summary>
    /// Enter 发送、Shift+Enter 换行。
    ///
    /// <para>
    /// ⚠️ 靠 <c>AcceptsReturn="False"</c> 兜底：那样 TextBox 本身就**不会**把 Enter 变成换行，
    /// 所以即使下面这段判断出岔子，最坏也只是 Shift+Enter 也发了出去，而不会变成"Enter 换行"。
    /// </para>
    /// <para>
    /// ⚠️ 修饰键用**位与**判断，不用 <c>HasFlag</c> —— <see cref="Windows.UI.Core.CoreVirtualKeyStates"/>
    /// 是 Flags 枚举，HasFlag 在只置了 Locked 位时会给出误导结果。
    /// </para>
    /// </summary>
    private void HubAiInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // ↑ 键（输入框为空时）：把最后一条提问拉回输入框，进入"编辑重发"状态。
        // 对标成熟工具的做法：改完直接替换那一轮，而不是再追加一条"我说错了"。
        if (e.Key == Windows.System.VirtualKey.Up
            && string.IsNullOrWhiteSpace(HubAiInput.Text)
            && !_busy)
        {
            var lastUser = _runtime.Chat.Snapshot().LastOrDefault(m => m.Role == "user");
            if (lastUser is not null)
            {
                HubAiInput.Text = lastUser.Content ?? "";
                HubAiInput.SelectionStart = HubAiInput.Text.Length;
                _editingFromHistory = true;

                // 记住"改的是存档里的哪一条" —— 提交时要挂到它的父节点下（新开一支），
                // 而不是把后面整段截掉（2026-10-23）。
                _editSource = _branch.LastOrDefault(x => x.Role == "user");
                e.Handled = true;
            }
            return;
        }

        if (e.Key != Windows.System.VirtualKey.Enter) return;

        var shiftState = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);

        var shiftDown = (shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down)
                        == Windows.UI.Core.CoreVirtualKeyStates.Down;

        if (shiftDown)
        {
            // Shift+Enter：手动在光标处插一个换行（AcceptsReturn=False 下 TextBox 不会自己插）
            e.Handled = true;

            var caret = HubAiInput.SelectionStart;
            var text = HubAiInput.Text ?? "";

            HubAiInput.Text = text.Insert(caret, "\n");
            HubAiInput.SelectionStart = caret + 1;
            return;
        }

        e.Handled = true;
        if (!_busy) _ = SendAsync();
    }

    // ══════════════════════════════════════════════════════
    //  对话（真调用，2026-10-18）
    // ══════════════════════════════════════════════════════

    // ⛔⛔ 2026-10-23（方案 B）：对话上下文**不再归页面所有**。
    //    改成按会话取的运行时（Services/HubAi/HubAiRuntime），流式跑在它里面 ——
    //    页面切走/切会话都不影响它跑完，回来重新订阅就能看到。
    private HubAiRuntime _runtime = HubAiRuntime.For(null);

    /// <summary>当前绑定的运行时对应的会话 id 变化时，重新订阅。</summary>
    private void BindRuntime(HubAiRuntime runtime)
    {
        if (ReferenceEquals(_runtime, runtime)) return;

        _runtime.Changed -= OnRuntimeChanged;
        _runtime.ToolDone -= OnToolDone;

        _runtime = runtime;

        _runtime.Changed += OnRuntimeChanged;
        _runtime.ToolDone += OnToolDone;
    }

    /// <summary>
    /// 一个工具调完（2026-10-23 接上）。
    /// ⛔ 改成会话运行时那一版把这条通路漏了 —— 界面上只剩思考，"已读取 xxx" 的痕迹整段消失。
    /// 现在它进「深度思考」折叠块，与思考段落排在同一条时间线上。
    /// </summary>
    private void OnToolDone(string note) => OnUi(() =>
    {
        if (_liveBubble is null) return;
        AddToolStep(_liveBubble, note);
    });

    /// <summary>
    /// 运行时内容有变化 → 节流刷新当前气泡。
    /// ⚠️ 走"全量重绘"而不是"增量 append" —— 切走再回来时，页面手上没有增量，
    ///    只能按运行时的完整快照重画一遍。
    /// </summary>
    private void OnRuntimeChanged() => OnUi(() => PaintLive(force: false));

    /// <summary>上一次真正重绘的时刻（TickCount64），用来做 80ms 节流。</summary>
    private long _lastPaint;

    /// <summary>
    /// 把运行时的当前快照画到正在生成的那个气泡上。
    /// <para>
    /// ⚠️ 2026-10-23 补回 80ms 节流：原来每个增量都做一次**全量 Markdown 解析 + 贴底滚动**，
    ///    一个 token 一次，长回答时主线程会被明显拖慢（旧实现里本来有这道闸，重构时丢了）。
    ///    收尾时 <see cref="FinishRun"/> 会再全量重绘一次，所以最后一段不会丢。
    /// </para>
    /// </summary>
    private void PaintLive(bool force)
    {
        if (_liveBubble is null) return;

        var body = _runtime.LiveBody;
        var reasoning = _runtime.LiveReasoning;

        if (body.Length > 0 || reasoning.Length > 0 || _runtime.LiveStatus is not null)
        {
            _liveBubble.Box.Visibility = Visibility.Visible;
            if (!_liveBubble.EnterPlayed)
            {
                _liveBubble.EnterPlayed = true;
                PlayEnter(_liveBubble.Box);
            }
        }

        var now = Environment.TickCount64;
        if (!force && now - _lastPaint < 80) return;
        _lastPaint = now;

        if (reasoning.Length > 0) EnsureThinkBlock(_liveBubble, reasoning);
        if (body.Length > 0) MarkdownText.Apply(_liveBubble.Body, body, _liveBubble.BodyForeground);

        ShowStatus(_runtime.LiveStatus);
        ScrollToBottom();
    }

    /// <summary>当前正在生成的那个助手气泡（换会话/重开后重置）。</summary>
    private Bubble? _liveBubble;

    /// <summary>当前会话正在显示的那一支（根 → 叶子）。分支切换、分叉定位都靠它。</summary>
    private List<SessionEntry> _branch = new();

    /// <summary>
    /// 「下一次发送要分叉」的标记（编辑重发 / 重新生成用）。
    /// <para>
    /// ⛔ 2026-10-23：以前是 `Restore(snap.Take(idx))` 把上下文截断 —— 旧的回答就永久没了。
    /// 现在改成"挂到某个父节点下再发"，旧的那一支原样留在存档里，界面上用 ‹1/2› 切回来。
    /// </para>
    /// </summary>
    private bool _forkPending;

    /// <summary>分叉要挂到哪个节点下（null = 从根开始，等于新开一支）。</summary>
    private string? _forkParentId;

    /// <summary>分叉时保留的前缀条数（父节点在分支里的下标 + 1）。</summary>
    private int _forkKeep;

    /// <summary>这次分叉要不要把输入框的文字当成一条新提问追加（重新生成时为 false）。</summary>
    private bool _forkAppendUser = true;

    /// <summary>
    /// 造一个「可折叠的小标题行」（深度思考 / 工具调用用同一个样式）。
    ///
    /// ⚠️ 2026-10-23 换掉 Expander：Nick 说「深度思考用那个下拉对话框太丑了」。
    ///    Expander 自带一整条 system header 底色 + 大块留白，放在消息流里像块控件；
    ///    这里改成一枚**透明按钮**：左侧一个小箭头 + 一行小字，点一下展开下面的正文。
    ///    视觉上只有文字，跟消息本体一个层次。
    /// </summary>
    /// <param name="title">标题文字（如「深度思考」「工具调用」）。</param>
    /// <param name="subtitle">可选：标题右侧的补充（如调用次数）。</param>
    /// <returns>
    /// <c>Body</c> 是**容器**（StackPanel）而不是一段文字：
    /// 里面按时间顺序排「思考段落 → 工具调用行 → 思考段落 → …」，见 <see cref="EnsureThinkBlock"/>。
    /// </returns>
    private (StackPanel Root, StackPanel Body, FontIcon Chevron, Button Toggle, TextBlock Caption) MakeCollapsible(
        string title, string? subtitle)
    {
        var palette = BubblePalette();
        var chevron = new FontIcon
        {
            Glyph = "\uE76C",            // 右向小箭头（收起）
            FontSize = 10,
            Foreground = palette.SubText,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var caption = new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            Foreground = palette.SubText,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(chevron);
        row.Children.Add(caption);

        if (!string.IsNullOrEmpty(subtitle))
        {
            row.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11.5,
                Opacity = 0.7,
                Foreground = palette.SubText,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var toggle = new Button
        {
            // ⛔ 必须是"看起来不像按钮"的按钮：透明底、无边框、无最小宽度
            Background = null,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 2, 0, 2),
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = row,
        };

        var body = new StackPanel
        {
            Spacing = 4,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(16, 4, 0, 0),   // 与标题缩进对齐（箭头那点宽度）
        };

        toggle.Click += (_, _) =>
        {
            var expand = body.Visibility != Visibility.Visible;
            body.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
            chevron.Glyph = expand ? "\uE70D" : "\uE76C";   // 下 / 右
        };

        var root = new StackPanel { Spacing = 2 };
        root.Children.Add(toggle);
        root.Children.Add(body);
        return (root, body, chevron, toggle, caption);
    }

    /// <summary>折叠块里的一段思考文字（颜色跟随气泡副文字色）。</summary>
    private TextBlock MakeThinkSegment(string text) => new()
    {
        Text = text,
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 21,
        Foreground = BubblePalette().SubText,
    };

    /// <summary>
    /// 建「深度思考」折叠块（已经有了就跳过）。内容容器见 <see cref="Bubble.ThinkBody"/>。
    /// </summary>
    private void EnsureThinkContainer(Bubble bubble)
    {
        if (bubble.ThinkToggle is not null) return;

        var (root, body, chevron, toggle, caption) = MakeCollapsible("深度思考", null);

        bubble.ThinkBody = body;
        bubble.ThinkToggle = toggle;
        bubble.ThinkChevron = chevron;
        bubble.ThinkCaption = caption;
        bubble.ThinkText = null;

        bubble.Steps.Children.Add(root);
    }

    /// <summary>
    /// 把思考过程画进「深度思考」折叠块。
    /// <para>
    /// ⚠️ 2026-10-23：**工具调用也排在这条时间线里**（Nick 要求）。
    ///    运行时每调完一个工具就会清空思考缓冲，所以这里的 <see cref="Bubble.ThinkText"/>
    ///    被置空 —— 下一段思考会新起一个文字块，而不是覆写到工具行上面那一段。
    /// </para>
    /// </summary>
    private void EnsureThinkBlock(Bubble bubble, string reasoning)
    {
        EnsureThinkContainer(bubble);

        if (bubble.ThinkText is null)
        {
            bubble.ThinkText = MakeThinkSegment("");
            bubble.ThinkBody!.Children.Add(bubble.ThinkText);
        }

        bubble.ThinkText.Text = reasoning;
        bubble.ThinkBuffer.Clear();
        bubble.ThinkBuffer.Append(reasoning);
    }

    /// <summary>
    /// 往「深度思考」里追加一条工具调用记录（2026-10-23）。
    /// 与思考段落按真实发生顺序排列：想一段 → 调一个工具 → 再想一段 → …
    /// </summary>
    private void AddToolStep(Bubble bubble, string note)
    {
        EnsureThinkContainer(bubble);

        var palette = BubblePalette();

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 1, 0, 1),
        };

        row.Children.Add(new FontIcon
        {
            Glyph = "\uE9D9",              // 工具/扳手
            FontSize = 11,
            Foreground = palette.SubText,
            VerticalAlignment = VerticalAlignment.Center,
        });

        row.Children.Add(new TextBlock
        {
            Text = note,
            FontSize = 12.5,
            Foreground = palette.SubText,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });

        bubble.ThinkBody!.Children.Add(row);

        bubble.ToolSteps++;
        bubble.ThinkCaption!.Text = $"深度思考 · {bubble.ToolSteps} 次工具调用";

        // 下一段思考另起一块，别把工具行上面那段覆写掉
        bubble.ThinkText = null;
    }

    /// <summary>
    /// 是否处于「编辑上一条重发」状态。
    /// 2026-10-23 加（对标成熟 AI 工具：↑ 键把上一条 user 消息拉回输入框）。
    /// ⭐ 提交时不再"截断那一轮"，而是以 <see cref="_editSource"/> 的父节点为新分支起点
    /// （旧提问与它的回答都留在存档里）。
    /// </summary>
    private bool _editingFromHistory;

    /// <summary>↑ 拉回来的是存档里的哪一条（提交时按它定位分叉点）。</summary>
    private SessionEntry? _editSource;

    private bool _busy;

    /// <summary>最近一次采集的结果。对话时直接复用，不再重采（首屏那张卡已经采过一次）。</summary>
    private MachineSnapshot? _snapshot;

    /// <summary>
    /// 跑一轮问答。
    ///
    /// <para>
    /// ⚠️ 三个细节都是踩出来的：
    /// <list type="number">
    ///   <item><b>节流刷新</b>：SSE 一个 token 一个回调，每次都赋 <c>TextBlock.Text</c> 会卡死，
    ///         这里按 80ms 合批。</item>
    ///   <item><b>换气泡</b>：模型可能先说话、再调工具、再说话。第二段必须换新气泡，
    ///         否则"我先看看磁盘"和最终结论会糊成一段。</item>
    ///   <item><b>贴底才跟随</b>：用户往上翻历史时不能强行拽到底。</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <summary>欢迎屏的建议卡：点一下就等于替用户把这句话发出去。</summary>
    /// <summary>
    /// 侧栏的滑入 / 滑出（2026-10-20 第二轮）。
    ///
    /// <para>
    /// ⛔ **布局由 Visibility 决定，动画只负责位移与透明度**。
    /// 第一版用「列宽 Auto + 动画面板 Width」，出过事：折叠后 Width 与 Visibility 的状态会不一致，
    /// Auto 列仍按残留宽度占位，结果两栏明明收起了，内容却被挤到偏右（Nick 截图为证）。
    /// 现在：收起 = 播完淡出后设 <c>Collapsed</c>（列宽立刻归 0），展开 = 先 <c>Visible</c> 再滑进来。
    /// </para>
    /// <para>
    /// ⚠️ <c>Opacity</c> 与 <c>TranslateTransform.X</c> 都是非合成动画，必须 <c>EnableDependentAnimation</c>。
    /// </para>
    /// </summary>
    /// <param name="exitX">收起时往哪边滑：左侧栏 -20、右侧栏 +20。</param>
    private static void AnimateSidePanel(FrameworkElement panel, bool collapse, double exitX)
    {
        var translate = panel.RenderTransform as TranslateTransform;
        if (translate is null)
        {
            translate = new TranslateTransform();
            panel.RenderTransform = translate;
        }

        var fromX = collapse ? 0 : exitX;
        var toX = collapse ? exitX : 0;

        translate.X = fromX;
        panel.Opacity = collapse ? 1 : 0;

        var slide = new DoubleAnimation
        {
            To = toX,
            Duration = new Duration(TimeSpan.FromMilliseconds(collapse ? 160 : 220)),
            EasingFunction = new CubicEase
            {
                EasingMode = collapse ? EasingMode.EaseIn : EasingMode.EaseOut,
            },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(slide, translate);
        Storyboard.SetTargetProperty(slide, "X");

        var fade = new DoubleAnimation
        {
            To = collapse ? 0 : 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(collapse ? 160 : 200)),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, panel);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);

        if (collapse)
        {
            storyboard.Completed += (_, _) =>
            {
                panel.Visibility = Visibility.Collapsed;

                // 恢复成"展开态"的初始值，下次滑入才不会从错误位置开始
                translate.X = 0;
                panel.Opacity = 1;
            };
        }
        else
        {
            panel.Visibility = Visibility.Visible;
        }

        storyboard.Begin();
    }

    /// <summary>输入框聚焦：整块输入容器描边变强调色，模拟网页那边 `:focus-within` 的效果。</summary>
    /// <summary>联网开关：跟「思考」对等，只影响是否把搜索工具交给模型（2026-10-20）。</summary>
    private void WebToggle_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Current.HubAiWebEnabled = WebToggle.IsChecked ?? false;
        App.Settings.Save();
    }

    private void HubAiInput_GotFocus(object sender, RoutedEventArgs e)
        => InputShell.BorderBrush = ThemeBrush.Get(this, "AccentFillColorDefault");

    private void HubAiInput_LostFocus(object sender, RoutedEventArgs e)
        => InputShell.BorderBrush = ThemeBrush.Get(this, "CardStrokeColorDefault");

    /// <summary>
    /// 新消息入场（2026-10-20）：淡入 + 上浮 10px。
    /// ⚠️ 位移/透明度都是**非合成动画**，必须开 `EnableDependentAnimation`，否则代码不报错但画面不动。
    /// </summary>
    private static void PlayEnter(FrameworkElement element)
    {
        if (element.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            element.RenderTransform = transform;
        }

        transform.Y = 10;
        element.Opacity = 0;

        var fade = new DoubleAnimation
        {
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var rise = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(260)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(rise, transform);
        Storyboard.SetTargetProperty(rise, "Y");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Children.Add(rise);
        storyboard.Begin();
    }

    /// <summary>收起 / 展开左侧历史栏</summary>
    private void ToggleHistory_Click(object sender, RoutedEventArgs e)
    {
        var collapse = HistoryPanel.Visibility == Visibility.Visible;

        AnimateSidePanel(HistoryPanel, collapse, exitX: -20);

        App.Settings.Current.HubAiHistoryCollapsed = collapse;
        App.Settings.Save();
    }

    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not Button { Tag: string prompt }) return;

        HubAiInput.Text = prompt;
        _ = SendAsync();
    }

    private async Task SendAsync()
    {
        var text = HubAiInput.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        // ⚠️ 看历史时留下的那条「本次对话共 N tokens」页脚要清掉 ——
        //    它属于上一段对话，留在顶部会跟新消息混在一起（2026-10-20 截图里就顶在最上面）。
        foreach (var stale in HubAiMessages.Children.OfType<TextBlock>()
                     .Where(t => t.Tag as string == HistoryFooterTag).ToList())
        {
            HubAiMessages.Children.Remove(stale);
        }

        var model = AiModelStore.Active;
        if (model is null)
        {
            AddNotice("尚未添加模型。请到「设置 → Hub AI → 模型服务」添加一个。");
            return;
        }

        var cfg = model.ToConfig(SecretStore.Load(model.EffectiveSecretId));

        if (!cfg.IsUsable)
        {
            AddNotice("当前模型的配置不完整。请到「设置 → Hub AI → 模型服务」填写服务地址与模型名称。");
            return;
        }

        // 同一条会话同时只允许一条流。`HubAiRuntime.Start` 也会挡，但那是不声不响地 return，
        // 页面这边就会一直停在"生成中"（插进去的用户气泡永远等不到回答）。
        if (_runtime.IsRunning)
        {
            AddNotice("这一条还在生成中，请等它结束，或先点停止。");
            return;
        }

        // ⭐ 2026-10-23：编辑重发不再"截断重写"，而是**新开一支**。
        //    旧的提问和它的回答原样留在存档里，界面上用 ‹1/2› 切回来。
        if (_editingFromHistory)
        {
            _editingFromHistory = false;
            if (_editSource is not null) ForkForEdit(_editSource);
            _editSource = null;
        }

        var forkParentId = _forkParentId;      // 这一轮产生的新节点挂到谁下面
        var appendUser = _forkAppendUser;

        if (_forkPending)
        {
            _forkPending = false;
            _forkAppendUser = true;
            _forkParentId = null;

            // 上下文截到分叉点：旧分支的内容不能带进这一轮，否则模型会同时看到"两条并行的问题"
            var prefix = _forkKeep <= 0
                ? new List<SessionEntry>()
                : _branch.Take(_forkKeep).ToList();

            _runtime.Chat.Restore(prefix.Select(ToMessage));
        }

        HubAiInput.Text = string.Empty;
        SetBusy(true);
        _lastUserText = text;

        // 重新生成时不补提问气泡 —— 那一条本来就在界面上
        if (appendUser) HubAiMessages.Children.Add(MakeBubble(text, alignRight: true));

        // 一旦开始对话，欢迎屏收起来、本机状态露出来（2026-10-20：本机状况等聊过再显示）
        WelcomePanel.Visibility = Visibility.Collapsed;
        SidePanel.Visibility = Visibility.Visible;

        // 这一轮的气泡：运行时有变化就往它里面刷（切走再回来也能接上）
        var bubble = NewAssistantBubble();
        _liveBubble = bubble;

        // ⚠️ 先把"这条消息属于哪个运行时、哪一段存档"钉住（2026-10-23 修）：
        //    下面采集本机概况是 await 的，几百毫秒里用户完全可能已经切到别的会话；
        //    等回来再读页面上的 `_runtime` / `_currentSessionId`，消息就启动到别人身上了。
        var rt = _runtime;
        var sessionId = _currentSessionId;

        var snapshot = _snapshot ?? await MachineProbe.CollectAsync();
        _snapshot = snapshot;

        // 记下"这一轮开始前上下文有多少条"：收尾时多出来的部分才是这一轮新产生的节点
        var before = rt.Chat.Snapshot().Count;

        // ⛔⛔ 2026-10-23（方案 B）：不 await、不等结果 —— 交给运行时在后台跑。
        //    页面切走 / 切会话都不影响它跑完；回来重新订阅 Changed 就能看到实时内容。
        rt.Start(
            cfg,
            snapshot,
            LoadTools(),
            text,
            onCompleted: doneUsage => OnUi(() => FinishRun(rt, sessionId, forkParentId, before, bubble, doneUsage)),
            onFailed: ex => OnUi(() => FailRun(rt, sessionId, forkParentId, before, bubble, ex)),
            appendUserMessage: appendUser,
            approveTool: ApproveToolAsync);
    }

    /// <summary>
    /// 一轮问答收尾（成功 / 用户停止都走这里）。
    /// ⚠️ 单独抽出来是因为 `HubAiRuntime.Start` 是**不等待**的：原来的 try/finally 结构不再成立，
    ///    收尾动作得由回调来触发。
    /// <para>
    /// ⚠️ `rt` / `sessionId` / `bubble` 都是**发起时**钉住的，⛔ 不能改成读页面字段
    ///    （2026-10-23 修）：这一轮可能在后台跑完，那时用户早就切走了，
    ///    读 `_runtime` / `_currentSessionId` 会把结果存进别的会话、还会往别人的气泡上写。
    /// </para>
    /// </summary>
    /// <param name="forkParentId">这一轮的新节点要挂到哪个父节点下（null = 从根起，即新开一支）。</param>
    /// <param name="before">这一轮开始前上下文里的消息条数 —— 之后多出来的才是新产生的节点。</param>
    /// <param name="emptyText">一个 token 都没收到时写进气泡的占位文字；失败路径传 null（错误信息已经写上）。</param>
    private void FinishRun(HubAiRuntime rt, string? sessionId, string? forkParentId, int before,
                           Bubble? bubble, AiUsage usage, string? emptyText = "（已停止）")
    {
        // 只有页面还停在这条运行时上，才允许动界面
        var onScreen = ReferenceEquals(rt, _runtime);

        if (onScreen)
        {
            ShowStatus(null);
            RenderSoftwareHits();
            SetBusy(false);
        }

        var body = rt.LiveBody;

        if (onScreen && bubble is not null)
        {
            // 一个 token 都没收到就结束了：把"已停止"写在气泡里，别留个空胶囊
            if (string.IsNullOrWhiteSpace(body))
            {
                if (emptyText is not null) MarkdownText.Apply(bubble.Body, emptyText, bubble.BodyForeground);
            }
            else
            {
                // ⚠️ 最后那一小段增量可能被 PaintLive 的 80ms 节流吃掉 —— 收尾补一次全量重绘
                MarkdownText.Apply(bubble.Body, body, bubble.BodyForeground);
            }

            bubble.Box.Visibility = Visibility.Visible;
            FinishBubble(bubble, usage, body);

            if (ReferenceEquals(_liveBubble, bubble)) _liveBubble = null;
        }

        PersistSession(rt, sessionId, forkParentId, before, usage);
    }

    /// <summary>请求失败：先把错误写清楚，再走统一的收尾（存档它已经生成的半截内容）。</summary>
    private void FailRun(HubAiRuntime rt, string? sessionId, string? forkParentId, int before,
                         Bubble bubble, Exception ex)
    {
        AppLog.Exception("hubai", ex, "对话请求");

        var onScreen = ReferenceEquals(rt, _runtime);

        if (onScreen)
        {
            AddNotice("请求失败：" + ex.Message);

            // 一个字都没生成出来 → 把错误顶在气泡里，否则那个气泡是空的
            if (string.IsNullOrWhiteSpace(rt.LiveBody))
                MarkdownText.Apply(bubble.Body, ex.Message, bubble.BodyForeground);
        }

        FinishRun(rt, sessionId, forkParentId, before, bubble, AiUsage.Empty, emptyText: null);
    }

    /// <summary>启用的工具。总闸关着、或工具包没装，都返回空 —— 模型于是只能看着体检单回答。</summary>
    private static IReadOnlyList<ToolDefinition> LoadTools()
    {
        // 联网工具与「工具总闸」**互相独立**：
        // 总闸关着只要用户把「联网」拨上就能用（Nick 要求跟「思考」一样就能开）。
        var tools = new List<ToolDefinition>();

        if (App.Settings.Current.HubAiToolsEnabled)
        {
            var pack = ToolPackStore.Load();
            if (pack?.Tools is { Count: > 0 } packTools) tools.AddRange(packTools);
        }

        if (App.Settings.Current.HubAiWebEnabled) tools.AddRange(BuiltinTools.Web);

        return tools;
    }

    /// <summary>当前是否已经有一个授权框在等（同一时刻只允许一个）。</summary>
    private bool _approvalOpen;

    /// <summary>
    /// 会改动系统的工具执行前的确认框（2026-10-23 加）。
    /// <para>
    /// ⛔ 默认动作是**拒绝**（<c>DefaultButton = Close</c>，Esc / 回车都不会误放行）。
    /// ⚠️ 运行时在后台线程回调，这里必须先切回 UI 线程再弹窗。
    /// </para>
    /// </summary>
    /// <returns>true = 用户允许。</returns>
    private Task<bool> ApproveToolAsync(string id, string arguments)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        OnUi(async () =>
        {
            if (_approvalOpen)
            {
                tcs.TrySetResult(false);
                return;
            }

            _approvalOpen = true;

            try
            {
                var detail = string.IsNullOrWhiteSpace(arguments) ? "（无参数）" : arguments.Trim();
                if (detail.Length > 400) detail = detail[..400] + "…";

                var content = new StackPanel { Spacing = 8 };

                content.Children.Add(new TextBlock
                {
                    Text = $"助手要执行「{id}」，这个操作会改动本机状态。",
                    TextWrapping = TextWrapping.Wrap,
                });

                content.Children.Add(new TextBlock
                {
                    Text = detail,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    FontSize = 12,
                    Opacity = 0.75,
                    TextWrapping = TextWrapping.Wrap,
                });

                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "允许这次操作？",
                    Content = content,
                    PrimaryButtonText = "允许",
                    CloseButtonText = "拒绝",
                    DefaultButton = ContentDialogButton.Close,
                };

                var result = await dialog.ShowAsync();
                tcs.TrySetResult(result == ContentDialogResult.Primary);
            }
            catch (Exception ex)
            {
                AppLog.Exception("hubai", ex, "工具授权对话框");
                tcs.TrySetResult(false);
            }
            finally
            {
                _approvalOpen = false;
            }
        });

        return tcs.Task;
    }

    /// <summary>一个 AI 气泡的三块内容：思考过程（可折叠）、正文、外框。</summary>
    /// <summary>
    /// 一个 AI 回合（2026-10-19 改成 WorkBuddy 式纯文本流）。
    ///
    /// <para>
    /// ⚠️ AI 内容**不再用聊天气泡**（Nick：「AI 生成的信息，我们就不用那种聊天气泡的方式了」）——
    /// 没有底色、没有边框，直接铺在页面上；用户消息仍然保留气泡（右对齐），两类信息一眼分得开。
    /// </para>
    /// <para>
    /// 「已完成」= 一个回合的全程：第一段思考 → 调用工具 → 继续思考 → 出正文。
    /// 状态行默认收起，点开能看到每一步；每段「深度思考」还能再展开。
    /// </para>
    /// </summary>
    private sealed class Bubble
    {
        public Border Box = null!;
        public TextBlock Status = null!;      // 「思考中」/「已完成 3m26s」
        public StackPanel Steps = null!;      // 思考段 + 工具条目

        /// <summary>
        /// 正文容器。
        /// ⚠️ 2026-10-23 从 <see cref="RichTextBlock"/> 换成 <see cref="StackPanel"/> ——
        ///    表格要真的画成 Grid，而 RichTextBlock 里塞不进 Grid（表格只会挤成一行竖线）。
        /// </summary>
        public StackPanel Body = null!;

        /// <summary>正文颜色。StackPanel 没有 Foreground 可继承，渲染时要显式传下去。</summary>
        public Microsoft.UI.Xaml.Media.Brush BodyForeground = null!;

        public StackPanel MetaActions = null!;
        public TextBlock MetaText = null!;     // 末尾元数据行（模型 · token · 参考价）

        /// <summary>思考段的折叠小标题（2026-10-23 起不再用 Expander，见 MakeCollapsible）。</summary>
        public Button? ThinkToggle;
        public FontIcon? ThinkChevron;

        /// <summary>折叠块的标题文字（有工具调用时改成「深度思考 · N 次工具调用」）。</summary>
        public TextBlock? ThinkCaption;

        /// <summary>折叠块的内容容器：思考段落与工具调用行按发生顺序排。</summary>
        public StackPanel? ThinkBody;

        /// <summary>**当前这一段**思考的文字块（工具调用之后就换新的一段）。</summary>
        public TextBlock? ThinkText;

        /// <summary>这一回合调了几次工具。</summary>
        public int ToolSteps;

        public readonly StringBuilder ThinkBuffer = new();

        public readonly Stopwatch Clock = Stopwatch.StartNew();

        /// <summary>入场动画只播一次（首个 token 到达、气泡真正可见的那一刻）。</summary>
        public bool EnterPlayed;

        public bool IsEmpty => Body.Children.Count == 0 && ThinkBuffer.Length == 0;
    }

    /// <summary>动态气泡用的一套颜色。</summary>
    /// <remarks>
    /// ⛔⛔ **代码里取主题色一律走 <see cref="ThemeBrush"/>**，别查 <c>Application.Current.Resources</c>：
    /// 后者认的是**应用级**主题（跟着系统定死那个），而本应用的主题是设在 <c>MainWindow.RootGrid</c> 上的，
    /// 两者不一致时（应用浅色 + 系统深色）就会刷出反色的文字。
    /// </remarks>
    private (Brush Background, Brush Border, Brush Text, Brush SubText) BubblePalette()
        => (ThemeBrush.Get(this, "CardBackgroundFillColorDefault"),
            ThemeBrush.Get(this, "CardStrokeColorDefault"),
            ThemeBrush.Get(this, "TextFillColorPrimary"),
            ThemeBrush.Get(this, "TextFillColorSecondary"));

    /// <summary>
    /// 新建一个 AI 回合的外壳。
    /// ⚠️ 先 Collapsed：从建出来到第一个 token 之间是空的，直接显示会留下一块空白。
    /// </summary>
    private Bubble NewAssistantBubble()
    {
        var palette = BubblePalette();

        // ── 头部：图标 + 名字 + 状态行 ──
        var headIcon = new FontIcon
        {
            Glyph = "\uE8F2",
            FontSize = 14,
            Foreground = palette.Text,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var nameText = new TextBlock
        {
            Text = "Hub AI",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = palette.Text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0),
        };

        var status = new TextBlock
        {
            Text = "思考中",
            FontSize = 12.5,
            Foreground = palette.SubText,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(headIcon);
        head.Children.Add(nameText);
        head.Children.Add(status);

        // ── 步骤区：深度思考（可展开）+ 工具条目 ──
        var steps = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 10) };

        // ── 正文 ──
        // ⚠️ StackPanel 而不是 RichTextBlock：表格要真正的 Grid（见 MarkdownText 的 StackPanel 重载）。
        //    字号/行高/颜色在这里定，渲染时按参数传给每个文本块。
        var body = new StackPanel { Spacing = 0 };

        // ── 末尾元数据：模型 · token · 参考价 · 耗时，右側跟复制/重试图标 ──
        // ⚠️ 2026-10-19 从"左侧独立栏"改成"正文末尾"：WorkBuddy 就是把这一排放在正文下方
        //    （Nick 指出「人家是放在文本末尾的左边啊」）。
        var metaText = new TextBlock
        {
            FontSize = 14,
            Foreground = palette.SubText,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };

        var metaActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 2026-10-20：复制/重试**靠左**（跟 ChatGPT 那类一致，操作紧跟正文末尾），
        // 模型与用量文字靠右；图标与字号都放大一档，太小了点不着。
        var metaRow = new Grid { Margin = new Thickness(0, 10, 0, 0), ColumnSpacing = 12 };
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metaRow.Children.Add(metaActions);
        Grid.SetColumn(metaText, 1);
        metaRow.Children.Add(metaText);

        var content = new StackPanel();
        content.Children.Add(head);
        content.Children.Add(steps);
        content.Children.Add(body);
        content.Children.Add(metaRow);

        var box = new Border
        {
            Child = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0, 4, 0, 12),
            Visibility = Visibility.Collapsed,
        };

        HubAiMessages.Children.Add(box);
        ScrollToBottom();

        return new Bubble
        {
            Box = box,
            Status = status,
            Steps = steps,
            Body = body,
            BodyForeground = palette.Text,
            MetaActions = metaActions,
            MetaText = metaText,
            ThinkText = null!,
        };
    }

    /// <summary>
    /// 一个回合收尾：状态行改成「已完成 Ns」、左列填上模型与用量、挂上复制/重试图标。
    /// ⚠️ 状态行可点 —— 点开能看到这一回合的思考与工具调用全过程。
    /// </summary>
    private void FinishBubble(Bubble bubble, AiUsage usage, string markdown)
    {
        var palette = BubblePalette();

        bubble.Status.Text = $"已完成 {bubble.Clock.Elapsed.TotalSeconds:0}s";
        bubble.Status.PointerPressed += (_, _) =>
        {
            bubble.Steps.Visibility = bubble.Steps.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        };
        ToolTipService.SetToolTip(bubble.Status, "展开或收起本回合的思考与工具调用");

        // 没有步骤内容就别留一个空行
        if (bubble.Steps.Children.Count == 0) bubble.Steps.Visibility = Visibility.Collapsed;

        var modelName = AiModelStore.Active?.Name ?? App.Settings.Current.HubAiModel;
        var cost = EstimateCost(usage);

        // ⚠️ 不要把「已完成 Ns」再写一遍 —— 上面那行状态里已经有了（截图里两处重复，很啰嗦）
        bubble.MetaText.Text =
            $"{modelName} · {usage.TotalTokens:N0} tokens" + (cost.Length > 0 ? $" · {cost}" : "");

        bubble.MetaActions.Children.Add(MakeIconButton("\uE8C8", "复制", palette.SubText, () => CopyText(markdown)));
        bubble.MetaActions.Children.Add(MakeIconButton("\uE72C", "重试", palette.SubText, RetryLast));
    }

    /// <summary>元数据行里的图标按钮（复制 / 重试）。⛔ 不用文字按钮 —— 会跟正文抢注意力。</summary>
    private Button MakeIconButton(string glyph, string tooltip, Brush foreground, Action onClick)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 16, Foreground = foreground },
            Padding = new Thickness(8, 6, 8, 6),
            Background = null,
            BorderThickness = new Thickness(0),
        };

        ToolTipService.SetToolTip(button, tooltip);
        button.Click += (_, _) => onClick();

        return button;
    }

    /// <summary>本轮工具命中过的软件，渲染成可点的卡片，点进详情页。</summary>
    private void RenderSoftwareHits()
    {
        var hits = _runtime.Chat.Session.SoftwareHits;
        if (hits.Count == 0) return;

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = "相关软件",
            FontSize = 12,
            Opacity = 0.6,
        });

        foreach (var hit in hits)
        {
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 9, 12, 9),
                Content = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = hit.Name,
                            FontSize = 13,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        },
                        new TextBlock
                        {
                            Text = hit.Tagline,
                            FontSize = 11.5,
                            Opacity = 0.7,
                            TextWrapping = TextWrapping.Wrap,
                        },
                    },
                },
            };

            var id = hit.Id;
            button.Click += (_, _) => Frame.Navigate(typeof(DetailPage), id);
            panel.Children.Add(button);
        }

        HubAiMessages.Children.Add(panel);
        ScrollToBottom();
    }

    private void AddNotice(string text)
        => HubAiMessages.Children.Add(MakeBubble(text, alignRight: false));

    /// <summary>上一次提问的原文，「重试」用它重发。</summary>
    private string? _lastUserText;

    /// <summary>
    /// 回答下方的脚注：用的哪个模型、花了多少、以及复制 / 重试。
    /// ⚠️ 模型名要显示出来 —— 用户切过模型后，光看回答分不出是谁答的。
    /// </summary>
    /// <summary>复制到剪贴板。复制的是**原始 Markdown**，粘到别处还能保住格式。</summary>
    private static void CopyText(string text)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            AppLog.Exception("hubai", ex, "复制回答");
        }
    }

    private void RetryLast()
    {
        if (_busy || string.IsNullOrEmpty(_lastUserText)) return;

        HubAiInput.Text = _lastUserText;
        _ = SendAsync();
    }


    /// <summary>
    /// 花费估算。
    /// ⚠️ 单价是**参考价**，服务商随时会调，这里只用于让用户对量级有个概念，⛔ 不能当账单。
    /// 单位：元 / 百万 tokens。
    /// </summary>
    private static string EstimateCost(AiUsage usage)
    {
        var (inputRate, outputRate) = App.Settings.Current.HubAiProvider switch
        {
            "deepseek" => (2.0, 8.0),
            "tongyi" => (0.8, 2.0),
            "zhipu" => (0.0, 0.0),    // glm-4-flash 免费档
            "local" => (0.0, 0.0),
            _ => (2.0, 8.0),
        };

        if (inputRate <= 0 && outputRate <= 0) return "本地或免费额度";

        var cost = usage.PromptTokens / 1_000_000.0 * inputRate
                 + usage.CompletionTokens / 1_000_000.0 * outputRate;

        return cost < 0.01 ? "约 ¥0.01 以内（参考价）" : $"约 ¥{cost:0.0000}（参考价）";
    }

    private void ShowStatus(string? status)
    {
        ChatStatus.Text = status ?? string.Empty;
        ChatStatus.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;

        HubAiInput.IsEnabled = !busy;
        SendIcon.Glyph = busy ? "\uE711" : "\uE724";   // 停止 / 发送
        ToolTipService.SetToolTip(SendButton, busy ? "停止" : "发送");

        ShowStatus(busy ? "正在思考" : null);

        if (!busy) HubAiInput.Focus(FocusState.Programmatic);
    }

    /// <summary>贴底才跟随滚动。用户往上翻的时候不能硬拽回底部。</summary>
    private void ScrollToBottom()
    {
        if (ChatScroll.ScrollableHeight - ChatScroll.VerticalOffset > 120) return;
        ChatScroll.ChangeView(null, ChatScroll.ScrollableHeight, null, true);
    }

    private void OnUi(Action action)
    {
        if (DispatcherQueue.HasThreadAccess) action();
        else DispatcherQueue.TryEnqueue(() => action());
    }

    /// <summary>
    /// 造一条消息气泡。背景走系统的次级填充画刷，不另立品牌色 ——
    /// 气泡本来就该跟系统"次级填充"一致（和软件下载页浮层里的那份实现保持一致）。
    /// </summary>
    private Border MakeBubble(string text, bool alignRight)
    {
        var box = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 11, 14, 11),
            MaxWidth = 620,
            HorizontalAlignment = alignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            BorderThickness = new Thickness(1),
            // 用户气泡打个标记 —— 「↑ 编辑重发」要靠它把最后一轮从消息区摘掉
            Tag = alignRight ? "hubai-user" : null,
        };

        var label = new TextBlock
        {
            Text = text,
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 25,
        };

        if (alignRight)
        {
            // 用户消息：强调色底 + **强调色背景上的文字色**。
            // ⛔ 别用 ThemeBrush.AccentText —— 那是"把强调色当文字颜色"（链接色），
            //    叠在强调色背景上对比度极低，深色下就是一片糊。
            box.Background = ThemeBrush.Accent(this);
            label.Foreground = ThemeBrush.Get(this, "TextOnAccentFillColorPrimary");
            box.BorderThickness = new Thickness(0);
        }
        else
        {
            // 提示条：与 AI 气泡同一套配色，保证浅深两色下都读得清
            var palette = BubblePalette();
            box.Background = palette.Background;
            box.BorderBrush = palette.Border;
            label.Foreground = palette.Text;
        }

        box.Child = label;
        PlayEnter(box);
        return box;
    }
}
