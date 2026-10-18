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
        ApplyEnabledState();
        LoadSessions();
        LoadPickers();

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

    private void LoadSessions()
    {
        _sessions = ChatSessionStore.LoadAll();

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
    }

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        _chat.Reset();
        _currentSessionId = null;

        _switchingSession = true;
        try { SessionList.SelectedItem = null; }
        finally { _switchingSession = false; }

        ClearConversation();
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

        if (_currentSessionId == id)
            NewChat_Click(sender, e);

        LoadSessions();
        AppLog.Step("hubai", "删除历史对话");
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switchingSession) return;
        if (SessionList.SelectedItem is not ChatSession session) return;

        _currentSessionId = session.Id;

        // 恢复时只回放文本轮次（存档里本来也只有这些）
        _chat.Restore(session.Entries.Select(entry => entry.Role == "user"
            ? AiMessage.User(entry.Content)
            : AiMessage.Assistant(entry.Content)));

        ClearConversation();

        foreach (var entry in session.Entries)
        {
            if (entry.Role == "user")
            {
                HubAiMessages.Children.Add(MakeBubble(entry.Content, alignRight: true));
            }
            else
            {
                var bubble = NewAssistantBubble();
                bubble.Box.Visibility = Visibility.Visible;

                // 思考过程也要复原 —— 它是对答的一部分，之前不存所以翻历史时整块消失
                if (!string.IsNullOrEmpty(entry.Reasoning))
                {
                    bubble.Think.Visibility = Visibility.Visible;
                    bubble.ThinkText.Text = entry.Reasoning;
                }

                MarkdownText.Apply(bubble.Body, entry.Content);
            }
        }

        if (!string.IsNullOrEmpty(session.Model) && session.Tokens > 0)
            AddHistoryFooter(session.Model, session.Tokens);

        ScrollToBottom();
    }

    /// <summary>清掉对话气泡，**保留开头那两张卡**（本机概况 / 初步判断）。</summary>
    private void ClearConversation()
    {
        while (HubAiMessages.Children.Count > 2)
            HubAiMessages.Children.RemoveAt(HubAiMessages.Children.Count - 1);
    }

    /// <summary>回答结束后存档。⛔ 没等到回答（比如刚发就取消）不存，免得列表里堆半截空对话。</summary>
    private void PersistSession(AiUsage usage)
    {
        var snapshot = _chat.Snapshot();
        if (snapshot.Count == 0) return;
        if (snapshot.All(m => m.Role != "assistant")) return;

        var firstUser = snapshot.FirstOrDefault(m => m.Role == "user")?.Content ?? "未命名对话";

        _currentSessionId = ChatSessionStore.Save(
            snapshot,
            firstUser,
            _currentSessionId,
            App.Settings.Current.HubAiModel,
            usage.TotalTokens);

        LoadSessions();
    }

    /// <summary>恢复历史时，把这次对话的模型与累计用量也补在底下。</summary>
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
        if (_busy)
        {
            _streamCts?.Cancel();
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

    private readonly AiChatService _chat = new();
    private CancellationTokenSource? _streamCts;
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
    private async Task SendAsync()
    {
        var text = HubAiInput.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

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

        HubAiInput.Text = string.Empty;
        SetBusy(true);
        _lastUserText = text;

        HubAiMessages.Children.Add(MakeBubble(text, alignRight: true));

        var buffer = new StringBuilder();
        var bubble = NewAssistantBubble();
        var lastFlush = 0L;

        void Flush(bool force = false)
        {
            var now = Environment.TickCount64;
            if (!force && now - lastFlush < 80) return;

            lastFlush = now;

            // 正文或思考任一有内容就让气泡现身（否则会留一个空胶囊）
            if (buffer.Length > 0 || bubble.ThinkBuffer.Length > 0)
                bubble.Box.Visibility = Visibility.Visible;

            MarkdownText.Apply(bubble.Body, buffer.ToString());
            ScrollToBottom();
        }

        _streamCts = new CancellationTokenSource();

        // 在 try 外面声明：finally 里存档要用（累计用量要写进历史）
        var usage = AiUsage.Empty;

        try
        {
            var snapshot = _snapshot ?? await MachineProbe.CollectAsync();
            _snapshot = snapshot;

            usage = await _chat.RunAsync(
                cfg,
                snapshot,
                LoadTools(),
                text,
                onDelta: chunk => OnUi(() =>
                {
                    buffer.Append(chunk);
                    Flush();
                }),
                onStatus: status => OnUi(() => ShowStatus(status)),
                onNewBubble: () => OnUi(() =>
                {
                    Flush(force: true);
                    ShowStatus(null);
                    buffer.Clear();
                    bubble = NewAssistantBubble();
                }),
                ct: _streamCts.Token,
                onReasoning: chunk => OnUi(() =>
                {
                    // 思考过程中气泡必须先现身 —— 有些模型要"想"几十秒才吐第一个正文 token
                    if (bubble.Think.Visibility != Visibility.Visible)
                        bubble.Think.Visibility = Visibility.Visible;

                    bubble.ThinkBuffer.Append(chunk);
                    bubble.ThinkText.Text = bubble.ThinkBuffer.ToString();
                    bubble.Box.Visibility = Visibility.Visible;

                    ScrollToBottom();
                }),
                onToolDone: note => OnUi(() => AddToolNote(note)));

            Flush(force: true);
            if (!usage.IsEmpty) AddUsageLine(usage, buffer.ToString());
        }
        catch (OperationCanceledException)
        {
            Flush(force: true);
            if (buffer.Length == 0)
            {
                MarkdownText.Apply(bubble.Body, "（已停止）");
                bubble.Box.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("hubai", ex, "对话请求");
            Flush(force: true);
            if (buffer.Length == 0)
            {
                MarkdownText.Apply(bubble.Body, ex.Message);
                bubble.Box.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            ShowStatus(null);
            RenderSoftwareHits();
            SetBusy(false);

            _streamCts?.Dispose();
            _streamCts = null;

            PersistSession(usage);
        }
    }

    /// <summary>启用的工具。总闸关着、或工具包没装，都返回空 —— 模型于是只能看着体检单回答。</summary>
    private static IReadOnlyList<ToolDefinition> LoadTools()
    {
        if (!App.Settings.Current.HubAiToolsEnabled) return Array.Empty<ToolDefinition>();

        var pack = ToolPackStore.Load();
        return pack?.Tools ?? (IReadOnlyList<ToolDefinition>)Array.Empty<ToolDefinition>();
    }

    /// <summary>一个 AI 气泡的三块内容：思考过程（可折叠）、正文、外框。</summary>
    private sealed class Bubble
    {
        public Border Box = null!;
        public RichTextBlock Body = null!;
        public Expander Think = null!;
        public TextBlock ThinkText = null!;
        public readonly StringBuilder ThinkBuffer = new();

        public bool IsEmpty => Body.Blocks.Count == 0 && ThinkBuffer.Length == 0;
    }

    /// <summary>
    /// 流式文本落进的新气泡。
    /// ⚠️ 用 <see cref="RichTextBlock"/> 而不是 TextBlock —— 模型吐的是 Markdown，
    ///    纯 TextBlock 会把 <c>**粗体**</c> 和 <c>- </c> 原样显示出来。
    /// </summary>
    /// <summary>动态气泡用的一套颜色。</summary>
    /// <remarks>
    /// ⛔⛔ **代码里取主题色一律走 <see cref="ThemeBrush"/>**，别查 <c>Application.Current.Resources</c>：
    /// 后者认的是**应用级**主题（跟着系统定死那个），而本应用的主题是设在 <c>MainWindow.RootGrid</c> 上的，
    /// 两者不一致时（应用浅色 + 系统深色）就会刷出反色的文字。
    /// <para>
    /// 这个坑连踩两轮（先深底黑字、再浅色白字），最后发现项目里早有 <c>ThemeBrush</c> 专门解决它。
    /// </para>
    /// </remarks>
    private (Brush Background, Brush Border, Brush Text, Brush SubText) BubblePalette()
        => (ThemeBrush.Get(this, "CardBackgroundFillColorDefault"),
            ThemeBrush.Get(this, "CardStrokeColorDefault"),
            ThemeBrush.Get(this, "TextFillColorPrimary"),
            ThemeBrush.Get(this, "TextFillColorSecondary"));

    private Bubble NewAssistantBubble()
    {
        var palette = BubblePalette();

        var thinkText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
            Foreground = palette.SubText,
        };

        // 思考过程用系统 Expander：默认折叠，流式时有内容才露出来
        var think = new Expander
        {
            Header = "思考过程",
            Content = thinkText,
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = Visibility.Collapsed,
            Foreground = palette.Text,
        };

        var body = new RichTextBlock
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
            Foreground = palette.Text,
        };

        var stack = new StackPanel();
        stack.Children.Add(think);
        stack.Children.Add(body);

        var box = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 11, 14, 11),
            MaxWidth = 620,
            HorizontalAlignment = HorizontalAlignment.Left,
            BorderThickness = new Thickness(1),
            Child = stack,
            Background = palette.Background,
            BorderBrush = palette.Border,

            // ⚠️ 先藏起来。换气泡时新建的这个框在拿到第一个 token 之前是空的，
            //    直接显示就会出现一个没有任何文字的深色胶囊。
            Visibility = Visibility.Collapsed,
        };

        HubAiMessages.Children.Add(box);
        ScrollToBottom();

        return new Bubble { Box = box, Body = body, Think = think, ThinkText = thinkText };
    }

    /// <summary>本轮工具命中过的软件，渲染成可点的卡片，点进详情页。</summary>
    private void RenderSoftwareHits()
    {
        var hits = _chat.Session.SoftwareHits;
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
    private void AddUsageLine(AiUsage usage, string markdown)
    {
        var palette = BubblePalette();

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 4, 0),
        };

        var cost = EstimateCost(usage);
        var modelName = AiModelStore.Active?.Name ?? App.Settings.Current.HubAiModel;
        var label = $"{modelName} · {usage.TotalTokens:N0} tokens"
                    + (cost.Length > 0 ? $" · {cost}" : "");

        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = palette.SubText,
            VerticalAlignment = VerticalAlignment.Center,
        });

        row.Children.Add(MakeFooterButton("复制", palette.SubText, () => CopyText(markdown)));
        row.Children.Add(MakeFooterButton("重试", palette.SubText, RetryLast));

        HubAiMessages.Children.Add(row);
        ScrollToBottom();
    }

    private Button MakeFooterButton(string text, Brush foreground, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 11,
            Padding = new Thickness(8, 1, 8, 2),
            Foreground = foreground,
            VerticalAlignment = VerticalAlignment.Center,
        };

        button.Click += (_, _) => onClick();
        return button;
    }

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
    /// 工具调用留下的痕迹，插在两段回答之间。
    /// ⚠️ 没有它的话，模型"说一句 → 调工具 → 再说一句"看起来就是对话突然断掉又接上（Nick 2026-10-18 指出）。
    /// </summary>
    private void AddToolNote(string text)
    {
        var palette = BubblePalette();

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Opacity = 0.85,
            Margin = new Thickness(2, 0, 0, 0),
        };

        row.Children.Add(new FontIcon
        {
            Glyph = "\uE9D9",
            FontSize = 11,
            Foreground = palette.SubText,
            VerticalAlignment = VerticalAlignment.Center,
        });

        row.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            Foreground = palette.SubText,
            VerticalAlignment = VerticalAlignment.Center,
        });

        HubAiMessages.Children.Add(row);
        ScrollToBottom();
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
        };

        var label = new TextBlock
        {
            Text = text,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
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
        return box;
    }
}
