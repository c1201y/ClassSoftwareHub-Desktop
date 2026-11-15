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













public sealed partial class HubAiPage : Page
{
    public HubAiPage()
    {
        InitializeComponent();

        
        ActualThemeChanged += (_, _) => RepaintConversation();
    }

    
    
    
    
    
    
    
    
    
    
    
    private void RepaintConversation()
    {
        if (!_uiReady || string.IsNullOrEmpty(_currentSessionId)) return;

        
        
        _repaintSessionId = _currentSessionId;
        _repaintActions = _runtime.Chat.Session.Actions.ToList();
        _repaintAfterTheme = true;
        LoadSessions();
    }

    
    
    
    
    public static string? PendingQuestion;

    
    
    
    private const string NormalTitle = "Hub AI";
    private const string EggTitle = "Hui AI";
    private const int EggClickCount = 6;

    
    private const int EggClickWindowMs = 600;

    private int _eggClicks;
    private long _eggLastTick;

    private void Title_Tapped(object sender, TappedRoutedEventArgs e)
    {
        
        if (TitleText.Text == EggTitle) return;

        var now = Environment.TickCount64;
        if (now - _eggLastTick > EggClickWindowMs) _eggClicks = 0;
        _eggLastTick = now;

        if (++_eggClicks < EggClickCount) return;

        _eggClicks = 0;
        TitleText.Text = EggTitle;
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        
        
        TitleText.Text = NormalTitle;
        _eggClicks = 0;

        
        
        
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

        
        
        
        if (PendingReloadId is { Length: > 0 } reloadId)
        {
            PendingReloadId = null;
            DispatcherQueue.TryEnqueue(() => SelectSessionById(reloadId));
        }

        
        
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

        
        DispatcherQueue.TryEnqueue(() => _ = SendAsync());
    }

    
    
    

    private List<ChatSession> _sessions = new();

    
    
    
    
    
    
    
    
    public static string? PendingReloadId;

    
    private string? _currentSessionId;

    
    private bool _switchingSession;

    
    private bool _repaintAfterTheme;

    
    
    
    
    
    private List<AiAction>? _repaintActions;

    
    private string? _repaintSessionId;

    
    private bool _uiReady;

    
    
    
    
    
    
    
    
    private void SelectSessionById(string id)
    {
        try
        {
            _sessions = ChatSessionStore.LoadAll();
            SessionList.ItemsSource = _sessions;

            var match = _sessions.FirstOrDefault(x => x.Id == id);
            if (match is null)
            {
                Core.AppLog.Info("hubai", $"要切换的会话 {id} 不在列表里（可能已被删除）");
                return;
            }

            SessionList.SelectedItem = match;
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn("hubai", "切到指定会话失败：" + ex.Message);
        }
    }

    private void LoadSessions()
    {
        _sessions = ChatSessionStore.LoadAll();

        
        
        
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

        
        
        _editingFromHistory = false;

        _runtime.Reset();
        _currentSessionId = null;
        App.Settings.Current.HubAiLastSessionId = "";
        App.Settings.Save();

        _switchingSession = true;
        try { SessionList.SelectedItem = null; }
        finally { _switchingSession = false; }

        ClearConversation();
        WelcomePanel.Visibility = Visibility.Visible;   
        HubAiInput.Focus(FocusState.Programmatic);
    }

    
    
    
    
    
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

    
    private void DeleteSession_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string id) return;

        ChatSessionStore.Delete(id);

        
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

        
        if (_repaintAfterTheme && session.Id != _repaintSessionId)
        {
            _repaintAfterTheme = false;
            _repaintActions = null;
            _repaintSessionId = null;
        }

        
        
        
        BindRuntime(HubAiRuntime.For(session.Id));
        _liveBubble = null;

        
        
        _editingFromHistory = false;

        
        
        
        _currentSessionId = session.Id;
        App.Settings.Current.HubAiLastSessionId = session.Id;   
        App.Settings.Save();

        var running = _runtime.IsRunning;

        
        
        
        
        
        
        if (!running)
        {
            _runtime.Chat.Restore(session.Branch().Select(ToMessage));
        }

        ClearConversation();

        
        WelcomePanel.Visibility = Visibility.Collapsed;
        SidePanel.Visibility = Visibility.Visible;

        
        
        
        
        
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

            var head = entry;      
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

            
            
            if (mergedContent.Length == 0 && mergedReasoning.Length == 0) continue;

            var bubble = NewAssistantBubble();
            bubble.Box.Visibility = Visibility.Visible;

            
            if (mergedReasoning.Length > 0)
            {
                
                
                
                var think = MakeCollapsible("深度思考", null);
                think.Body.Children.Add(MakeThinkSegment(mergedReasoning.ToString()));
                bubble.Steps.Children.Add(think.Root);
            }

            bubble.Status.Text = "已完成";
            MarkdownText.Apply(bubble.Body, mergedContent.ToString(), bubble.BodyForeground);

            
            
            var histPalette = BubblePalette();
            var copyText = mergedContent.ToString();

            
            
            if (!string.IsNullOrWhiteSpace(session.Model))
            {
                var displayName = AiModelStore.All
                    .FirstOrDefault(m => string.Equals(m.Id, session.Model, StringComparison.OrdinalIgnoreCase))
                    ?.Name ?? session.Model;

                
                var cost = EstimateCost(new AiUsage(
                    session.Tokens * 7 / 10,
                    session.Tokens * 3 / 10,
                    session.Tokens));

                bubble.MetaText.Text = session.Tokens > 0
                    ? $"{displayName} · {session.Tokens:N0} tokens · {cost}"
                    : displayName;
            }

            bubble.MetaActions.Children.Add(MakeIconButton("\uE8C8", "复制", histPalette.SubText, () => CopyText(copyText)));

            
            
            bubble.MetaActions.Children.Add(MakeIconButton("\uE72C", "重试", histPalette.SubText, () => RetryFrom(head)));
            AddBranchSwitcher(session, head);
        }

        
        if (!running && !string.IsNullOrEmpty(session.Model) && session.Tokens > 0)
            AddHistoryFooter(session.Model, session.Tokens);

        if (running)
        {
            
            
            var asked = _runtime.Chat.Snapshot().LastOrDefault(m => m.Role == "user");
            if (asked?.Content is { Length: > 0 } question
                && _branch.LastOrDefault(x => x.Role == "user")?.Content != question)
            {
                HubAiMessages.Children.Add(MakeBubble(question, alignRight: true));
            }

            
            
            
            var resumed = NewAssistantBubble();
            _liveBubble = resumed;
            resumed.Box.Visibility = Visibility.Visible;
            SetBusy(true);
            PaintLive(force: true);
        }
        else if (_busy)
        {
            
            
            SetBusy(false);
        }

        ScrollToBottom();

        if (_repaintAfterTheme)
        {
            _repaintAfterTheme = false;

            
            if (_repaintActions is { Count: > 0 })
            {
                _runtime.Chat.Session.Actions.Clear();
                _runtime.Chat.Session.Actions.AddRange(_repaintActions);
            }

            _repaintActions = null;
            _repaintSessionId = null;
            RenderActions(_runtime);

            
            
            HubAiMessages.UpdateLayout();
            ChatScroll.ChangeView(null, ChatScroll.ScrollableHeight, null, disableAnimation: true);
        }
    }

    
    
    
    
    
    
    private static AiMessage ToMessage(SessionEntry entry) =>
        entry.Role == "user"
            ? AiMessage.User(entry.Content)
            : AiMessage.Assistant(entry.Content) with { Reasoning = entry.Reasoning };

    
    
    
    
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

        var prev = MakeArrow("\uE76B");   
        var next = MakeArrow("\uE76D");   

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

    
    
    
    
    private void SwitchBranch(ChatSession session, SessionEntry target)
    {
        ChatSessionStore.SetCurrent(session.Id, session.LeafOf(target.Id));
        LoadSessions();
        AppLog.Step("hubai", "切换历史分支");
    }

    
    
    
    
    private void RetryFrom(SessionEntry assistant)
    {
        if (_busy) return;

        var user = _branch.FirstOrDefault(e => e.Id == assistant.ParentId);
        if (user is null || user.Role != "user") return;

        ForkForRetry(user);
        HubAiInput.Text = user.Content;
        _ = SendAsync();
    }

    
    private void ForkForEdit(SessionEntry oldUser)
    {
        var index = _branch.FindIndex(e => e.Id == oldUser.Id);

        _forkPending = true;
        _forkAppendUser = true;
        _forkParentId = oldUser.ParentId;
        _forkKeep = index < 0 ? 0 : index;
    }

    
    private void ForkForRetry(SessionEntry user)
    {
        var index = _branch.FindIndex(e => e.Id == user.Id);

        _forkPending = true;
        _forkAppendUser = false;             
        _forkParentId = user.Id;
        _forkKeep = index < 0 ? _branch.Count : index + 1;
    }

    private void ClearConversation()
    {
        while (HubAiMessages.Children.Count > 1)
            HubAiMessages.Children.RemoveAt(HubAiMessages.Children.Count - 1);
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    private void PersistSession(HubAiRuntime rt, string? sessionId, string? forkParentId, int before, AiUsage usage)
    {
        var snapshot = rt.Chat.Snapshot();

        
        var turns = snapshot.Skip(before).ToList();
        if (turns.Count == 0) return;

        
        
        
        if (!turns.Any(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content))) return;

        var fallbackTitle = turns.FirstOrDefault(m => m.Role == "user")?.Content ?? "未命名对话";

        var savedId = ChatSessionStore.SaveBranch(
            sessionId,
            forkParentId,
            turns,
            fallbackTitle,
            App.Settings.Current.HubAiModel,
            usage.TotalTokens);

        
        
        
        if (string.IsNullOrEmpty(sessionId) && !string.IsNullOrEmpty(savedId))
            HubAiRuntime.AdoptDraft(savedId);

        
        if (savedId is { Length: > 0 } && ReferenceEquals(rt, _runtime))
        {
            _currentSessionId = savedId;
            App.Settings.Current.HubAiLastSessionId = savedId;
            App.Settings.Save();
        }

        LoadSessions();
    }

    
    
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

    
    
    
    
    private void ApplyEnabledState()
    {
        var on = App.Settings.Current.HubAiEnabled;

        
        
        
        
        var activeModel = AiModelStore.Active;
        var configured = activeModel is not null
                         && (activeModel.ProviderId == "local"
                             || SecretStore.Has(activeModel.EffectiveSecretId));

        DisabledPanel.Visibility = on ? Visibility.Collapsed : Visibility.Visible;

        
        
        NotWiredBar.IsOpen = on && !configured;
        NotWiredBar.Visibility = (on && !configured) ? Visibility.Visible : Visibility.Collapsed;

        ChatScroll.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ChatInput.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        
        
        if (on && !_snapshotLoaded)
        {
            _snapshotLoaded = true;
            LoadSnapshot();
        }
    }

    
    private bool _snapshotLoaded;

    
    
    
    
    private async void LoadSnapshot()
    {
        SnapshotLoading.IsActive = true;
        SnapshotLoading.Visibility = Visibility.Visible;
        SnapshotBody.Visibility = Visibility.Collapsed;
        SnapshotStatus.Text = "正在读取";

        try
        {
            var snap = await MachineProbe.CollectAsync();
            _snapshot = snap;   
            RenderSnapshot(snap);
            RenderFindings(MachineDiagnosis.Analyze(snap));

            
            
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

        
        var cpu = s.CpuName.Length == 0 ? "未取到" : s.CpuName;
        if (s.CpuName.Length > 0)
        {
            cpu += $" · {s.CpuThreads} 线程";

            
            
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

    
    private static string Size(long mb)
        => mb >= 1024 ? $"{mb / 1024.0:0.#} GB" : $"{mb} MB";

    
    
    
    
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

    
    
    

    
    private bool _loadingPickers;

    private void LoadPickers()
    {
        _loadingPickers = true;
        try
        {
            AiModelStore.EnsureMigrated();
            EnsureEffortItems();

            
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
        
        
        
        
        if (_busy)
        {
            _runtime.Stop();
            return;
        }

        _ = SendAsync();
    }

    
    
    
    
    
    
    
    
    
    
    
    
    private void HubAiInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        
        
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

    
    
    

    
    
    
    private HubAiRuntime _runtime = HubAiRuntime.For(null);

    
    private void BindRuntime(HubAiRuntime runtime)
    {
        if (ReferenceEquals(_runtime, runtime)) return;

        _runtime.Changed -= OnRuntimeChanged;
        _runtime.ToolDone -= OnToolDone;

        _runtime = runtime;

        _runtime.Changed += OnRuntimeChanged;
        _runtime.ToolDone += OnToolDone;
    }

    
    
    
    
    
    private void OnToolDone(string note) => OnUi(() =>
    {
        if (_liveBubble is null) return;
        AddToolStep(_liveBubble, note);
    });

    
    
    
    
    
    private void OnRuntimeChanged() => OnUi(() => PaintLive(force: false));

    
    private long _lastPaint;

    
    
    
    
    
    
    
    
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

    
    private Bubble? _liveBubble;

    
    private List<SessionEntry> _branch = new();

    
    
    
    
    
    
    
    private bool _forkPending;

    
    private string? _forkParentId;

    
    private int _forkKeep;

    
    private bool _forkAppendUser = true;

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private (StackPanel Root, StackPanel Body, FontIcon Chevron, Button Toggle, TextBlock Caption) MakeCollapsible(
        string title, string? subtitle)
    {
        var palette = BubblePalette();
        var chevron = new FontIcon
        {
            Glyph = "\uE76C",            
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
            Margin = new Thickness(16, 4, 0, 0),   
        };

        toggle.Click += (_, _) =>
        {
            var expand = body.Visibility != Visibility.Visible;
            body.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
            chevron.Glyph = expand ? "\uE70D" : "\uE76C";   
        };

        var root = new StackPanel { Spacing = 2 };
        root.Children.Add(toggle);
        root.Children.Add(body);
        return (root, body, chevron, toggle, caption);
    }

    
    private TextBlock MakeThinkSegment(string text) => new()
    {
        Text = text,
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 21,
        Foreground = BubblePalette().SubText,
    };

    
    
    
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
            Glyph = "\uE9D9",              
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

        
        bubble.ThinkText = null;
    }

    
    
    
    
    
    
    private bool _editingFromHistory;

    
    private SessionEntry? _editSource;

    private bool _busy;

    
    private MachineSnapshot? _snapshot;

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
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

    
    
    private void WebToggle_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Current.HubAiWebEnabled = WebToggle.IsChecked ?? false;
        App.Settings.Save();
    }

    private void HubAiInput_GotFocus(object sender, RoutedEventArgs e)
        => InputShell.BorderBrush = ThemeBrush.Get(this, "AccentFillColorDefault");

    private void HubAiInput_LostFocus(object sender, RoutedEventArgs e)
        => InputShell.BorderBrush = ThemeBrush.Get(this, "CardStrokeColorDefault");

    
    
    
    
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

        
        var secret = "";
        try { secret = SecretStore.Load(model.EffectiveSecretId); }
        catch { secret = ""; }

        var cfg = model.ToConfig(secret);

        if (!cfg.IsUsable)
        {
            AddNotice("当前模型的配置不完整。请到「设置 → Hub AI → 模型服务」填写服务地址与模型名称。");
            return;
        }

        if (secret.Length == 0 && model.ProviderId != "local")
        {
            AddNotice($"「{model.Name}」还没有配置 API Key，暂时无法对话。请到「设置 → Hub AI → 模型服务」补上密钥。");
            return;
        }

        
        
        if (_runtime.IsRunning)
        {
            AddNotice("这一条还在生成中，请等它结束，或先点停止。");
            return;
        }

        
        
        if (_editingFromHistory)
        {
            _editingFromHistory = false;
            if (_editSource is not null) ForkForEdit(_editSource);
            _editSource = null;
        }

        var forkParentId = _forkParentId;      
        var appendUser = _forkAppendUser;

        if (_forkPending)
        {
            _forkPending = false;
            _forkAppendUser = true;
            _forkParentId = null;

            
            var prefix = _forkKeep <= 0
                ? new List<SessionEntry>()
                : _branch.Take(_forkKeep).ToList();

            _runtime.Chat.Restore(prefix.Select(ToMessage));
        }

        HubAiInput.Text = string.Empty;
        SetBusy(true);
        _lastUserText = text;

        
        if (appendUser) HubAiMessages.Children.Add(MakeBubble(text, alignRight: true));

        
        WelcomePanel.Visibility = Visibility.Collapsed;
        SidePanel.Visibility = Visibility.Visible;

        
        var bubble = NewAssistantBubble();
        _liveBubble = bubble;

        
        
        
        var rt = _runtime;
        var sessionId = _currentSessionId;

        var snapshot = _snapshot ?? await MachineProbe.CollectAsync();
        _snapshot = snapshot;

        
        var before = rt.Chat.Snapshot().Count;

        
        
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

    
    
    
    
    
    
    
    
    
    
    
    
    
    private void FinishRun(HubAiRuntime rt, string? sessionId, string? forkParentId, int before,
                           Bubble? bubble, AiUsage usage, string? emptyText = "（已停止）")
    {
        
        var onScreen = ReferenceEquals(rt, _runtime);

        if (onScreen)
        {
            ShowStatus(null);
            RenderSoftwareHits();
            RenderActions(rt);
            SetBusy(false);
        }

        var body = rt.LiveBody;

        if (onScreen && bubble is not null)
        {
            
            if (string.IsNullOrWhiteSpace(body))
            {
                if (emptyText is not null) MarkdownText.Apply(bubble.Body, emptyText, bubble.BodyForeground);
            }
            else
            {
                
                MarkdownText.Apply(bubble.Body, body, bubble.BodyForeground);
            }

            bubble.Box.Visibility = Visibility.Visible;
            FinishBubble(bubble, usage, body);

            if (ReferenceEquals(_liveBubble, bubble)) _liveBubble = null;
        }

        PersistSession(rt, sessionId, forkParentId, before, usage);
    }

    
    private void FailRun(HubAiRuntime rt, string? sessionId, string? forkParentId, int before,
                         Bubble bubble, Exception ex)
    {
        AppLog.Exception("hubai", ex, "对话请求");

        var onScreen = ReferenceEquals(rt, _runtime);

        if (onScreen)
        {
            AddNotice("请求失败：" + ex.Message);

            
            if (string.IsNullOrWhiteSpace(rt.LiveBody))
                MarkdownText.Apply(bubble.Body, ex.Message, bubble.BodyForeground);
        }

        FinishRun(rt, sessionId, forkParentId, before, bubble, AiUsage.Empty, emptyText: null);
    }

    
    
    
    
    
    private static IReadOnlyList<ToolDefinition> LoadTools() => ActiveTools.Load();

    
    private bool _approvalOpen;

    
    
    
    
    
    
    
    
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

    
    
    
    
    
    
    
    
    
    
    
    
    
    private sealed class Bubble
    {
        public Border Box = null!;
        public TextBlock Status = null!;      
        public StackPanel Steps = null!;      

        
        
        
        
        
        public StackPanel Body = null!;

        
        public Microsoft.UI.Xaml.Media.Brush BodyForeground = null!;

        public StackPanel MetaActions = null!;
        public TextBlock MetaText = null!;     

        
        public Button? ThinkToggle;
        public FontIcon? ThinkChevron;

        
        public TextBlock? ThinkCaption;

        
        public StackPanel? ThinkBody;

        
        public TextBlock? ThinkText;

        
        public int ToolSteps;

        public readonly StringBuilder ThinkBuffer = new();

        public readonly Stopwatch Clock = Stopwatch.StartNew();

        
        public bool EnterPlayed;

        public bool IsEmpty => Body.Children.Count == 0 && ThinkBuffer.Length == 0;
    }

    
    
    
    
    
    
    private (Brush Background, Brush Border, Brush Text, Brush SubText) BubblePalette()
        => (ThemeBrush.Get(this, "CardBackgroundFillColorDefault"),
            ThemeBrush.Get(this, "CardStrokeColorDefault"),
            ThemeBrush.Get(this, "TextFillColorPrimary"),
            ThemeBrush.Get(this, "TextFillColorSecondary"));

    
    
    
    
    private Bubble NewAssistantBubble()
    {
        var palette = BubblePalette();

        
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

        
        var steps = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 10) };

        
        
        
        var body = new StackPanel { Spacing = 0 };

        
        
        
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

        
        if (bubble.Steps.Children.Count == 0) bubble.Steps.Visibility = Visibility.Collapsed;

        var modelName = AiModelStore.Active?.Name ?? App.Settings.Current.HubAiModel;
        var cost = EstimateCost(usage);

        
        bubble.MetaText.Text =
            $"{modelName} · {usage.TotalTokens:N0} tokens" + (cost.Length > 0 ? $" · {cost}" : "");

        bubble.MetaActions.Children.Add(MakeIconButton("\uE8C8", "复制", palette.SubText, () => CopyText(markdown)));
        bubble.MetaActions.Children.Add(MakeIconButton("\uE72C", "重试", palette.SubText, RetryLast));
    }

    
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

    
    
    
    
    
    
    
    private void RenderActions(HubAiRuntime rt)
    {
        var actions = rt.Chat.Session.Actions;
        if (actions.Count == 0) return;

        var panel = Controls.AiActionBar.Build(actions, Services.HubAi.AiActionRunner.Run);
        if (panel is null) return;

        HubAiMessages.Children.Add(panel);
        ScrollToBottom();
    }

    private void AddNotice(string text)
        => HubAiMessages.Children.Add(MakeBubble(text, alignRight: false));

    
    private string? _lastUserText;

    
    
    
    
    
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


    
    
    
    
    
    private static string EstimateCost(AiUsage usage)
    {
        var (inputRate, outputRate) = App.Settings.Current.HubAiProvider switch
        {
            "deepseek" => (2.0, 8.0),
            "tongyi" => (0.8, 2.0),
            "zhipu" => (0.0, 0.0),    
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
        SendIcon.Glyph = busy ? "\uE711" : "\uE724";   
        ToolTipService.SetToolTip(SendButton, busy ? "停止" : "发送");

        ShowStatus(busy ? "正在思考" : null);

        if (!busy) HubAiInput.Focus(FocusState.Programmatic);
    }

    
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

    
    
    
    
    private Border MakeBubble(string text, bool alignRight)
    {
        var box = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 11, 14, 11),
            MaxWidth = 620,
            HorizontalAlignment = alignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            BorderThickness = new Thickness(1),
            
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
            
            
            
            box.Background = ThemeBrush.Accent(this);
            label.Foreground = ThemeBrush.Get(this, "TextOnAccentFillColorPrimary");
            box.BorderThickness = new Thickness(0);
        }
        else
        {
            
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
