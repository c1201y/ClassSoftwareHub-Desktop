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




































public sealed partial class HubAiFlyout : UserControl
{
    private HubAiRuntime? _runtime;

    
    
    
    
    private string? _sessionId;

    
    private StackPanel? _answerPanel;

    
    private string _answerRendered = "";

    private bool _busy;

    
    
    
    
    
    
    
    private Border? _thinkingBubble;

    private ProgressRing? _thinkingRing;

    private TextBlock? _thinkingText;

    
    
    
    
    
    private DispatcherQueueTimer? _thinkingTimer;

    
    private long _thinkingSince;

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    public Func<PageContextDoc?>? ContextProvider { get; set; }

    public HubAiFlyout()
    {
        InitializeComponent();

        
        ApplySurface();
        ActualThemeChanged += (_, _) =>
        {
            ApplySurface();
            RepaintConversation();
        };
    }

    
    public bool IsOpen => Card.Visibility == Visibility.Visible;

    
    public void Show()
    {
        if (IsOpen)
        {
            Input.Focus(FocusState.Programmatic);
            return;
        }

        BeginConversation();

        Card.Visibility = Visibility.Visible;

        
        
        
        CardTransform.ScaleX = 0.96;
        CardTransform.ScaleY = 0.96;
        CardTransform.TranslateY = 8;
        Card.Opacity = 0;

        PlayAppear(show: true);
        Input.Focus(FocusState.Programmatic);
    }

    
    public void Hide()
    {
        if (!IsOpen) return;

        PlayAppear(show: false, onDone: () => Card.Visibility = Visibility.Collapsed);
    }

    
    public void Toggle()
    {
        if (IsOpen) Hide();
        else Show();
    }

    

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    
    
    
    
    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        App.MainWindow?.Shell.NavigateTo("hubai");
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        
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

    
    private void OnRuntimeChanged() => Dispatch(RenderLive);

    
    
    
    
    
    
    
    
    
    
    
    private void RenderLive()
    {
        if (_runtime is null) return;

        var body = _runtime.LiveBody;

        if (body.Length > 0) HideThinking();
        else if (_busy) ShowThinking();          

        ShowStatusLine(body.Length > 0 ? _runtime.LiveStatus : null);

        if (_answerPanel is null || body == _answerRendered) return;

        _answerRendered = body;
        MarkdownText.Apply(_answerPanel, body, ThemeBrush.Get(this, "TextFillColorPrimaryBrush"),
            fontSize: 12.5, lineHeight: 20);
        ScrollToEnd();
    }

    

    private async Task AskAsync()
    {
        if (_busy) return;

        var text = Input.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        
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

        
        var sessionId = _sessionId;

        
        var before = runtime.Chat.Snapshot().Count;

        
        var contextJson = BuildContextJson();

        Input.Text = string.Empty;
        Intro.Visibility = Visibility.Collapsed;

        AddBubble(text, mine: true);
        _answerPanel = AddBubble(string.Empty, mine: false);
        _answerRendered = "";

        _busy = true;
        SetBusyUi(true);

        
        
        ShowThinking();

        try
        {
            
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

    
    
    
    
    
    
    
    private void Finish(HubAiRuntime runtime, string? sessionId, int before, AiUsage usage, Exception? error)
    {
        
        
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
                
                if (current && _answerPanel is { Children.Count: 0 })
                    AddBubble("这一轮没有文字回答，可以换个问法。", mine: false);
                return;
            }

            var title = turns.FirstOrDefault(m => m.Role == "user")?.Content ?? "未命名对话";

            
            if (current) RenderActions(runtime);

            var savedId = ChatSessionStore.SaveBranch(
                sessionId,
                parentId: null,
                turns,
                title,
                App.Settings.Current.HubAiModel,
                usage.TotalTokens);

            
            
            if (string.IsNullOrEmpty(sessionId) && !string.IsNullOrEmpty(savedId))
                HubAiRuntime.Adopt(runtime.SessionId, savedId);

            if (savedId is { Length: > 0 })
            {
                
                if (current) _sessionId = savedId;

                App.Settings.Current.HubAiLastSessionId = savedId;
                App.Settings.Save();

                
                Pages.HubAiPage.PendingReloadId = savedId;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("hubai", "浮层存档失败：" + ex.Message);
        }
    }

    

    
    
    
    
    
    
    
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

    
    
    
    
    private void RunAction(AiAction action)
    {
        if (action.Kind == AiActionKind.Navigate) Hide();

        AiActionRunner.Run(action);
    }

    
    
    
    
    
    
    
    
    
    
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
            
            
            Messages.UpdateLayout();
            MessagesScroll.ChangeView(null, MessagesScroll.ScrollableHeight, null, disableAnimation: true);
        }
        catch { }
    }

    
    private void SetBusyUi(bool busy)
    {
        SendGlyph.Glyph = busy ? "\uE71A" : "\uE724";       
        ToolTipService.SetToolTip(SendButton, busy ? "停止生成" : "发送");
    }

    

    
    
    
    
    
    
    
    
    
    
    
    
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

    
    
    
    
    
    
    
    private void UpdateThinkingText()
    {
        if (_thinkingText is null) return;

        var status = _runtime?.LiveStatus;
        if (string.IsNullOrWhiteSpace(status) || status == "思考中") status = "正在思考";

        
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

        
        _thinkingTimer.Tick -= OnThinkingTick;
        _thinkingTimer.Tick += OnThinkingTick;
        _thinkingTimer.Start();
    }

    private void OnThinkingTick(DispatcherQueueTimer sender, object args) => UpdateThinkingText();

    
    
    
    
    private void ShowStatusLine(string? status)
    {
        var text = status ?? string.Empty;

        if (StatusLine.Text != text) StatusLine.Text = text;
        StatusLine.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    
    private void Dispatch(Action action)
    {
        var queue = DispatcherQueue;
        if (queue is null) return;

        if (queue.HasThreadAccess) action();
        else queue.TryEnqueue(() => action());
    }

    

    
    
    
    
    
    
    
    
    
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
