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












public sealed partial class AnnounceWindow : Window
{
    private static AnnounceWindow? _current;

    
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
                _current = null;   
            }
        }

        var window = new AnnounceWindow(msg, quiet, speak);
        _current = window;
        window.Start();
    }

    
    
    
    
    public static void Preview(AnnounceMessage msg, bool quiet, bool speak)
    {
        try { _current?.Close(); } catch {  }
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

        
        _durationSec = Math.Clamp(msg.Duration <= 0 ? 30 : msg.Duration, 5, 180);
        _remainSec = _durationSec;

        _fullText = msg.Text ?? "";
        BodyText.Text = _fullText;

        
        if (!string.IsNullOrWhiteSpace(msg.From))
        {
            FromText.Text = msg.From.Trim();
            FromText.Visibility = Visibility.Visible;
        }
        else
        {
            FromText.Visibility = Visibility.Collapsed;
        }

        
        Root.Children.Add(_measureProbe);

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
        
        
        
        
        
        
        
        
        try
        {
            
            _appWindow = AppWindow;
            _appWindow.Title = "远程喊话";

            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
            }

            _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        catch (Exception ex)
        {
            AppLog.Fail("announce", ex, "喊话窗全屏失败");
        }

        Activate();

        
        
        
        
        
        
        
        try
        {
            WindowChrome.RemoveBorder(WindowNative.GetWindowHandle(this), rounded: false, dark: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("announce", "去掉窗口边框失败：" + ex.Message);
        }

        SetupDimBackdrop();

        
        
        Root.Loaded += (_, _) => TryBegin();
        Root.SizeChanged += (_, _) => TryBegin();
        _ = ForceBeginAsync();
    }

    private bool _begun;

    
    
    private string _fullText = "";

    
    private double _fittedHeight;

    
    private bool _acrylicReady;


    
    
    
    
    
    
    
    private readonly TextBlock _measureProbe = new()
    {
        Opacity = 0,
        IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
    };

    private void TryBegin()
    {
        if (_begun) return;
        if (Root.ActualWidth < 200 || Root.ActualHeight < 200) return;

        
        
        
        
        
        
        var rootSize = Root.XamlRoot?.Size;
        if (rootSize is { Height: > 200 } rs && Root.ActualHeight < rs.Height * 0.95) return;

        _begun = true;

        try
        {
            FitText();
            BuildSlideLinesIfNeeded();
            PlayIntro();
        }
        catch (Exception ex)
        {
            Core.AppLog.Fail("announce", ex, "开场失败");
            return;
        }

        
        if (!_quiet)
        {
            AnnounceSound.Play();
            if (_speak) _ = SpeakAsync(BodyText.Text);
        }

        _tick.Start();
        Root.Focus(FocusState.Programmatic);
    }

    
    private async Task ForceBeginAsync()
    {
        await Task.Delay(1200);
        TryBegin();
    }

    

    private DesktopAcrylicController? _acrylic;

    
    
    
    
    
    private void SetupDimBackdrop()
    {
        try
        {
            
            
            
            
            
            SystemBackdrop = new DesktopAcrylicBackdrop();
            _acrylicReady = true;

            
            
            Core.AppLog.Info("announce", "喊话窗使用系统默认亚克力（桌面可见）+ 30% 变暗层");
            return;
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn("announce", "亚克力不可用，退回半透明深底：" + ex.Message);
        }

        
        
        Root.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(110, 0x0B, 0x0E, 0x13));
    }

    

    private void PlayIntro()
    {
        
        
        
        
        
        Core.AppLog.Step("announce",
            $"开场：动画={(App.Settings.Current.AnnounceAnimation ?? "(null)")}，亚克力={(_acrylicReady ? "已挂" : "未挂")}，窗口 {Root.ActualWidth:0}×{Root.ActualHeight:0}");

        
        
        
        Fade(DimLayer, 1, 0.30, 420);
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
                
                BodyHost.Opacity = 1;
                FromText.Opacity = FromText.Visibility == Visibility.Visible ? 1 : 0;
                break;
        }
    }

    
    private void StartTypewriter()
    {
        var full = BodyText.Text ?? "";
        BodyHost.Opacity = 1;

        
        var height = _fittedHeight;
        if (height <= 0)
            height = MeasureRealHeight(full, BodyText.FontSize, BodyText.LineHeight, Root.ActualWidth - 280);
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

    
    private void StartSlideIn()
    {
        if (_lineTexts.Count == 0)
        {
            
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

    

    
    
    
    
    
    
    
    
    private void FitText()
    {
        var width = Root.ActualWidth - 280;
        var maxHeight = Root.ActualHeight - 270;
        if (width <= 0 || maxHeight <= 0) return;

        
        
        
        var fit = TextFit.PickFontSize(
            _fullText, width, maxHeight,
            BodyText.FontSize > 0 ? BodyText.FontSize : 88,
            MeasureRealHeight);

        BodyText.FontSize = fit.FontSize;
        BodyText.LineHeight = fit.LineHeight;
        _fittedHeight = fit.Height;

        Core.AppLog.Step("announce",
            $"字号适配：窗口 {Root.ActualWidth:0}×{Root.ActualHeight:0}，可用高 {maxHeight:0}，"
            + $"字号 {fit.FontSize:0}，高度 {fit.Height:0}");
    }

    
    
    
    
    
    private double MeasureRealHeight(string text, double fontSize, double lineHeight, double width)
    {
        if (string.IsNullOrEmpty(text) || width <= 0) return 0;

        _measureProbe.Text = text;
        _measureProbe.FontFamily = BodyText.FontFamily;
        _measureProbe.FontWeight = BodyText.FontWeight;
        _measureProbe.FontSize = fontSize;
        _measureProbe.LineHeight = lineHeight;
        _measureProbe.Width = width;

        _measureProbe.UpdateLayout();

        var h = _measureProbe.ActualHeight;
        if (h <= 0) h = _measureProbe.DesiredSize.Height;
        return h;
    }

    
    
    
    
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
            Core.AppLog.Step("announce", $"切行：字号 {size:0} → {lines.Count} 行，总高 {total:0}（可用 {height:0}）");
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

    

    private static void Fade(UIElement element, double from, double to, int ms,
                             int beginMs = 0, EasingFunctionBase? easing = null)
        => Animate(element, "Opacity", from, to, ms, beginMs, easing);

    
    
    
    
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
