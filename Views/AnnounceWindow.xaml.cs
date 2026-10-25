using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml.Media.Animation;
using WinRT;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 全屏喊话窗（2026-10-25 第三版）。
/// <para>
/// 改动来源（Nick 提）：① 提示音换成他给的 wav，并允许接收端自选；② 出场方式做成预设
/// （打字机 / 每行交错划入 / 弹入 / 无动画兜底）；③ 署名不再带「说」；④ 有出场动画：
/// 文字先退场 → 变暗层再淡回去 → 关窗。
/// </para>
/// <para>
/// ⛔ 字号自适应必须用离屏探针量（树内直接 Measure 会拿到缓存值，见 FitText 注释）。
/// </para>
/// </summary>
public sealed partial class AnnounceWindow : Window
{
    private static AnnounceWindow? _current;

    /// <summary>播报中（轮询据此跳过出队，避免播放期间又拉一条）。</summary>
    public static bool IsShowing { get; private set; }

    private AppWindow? _appWindow;
    private readonly DispatcherQueueTimer _tick;
    private DispatcherQueueTimer? _typeTimer;
    private readonly int _durationSec;
    private int _remainSec;
    private readonly bool _quiet;
    private readonly bool _speak;
    private bool _exiting;

    private readonly List<string> _lineTexts = new();

    public static void Show(AnnounceMessage msg, bool quiet, bool speak)
    {
        if (_current is not null)
        {
            try
            {
                _current.Activate();
                return;
            }
            catch
            {
                _current = null;   // 窗口已销毁但字段没清，丢掉引用重新开
            }
        }

        var window = new AnnounceWindow(msg, quiet, speak);
        _current = window;
        window.Start();
    }

    /// <summary>
    /// 本机预览：不走服务端，直接弹示例。与 <see cref="Show"/> 的区别是**先关掉正在显示的**，
    /// 这样能连续切换示例（正常来消息时只会被拉到前台）。
    /// </summary>
    public static void Preview(AnnounceMessage msg, bool quiet, bool speak)
    {
        try { _current?.Close(); } catch { /* 已销毁，忽略 */ }
        _current = null;
        Show(msg, quiet, speak);
    }

    private AnnounceWindow(AnnounceMessage msg, bool quiet, bool speak)
    {
        InitializeComponent();
        Title = "远程喊话";
        ApplyAccent();

        _quiet = quiet;
        _speak = speak;

        // 服务端约束 10~120 秒，默认 30；这里再夹一次，防语义漂移导致窗挂很久
        _durationSec = Math.Clamp(msg.Duration <= 0 ? 30 : msg.Duration, 5, 180);
        _remainSec = _durationSec;

        BodyText.Text = msg.Text ?? "";

        // 署名只写名字，不带「说」（2026-10-25 Nick 要求）
        if (!string.IsNullOrWhiteSpace(msg.From))
        {
            FromText.Text = msg.From.Trim();
            FromText.Visibility = Visibility.Visible;
        }
        else
        {
            FromText.Visibility = Visibility.Collapsed;
        }

        Root.SizeChanged += (_, _) => FitText();
        Root.Loaded += (_, _) => FitText();

        _tick = DispatcherQueue.CreateTimer();
        _tick.Interval = TimeSpan.FromSeconds(1);
        _tick.IsRepeating = true;
        _tick.Tick += (_, _) => Tick();

        Closed += OnClosed;

        UpdateCountText();
    }

    public void Start()
    {
        Activate();

        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "远程喊话";

            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
            }

            // 全屏：先把窗口铺满工作区，再切成全屏呈现器 —— 换呈现器再挪尺寸时标题栏状态更干净
            var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            _appWindow.MoveAndResize(area);
            _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        catch (Exception ex)
        {
            AppLog.Fail("announce", ex, "喊话窗全屏失败");
        }

        SetupDimBackdrop();

        // ⚠️⛔ 必须等布局拿到真实尺寸再开场：FitText、按行切分、打字机预留高度全靠它，
        //    早一步就会量到 0 宽 —— 长文本被截断、交错划入退化成整块滑入，根因都是这个（2026-10-25 实测）。
        Root.Loaded += (_, _) => TryBegin();
        Root.SizeChanged += (_, _) => TryBegin();
        _ = ForceBeginAsync();
    }

    private bool _begun;

    private void TryBegin()
    {
        if (_begun) return;
        if (Root.ActualWidth < 200 || Root.ActualHeight < 200) return;

        _begun = true;

        FitText();
        BuildSlideLinesIfNeeded();
        PlayIntro();

        // 声音：安静模式什么都不放；否则先放提示音（用户可选），再按开关朗读
        if (!_quiet)
        {
            AnnounceSound.Play();
            if (_speak) _ = SpeakAsync(BodyText.Text);
        }

        _tick.Start();
        Root.Focus(FocusState.Programmatic);
    }

    /// <summary>兜底：万一 Loaded/SizeChanged 都没给到尺寸（极少见），1.2 秒后强行开场，别让窗口空着。</summary>
    private async Task ForceBeginAsync()
    {
        await Task.Delay(1200);
        TryBegin();
    }

    // ════════════════════════ 桌面变暗 ════════════════════════

    private DesktopAcrylicController? _acrylic;

    /// <summary>
    /// 「屏幕变暗」的正解：窗口本身透明，挂亚克力并把 tint 调成深色 —— 后面的桌面还在，只是被压暗、虚化。
    /// ⛔ 之前的做法是给根容器刷一个 90% 不透明的深色，等于盖了一块黑板（Nick 原话「背景黑色一坨」）。
    /// 系统不支持亚克力时才退回不透明深底。
    /// </summary>
    private void SetupDimBackdrop()
    {
        try
        {
            if (DesktopAcrylicController.IsSupported())
            {
                _acrylic = new DesktopAcrylicController
                {
                    TintColor = Windows.UI.Color.FromArgb(255, 0, 0, 0),
                    TintOpacity = 0.72f,
                    LuminosityOpacity = 0.0f,
                };
                _acrylic.AddSystemBackdropTarget(this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
                AppLog.Info("announce", "喊话窗使用亚克力背景（桌面可见、整体压暗）");
                return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("announce", "亚克力不可用，改用不透明深底：" + ex.Message);
        }

        Root.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(242, 0x0B, 0x0E, 0x13));
    }

    // ════════════════════════ 入场 ════════════════════════

    private void PlayIntro()
    {
        Fade(DimLayer, 0, 1, 300);
        Fade(AccentBar, 0, 1, 260);
        Fade(TopBar, 0, 1, 320, beginMs: 140);
        Fade(Footer, 0, 1, 320, beginMs: 220);

        var preset = (App.Settings.Current.AnnounceAnimation ?? "none").ToLowerInvariant();

        switch (preset)
        {
            case "typewriter":
                StartTypewriter();
                break;

            case "slide":
                StartSlideIn();
                break;

            case "pop":
                StartPop();
                break;

            default:
                // 兜底：不做动画，直接显示（不想要动画的人用这个）
                BodyHost.Opacity = 1;
                FromText.Opacity = FromText.Visibility == Visibility.Visible ? 1 : 0;
                break;
        }
    }

    /// <summary>打字机：逐字显示，完成后署名淡入。先按最终排版占好高度，避免逐字时行数跳动。</summary>
    private void StartTypewriter()
    {
        var full = BodyText.Text ?? "";
        BodyHost.Opacity = 1;

        // 预留最终高度（用离屏探针量），否则文字一多就边打边长、整块往上跳
        var height = MeasureTextHeight(full, BodyText.FontSize, Root.ActualWidth - 280);
        if (height > 0) BodyText.MinHeight = height;

        BodyText.Text = "";
        var shown = 0;
        var delay = full.Length > 60 ? 18 : 30;

        _typeTimer = DispatcherQueue.CreateTimer();
        _typeTimer.Interval = TimeSpan.FromMilliseconds(delay);
        _typeTimer.IsRepeating = true;
        _typeTimer.Tick += (_, _) =>
        {
            shown++;
            BodyText.Text = full[..Math.Min(shown, full.Length)];

            if (shown < full.Length) return;

            _typeTimer?.Stop();
            if (FromText.Visibility == Visibility.Visible)
                Fade(FromText, 0, 1, 420, beginMs: 120);
        };
        _typeTimer.Start();
    }

    /// <summary>弹入：整体放大到原尺寸，带一点回弹。</summary>
    private void StartPop()
    {
        BodyHost.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        var scale = new ScaleTransform { ScaleX = 0.86, ScaleY = 0.86 };
        BodyHost.RenderTransform = scale;

        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        Animate(BodyHost, "Opacity", 0, 1, 260, 0, ease);
        Animate(scale, "ScaleX", 0.86, 1, 460, 0, ease);
        Animate(scale, "ScaleY", 0.86, 1, 460, 0, ease);

        if (FromText.Visibility == Visibility.Visible)
            Fade(FromText, 0, 1, 380, beginMs: 380);
    }

    /// <summary>每行从屏幕左侧外侧交错滑入（2026-10-25 Nick 点名要的效果）。</summary>
    private void StartSlideIn()
    {
        if (_lineTexts.Count == 0)
        {
            // 没算出分行（极短消息等），退化成整体滑入
            BodyHost.Opacity = 1;
            var t = new TranslateTransform { X = -80 };
            BodyHost.RenderTransform = t;
            Animate(t, "X", -80, 0, 420, 0, new CubicEase { EasingMode = EasingMode.EaseOut });
            if (FromText.Visibility == Visibility.Visible) Fade(FromText, 0, 1, 380, beginMs: 260);
            return;
        }

        BodyHost.Opacity = 1;
        foreach (var child in LinesPanel.Children.OfType<TextBlock>())
        {
            var index = LinesPanel.Children.IndexOf(child);
            child.Opacity = 1;

            var transform = new TranslateTransform { X = -(Root.ActualWidth + 120) };
            child.RenderTransform = transform;

            Animate(transform, "X", -(Root.ActualWidth + 120), 0, 460, index * 90,
                new CubicEase { EasingMode = EasingMode.EaseOut });
        }

        var totalMs = 460 + Math.Max(0, _lineTexts.Count - 1) * 90;
        if (FromText.Visibility == Visibility.Visible) Fade(FromText, 0, 1, 380, beginMs: totalMs - 120);
    }

    // ════════════════════════ 出场 ════════════════════════

    /// <summary>
    /// 出场（2026-10-25 Nick 要求）：文字先退场 → 变暗层淡回 → 关窗。
    /// ⛔ 用「先动画再延时关」而不是直接 Close：直接关会硬切，看不出「慢慢变回去」。
    /// </summary>
    private async Task PlayOutroAsync()
    {
        if (_exiting) return;
        _exiting = true;

        try { _tick.Stop(); } catch { }
        try { _typeTimer?.Stop(); } catch { }
        try { AnnounceSound.Stop(); } catch { }

        Fade(BodyHost, BodyHost.Opacity, 0, 260);
        Fade(FromText, FromText.Opacity, 0, 220);
        Fade(TopBar, TopBar.Opacity, 0, 200);
        Fade(Footer, Footer.Opacity, 0, 200);
        Fade(AccentBar, AccentBar.Opacity, 0, 200);

        await Task.Delay(300);
        Fade(DimLayer, DimLayer.Opacity, 0, 420);
        await Task.Delay(460);

        Close();
    }

    // ════════════════════════ 排版 ════════════════════════

    /// <summary>
    /// 字号自适应：96 → 30 逐档试，直到正文在可用区域放得下。
    /// ⛔⛔ 不能对树内的 BodyText 调 Measure()（布局干净时返回缓存值，改字号不重算，
    /// 2026-10-25 实测长消息被上下裁掉）。用离屏探针：新对象、每次真的按当前字号排一遍。
    /// </summary>
    private void FitText()
    {
        var width = Root.ActualWidth - 280;
        var height = Root.ActualHeight - 270;
        if (width <= 0 || height <= 0) return;

        var probe = new TextBlock
        {
            Text = BodyText.Text,
            FontFamily = BodyText.FontFamily,
            FontWeight = BodyText.FontWeight,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Width = width,
        };

        for (var size = 96.0; size >= 30; size -= 3)
        {
            probe.FontSize = size;
            probe.LineHeight = Math.Round(size * 1.22);
            probe.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
            if (probe.DesiredSize.Height <= height)
            {
                BodyText.FontSize = size;
                BodyText.LineHeight = Math.Round(size * 1.22);
                return;
            }
        }

        BodyText.FontSize = 30;
        BodyText.LineHeight = 37;
    }

    /// <summary>量一段文字在给定宽度下的高度（打字机预留高度用）。</summary>
    private double MeasureTextHeight(string text, double fontSize, double width)
    {
        if (string.IsNullOrEmpty(text) || width <= 0) return 0;

        var probe = new TextBlock
        {
            Text = text,
            FontFamily = BodyText.FontFamily,
            FontWeight = BodyText.FontWeight,
            FontSize = fontSize,
            LineHeight = BodyText.LineHeight,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Width = width,
        };
        probe.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
        return probe.DesiredSize.Height;
    }

    /// <summary>
    /// 「交错划入」需要按行摆放，所以先把正文按实际宽度切行（贪心逐字加）。
    /// 切完若总高超出可用区域，就缩字号重来（最多几轮）。
    /// </summary>
    private void BuildSlideLinesIfNeeded()
    {
        var preset = (App.Settings.Current.AnnounceAnimation ?? "none").ToLowerInvariant();
        if (preset != "slide") return;

        var width = Root.ActualWidth - 280;
        var height = Root.ActualHeight - 270;
        var text = BodyText.Text ?? "";
        if (width <= 0 || height <= 0 || text.Length == 0) return;

        var size = BodyText.FontSize;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var lines = WrapLines(text, size, width);
            var total = lines.Count * Math.Round(size * 1.22);
            if (total <= height || size <= 30)
            {
                _lineTexts.Clear();
                _lineTexts.AddRange(lines);
                break;
            }
            size -= 4;
        }

        if (_lineTexts.Count == 0) return;

        LinesPanel.Children.Clear();
        foreach (var line in _lineTexts)
        {
            LinesPanel.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = size,
                LineHeight = Math.Round(size * 1.22),
                FontWeight = BodyText.FontWeight,
                FontFamily = BodyText.FontFamily,
                Foreground = BodyText.Foreground,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }

        BodyText.Visibility = Visibility.Collapsed;
        LinesPanel.Visibility = Visibility.Visible;
    }

    /// <summary>贪心切行：逐字累加，超宽就断开。中文没有空格，只能这样切。</summary>
    private static List<string> WrapLines(string text, double fontSize, double width)
    {
        var lines = new List<string>();
        var probe = new TextBlock
        {
            FontSize = fontSize,
            TextWrapping = TextWrapping.NoWrap,
        };

        var current = "";
        foreach (var ch in text)
        {
            var candidate = current + ch;
            probe.Text = candidate;
            probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

            if (probe.DesiredSize.Width > width && current.Length > 0)
            {
                lines.Add(current);
                current = ch.ToString();
            }
            else
            {
                current = candidate;
            }
        }

        if (current.Length > 0) lines.Add(current);
        return lines;
    }

    // ════════════════════════ 动画工具 ════════════════════════

    private static void Fade(UIElement element, double from, double to, int ms,
                             int beginMs = 0, EasingFunctionBase? easing = null)
        => Animate(element, "Opacity", from, to, ms, beginMs, easing);

    /// <summary>
    /// 动一根 double 属性。⛔ 目标必须是**属性所在的那个对象**：
    /// 动 Opacity 传元素本身，动 RenderTransform 的 X / ScaleX 要传 transform（传元素会静默不生效）。
    /// </summary>
    private static void Animate(DependencyObject target, string path, double from, double to, int ms,
                                int beginMs, EasingFunctionBase? easing)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            BeginTime = TimeSpan.FromMilliseconds(beginMs),
            EnableDependentAnimation = true,
        };
        if (easing is not null) anim.EasingFunction = easing;

        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, path);

        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    /// <summary>强调色统一取系统强调色；窗口固定深底，所以文字用提亮版。</summary>
    private void ApplyAccent()
    {
        var accent = AccentColor();
        AccentBar.Background = new SolidColorBrush(accent);
        AccentIcon.Foreground = new SolidColorBrush(accent);
        RemainBar.Foreground = new SolidColorBrush(accent);
    }

    private static Windows.UI.Color AccentColor()
    {
        try
        {
            var ui = new Windows.UI.ViewManagement.UISettings();
            return ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
        }
        catch
        {
            return Windows.UI.Color.FromArgb(255, 0, 120, 212);
        }
    }

    // ════════════════════════ 倒计时 ════════════════════════

    private void Tick()
    {
        if (_exiting) return;

        _remainSec--;
        if (_remainSec <= 0)
        {
            _remainSec = 0;
            UpdateCountText();
            _ = PlayOutroAsync();
            return;
        }

        UpdateCountText();
    }

    private void UpdateCountText()
    {
        CountText.Text = $"{_remainSec} 秒后自动关闭　·　按 Esc 或右上角按钮可提前关闭";
        RemainBar.Value = _durationSec > 0
            ? Math.Clamp(_remainSec * 100.0 / _durationSec, 0, 100)
            : 0;
    }

    // ════════════════════════ 关闭 / 朗读 ════════════════════════

    private void Close_Click(object sender, RoutedEventArgs e) => _ = PlayOutroAsync();

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            _ = PlayOutroAsync();
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        IsShowing = false;
        _current = null;
        try { _typeTimer?.Stop(); } catch { }
        try { AnnounceSound.Stop(); } catch { }
        try { _acrylic?.Dispose(); } catch { }
        _acrylic = null;
        TtsStop();
    }

    private async Task SpeakAsync(string text)
    {
        try
        {
            TtsStop();

            var voice = SpeechSynthesizer.AllVoices
                .FirstOrDefault(v => v.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
            if (voice is null)
            {
                AppLog.Warn("announce", "系统没有中文语音，跳过朗读");
                return;
            }

            var synth = new SpeechSynthesizer { Voice = voice };
            var stream = await synth.SynthesizeTextToStreamAsync(text);
            _ttsSynth = synth;
            _ttsStream = stream;

            _ttsPlayer = new MediaPlayer();
            _ttsPlayer.MediaEnded += (_, _) => TtsStop();
            _ttsPlayer.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
            _ttsPlayer.Play();
        }
        catch (Exception ex)
        {
            AppLog.Warn("announce", "朗读失败：" + ex.Message);
        }
    }

    private void TtsStop()
    {
        try { _ttsPlayer?.Pause(); } catch { }
        try { _ttsPlayer?.Dispose(); } catch { }
        try { _ttsStream?.Dispose(); } catch { }
        try { _ttsSynth?.Dispose(); } catch { }
        _ttsPlayer = null;
        _ttsStream = null;
        _ttsSynth = null;
    }

    private MediaPlayer? _ttsPlayer;
    private SpeechSynthesisStream? _ttsStream;
    private SpeechSynthesizer? _ttsSynth;
}
