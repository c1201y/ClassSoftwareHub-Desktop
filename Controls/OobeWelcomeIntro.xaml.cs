using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Views.Oobe;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace ClassSoftwareHub.Desktop.Controls;
















public sealed partial class OobeWelcomeIntro : UserControl
{
    private const string Slogan = "Fast, Useful & Convenient for class";
    private const string Brand = "ClassSoftwareHub";
    private const double SloganFontSize = 30;

    
    private const double LineOffsetY = 4;

    private const string GlyphPool =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789#$%&*+-=/<>";

    
    private static readonly string[] Features =
    {
        "图片取色", "编码", "哈希工具", "函数图像分成求解", "化学方程式配平", "关闭窗口",
        "关闭前台应用", "反馈中心", "提交软件", "系统功能关闭启用", "系统优化",
        "希沃白板强制结束", "亮度调节", "日志回溯", "快速设置", "定时关机", "音量调节",
        "放大镜", "截图钉图", "任务视图", "返回桌面", "全屏时钟", "秒表计时",
        "常用系统修复指令", "剪贴板历史", "快速打开", "随机抽号", "课堂计时",
        "AppData文件管理", "局域网快速配对", "安全选项快速打开", "系统分类",
        "HubAI", "软件下载", "远程教室喊话", "任务管理器快速打开", "开机启动项管理", "时钟",
    };

    private sealed class MarqueeRow
    {
        public TextBlock Block = null!;
        public double StartX;
        public double CopyWidth;
        public double DurationMs;
    }

    private sealed class GlyphSlot
    {
        public TextBlock Block = null!;
        public double Width;

        
        public bool Space;

        
        public string[] Glyphs = Array.Empty<string>();

        
        public char? Target;

        
        public double BrandWidth;

        
        
        
        
        public double Advance;

        
        public double ResolveAt = double.MaxValue;
        public double EliminateAt = double.MaxValue;

        
        public bool Settled;

        
        public bool Gone;

        public double NextTick;
    }

    private readonly List<Storyboard> _marqueeBoards = new();
    private readonly List<MarqueeRow> _marqueeRows = new();
    private readonly List<GlyphSlot> _slots = new();
    private readonly Random _random = new();

    private double _stageWidth;
    private double _stageHeight;
    private double _centerY;
    private double _wordmarkLift;

    private bool _built;
    private bool _requested;
    private bool _played;
    private bool _stop;
    private bool _completed;

    public event EventHandler? Completed;

    public OobeWelcomeIntro()
    {
        InitializeComponent();

        VersionLine.Text = $"Desktop({(SystemInfo.IsWin11 ? "Win11" : "Win10")}) {ShellConfig.DisplayVersion}";
        LoadLogo();

        Loaded += (_, _) =>
        {
            EnsureBuilt();
            if (_requested) StartPlay();
        };
        SizeChanged += (_, _) => EnsureBuilt();
        Unloaded += OnUnloaded;
    }

    
    public void Play()
    {
        _requested = true;
        if (_built && IsLoaded) StartPlay();
    }

    

    private void EnsureBuilt()
    {
        if (_built) return;

        var width = Stage.ActualWidth;
        var height = Stage.ActualHeight;
        if (width < 200 || height < 150) return;

        _built = true;
        _stageWidth = width;
        _stageHeight = height;
        _centerY = height / 2 + LineOffsetY;

        BuildMarquee();
        BuildSloganSlots();
        BuildWordmarkGlyphs();
        ComputeWordmarkLift();

        if (Wordmark.ActualHeight <= 0)
            DispatcherQueue.TryEnqueue(ComputeWordmarkLift);

        if (_requested && IsLoaded) StartPlay();
    }

    
    private void LoadLogo()
    {
        try
        {
            var path = EmbeddedAssets.ExtractToCache("AppIcon-512.png", "app-icon.png");
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                Logo.Source = new BitmapImage(new Uri(path));
        }
        catch (Exception ex)
        {
            AppLog.Exception("oobe", ex, "欢迎页图标加载失败");
        }
    }

    private void BuildMarquee()
    {
        var layerWidth = _stageWidth * 2.2;
        var layerHeight = _stageHeight * 1.9;
        MarqueeLayer.Width = layerWidth;
        MarqueeLayer.Height = layerHeight;
        Canvas.SetLeft(MarqueeLayer, (_stageWidth - layerWidth) / 2);
        Canvas.SetTop(MarqueeLayer, (_stageHeight - layerHeight) / 2);

        var probe = new TextBlock { FontWeight = FontWeights.SemiBold };

        
        
        const string gap = "\u00A0\u00A0\u00A0\u00A0";

        for (var i = 0; i < 13; i++)
        {
            var fontSize = 11.5 + _random.NextDouble() * 6.5;

            
            var names = new List<string>();
            var count = 5 + _random.Next(4);
            for (var n = 0; n < count; n++) names.Add(Features[_random.Next(Features.Length)]);
            var period = string.Join(gap, names) + gap;

            probe.FontSize = fontSize;
            probe.Text = period;
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var periodWidth = Math.Max(160, probe.DesiredSize.Width);

            var repeats = (int)Math.Ceiling(layerWidth / periodWidth) + 2;
            var builder = new StringBuilder(period);
            for (var r = 1; r < repeats; r++) builder.Append(period);

            var row = new TextBlock
            {
                Text = builder.ToString(),
                FontSize = fontSize,
                FontWeight = FontWeights.SemiBold,
                Opacity = 0.24 + _random.NextDouble() * 0.5,
                IsHitTestVisible = false,
                RenderTransform = new TranslateTransform(),
                
                Margin = new Thickness(-_random.NextDouble() * periodWidth * 0.8, 0, 0, 0),
            };

            MarqueeRows.Children.Add(row);
            _marqueeRows.Add(new MarqueeRow
            {
                Block = row,
                StartX = -_random.NextDouble() * periodWidth,
                CopyWidth = periodWidth,
                DurationMs = 900 + _random.NextDouble() * 800,
            });
        }
    }

    
    
    
    
    
    
    
    
    
    private void BuildSloganSlots()
    {
        var probe = new TextBlock { FontSize = SloganFontSize, FontWeight = FontWeights.Bold };

        double WidthOf(string text)
        {
            probe.Text = text;
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return probe.DesiredSize.Width;
        }

        probe.Text = "Ag";
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var lineHeight = probe.DesiredSize.Height;

        
        var spaceWidth = Math.Max(6, WidthOf("A A") - WidthOf("AA"));

        
        var pool = new List<(string Text, double Width)>(GlyphPool.Length);
        foreach (var glyph in GlyphPool)
        {
            var text = glyph.ToString();
            pool.Add((text, WidthOf(text)));
        }

        string[] CandidatesFor(double width)
        {
            var list = new List<string>();
            foreach (var (text, w) in pool)
            {
                if (w >= width * 0.6 && w <= width * 1.45) list.Add(text);
            }
            if (list.Count < 4)
            {
                foreach (var (text, _) in pool) list.Add(text);
            }
            return list.ToArray();
        }

        var x = 0.0;
        foreach (var ch in Slogan)
        {
            if (ch == ' ')
            {
                
                
                var gapBlock = new TextBlock
                {
                    Text = " ",
                    FontSize = SloganFontSize,
                    FontWeight = FontWeights.Bold,
                    Width = spaceWidth,
                    Opacity = 0,
                    IsHitTestVisible = false,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new CompositeTransform(),
                };
                Canvas.SetLeft(gapBlock, x);
                Canvas.SetTop(gapBlock, 0);
                SloganLine.Children.Add(gapBlock);

                _slots.Add(new GlyphSlot
                {
                    Block = gapBlock,
                    Width = spaceWidth,
                    Advance = spaceWidth,
                    Space = true,
                });
                x += spaceWidth;
                continue;
            }

            var width = Math.Max(4, WidthOf(ch.ToString()));
            var block = new TextBlock
            {
                Text = ch.ToString(),
                FontSize = SloganFontSize,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                Width = width,          
                Opacity = 0,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new CompositeTransform(),
            };
            Canvas.SetLeft(block, x);
            Canvas.SetTop(block, 0);
            SloganLine.Children.Add(block);

            _slots.Add(new GlyphSlot
            {
                Block = block,
                Width = width,
                Advance = width,
                Glyphs = CandidatesFor(width),
            });
            x += width;
        }

        Canvas.SetLeft(SloganLine, (_stageWidth - x) / 2);
        Canvas.SetTop(SloganLine, _centerY - lineHeight / 2);
    }

    
    
    
    
    
    
    
    private void BuildWordmarkGlyphs()
    {
        foreach (var ch in Brand)
        {
            Wordmark.Children.Add(new TextBlock
            {
                Text = ch.ToString(),
                FontSize = SloganFontSize,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                Width = Math.Max(4, MeasureText(ch.ToString())),
                IsHitTestVisible = false,
            });
        }
    }

    
    
    
    
    private void ComputeWordmarkLift()
    {
        if (Wordmark.ActualHeight <= 0) return;

        WordmarkShift.TranslateY = 0;
        var origin = Wordmark.TransformToVisual(Stage).TransformPoint(new Point(0, 0));
        _wordmarkLift = _centerY - (origin.Y + Wordmark.ActualHeight / 2);
        WordmarkShift.TranslateY = _wordmarkLift;
    }

    

    private void StartPlay()
    {
        if (_played || !_built) return;
        _played = true;

        if (!OobeMotion.AnimationsEnabled)
        {
            ShowFinalStatic();
            RaiseCompleted();
            return;
        }

        _ = PlayAsync();
    }

    private async Task PlayAsync()
    {
        try
        {
            MarqueeCanvas.Visibility = Visibility.Visible;
            StartMarqueeScroll();
            Animate(MarqueeCanvas, "Opacity", 0, 1, 220);

            
            await Delay(320);
            if (_stop) return;

            
            await Delay(1800);
            if (_stop) return;

            
            AnimateMarqueeExit();
            await Delay(160);
            if (_stop) return;

            
            FlyInSlogan();
            await Delay(900);
            if (_stop) return;

            await Delay(260);
            if (_stop) return;

            
            await ScrambleToBrand();
            if (_stop) return;

            await Delay(260);
            if (_stop) return;

            
            HandOffToWordmark();
            if (_stop) return;

            await Delay(60);
            if (_stop) return;

            
            Descend();
            await Delay(680);
            if (_stop) return;

            RaiseCompleted();
        }
        catch (Exception ex)
        {
            AppLog.Warn("oobe", "欢迎页开场失败：" + ex.Message);
            ShowFinalStatic();
            RaiseCompleted();
        }
    }

    private void StartMarqueeScroll()
    {
        foreach (var row in _marqueeRows)
        {
            var transform = (TranslateTransform)row.Block.RenderTransform;
            transform.X = row.StartX;

            var sb = new Storyboard();
            var scroll = new DoubleAnimation
            {
                From = row.StartX,
                To = row.StartX - row.CopyWidth,
                Duration = Ms(row.DurationMs),
                RepeatBehavior = RepeatBehavior.Forever,
            };
            Storyboard.SetTarget(scroll, row.Block);
            Storyboard.SetTargetProperty(scroll, "(UIElement.RenderTransform).(TranslateTransform.X)");
            sb.Children.Add(scroll);
            sb.Begin();
            _marqueeBoards.Add(sb);
        }
    }

    private void AnimateMarqueeExit()
    {
        var exit = new Storyboard();

        var slideX = new DoubleAnimation
        {
            From = 0,
            To = -_stageWidth * 1.5,
            Duration = Ms(560),
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 4 },
        };
        Storyboard.SetTarget(slideX, MarqueeLayer);
        Storyboard.SetTargetProperty(slideX, "(UIElement.RenderTransform).(CompositeTransform.TranslateX)");

        var slideY = new DoubleAnimation
        {
            From = 0,
            To = _stageHeight * 0.35,
            Duration = Ms(560),
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 4 },
        };
        Storyboard.SetTarget(slideY, MarqueeLayer);
        Storyboard.SetTargetProperty(slideY, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");

        exit.Children.Add(slideX);
        exit.Children.Add(slideY);
        exit.Completed += (_, _) =>
        {
            StopMarquee();
            MarqueeCanvas.Visibility = Visibility.Collapsed;
        };
        exit.Begin();
    }

    
    private void FlyInSlogan()
    {
        LineCanvas.Visibility = Visibility.Visible;
        LineCanvas.Opacity = 1;

        var order = 0;
        foreach (var slot in _slots)
        {
            var delay = order * 10 + _random.Next(0, 28);
            var duration = 250 + _random.NextDouble() * 130;
            var jitter = (_random.NextDouble() - 0.5) * 56;

            
            var startX = -(_stageWidth + 80 + _random.NextDouble() * 220);

            var transform = (CompositeTransform)slot.Block.RenderTransform;
            transform.TranslateX = startX;
            transform.TranslateY = jitter;
            transform.ScaleX = 1.4;
            slot.Block.Opacity = 0;

            var sb = new Storyboard();

            var x = new DoubleAnimation
            {
                From = startX,
                To = 0,
                Duration = Ms(duration),
                BeginTime = Time(delay),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(x, slot.Block);
            Storyboard.SetTargetProperty(x, "(UIElement.RenderTransform).(CompositeTransform.TranslateX)");

            var y = new DoubleAnimation
            {
                From = jitter,
                To = 0,
                Duration = Ms(duration * 0.9),
                BeginTime = Time(delay),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(y, slot.Block);
            Storyboard.SetTargetProperty(y, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");

            var stretch = new DoubleAnimation
            {
                From = 1.4,
                To = 1,
                Duration = Ms(duration * 0.8),
                BeginTime = Time(delay),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(stretch, slot.Block);
            Storyboard.SetTargetProperty(stretch, "(UIElement.RenderTransform).(CompositeTransform.ScaleX)");

            var fade = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = Ms(90),
                BeginTime = Time(delay + 30),
            };
            Storyboard.SetTarget(fade, slot.Block);
            Storyboard.SetTargetProperty(fade, "Opacity");

            sb.Children.Add(x);
            sb.Children.Add(y);
            sb.Children.Add(stretch);
            sb.Children.Add(fade);
            sb.Completed += (_, _) =>
            {
                transform.TranslateX = 0;
                transform.TranslateY = 0;
                transform.ScaleX = 1;
                slot.Block.Opacity = 1;
            };
            sb.Begin();

            order++;
        }
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private async Task ScrambleToBrand()
    {
        var end = PrepareMorphTargets();
        foreach (var slot in _slots) slot.NextTick = 0;

        var start = Environment.TickCount64;
        while (!_stop)
        {
            var elapsed = Environment.TickCount64 - start;
            if (elapsed >= end) break;

            MorphTick(elapsed);
            await Delay(8);
        }

        
        if (!_stop) MorphTick(end);
    }

    
    private void MorphTick(double elapsed)
    {
        var total = 0.0;

        foreach (var slot in _slots)
        {
            if (slot.Space)
            {
                
                slot.Advance = elapsed >= slot.EliminateAt ? 0 : slot.Width;
                total += slot.Advance;
                continue;
            }

            if (slot.Target is char target)
            {
                if (!slot.Settled)
                {
                    if (elapsed >= slot.ResolveAt)
                    {
                        slot.Settled = true;
                        slot.Block.Text = target.ToString();
                        
                        slot.Block.Width = slot.BrandWidth;
                        slot.Block.Opacity = 1;
                        SetGlyphScale(slot, 1);
                    }
                    else
                    {
                        ScrambleGlyph(slot, elapsed);
                    }
                }

                slot.Advance = slot.Settled ? slot.BrandWidth : slot.Width;
            }
            else
            {
                if (!slot.Gone && elapsed >= slot.EliminateAt)
                {
                    slot.Gone = true;
                    slot.Block.Visibility = Visibility.Collapsed;
                }

                if (!slot.Gone) ScrambleGlyph(slot, elapsed);
                slot.Advance = slot.Gone ? 0 : slot.Width;
            }

            total += slot.Advance;
        }

        
        var x = 0.0;
        foreach (var slot in _slots)
        {
            Canvas.SetLeft(slot.Block, x + (slot.Advance - slot.Block.Width) / 2);
            x += slot.Advance;
        }

        
        Canvas.SetLeft(SloganLine, (_stageWidth - total) / 2);
    }

    
    private void ScrambleGlyph(GlyphSlot slot, double elapsed)
    {
        if (slot.Glyphs.Length == 0 || elapsed < slot.NextTick) return;

        slot.NextTick = elapsed + 34 + _random.NextDouble() * 40;
        slot.Block.Text = slot.Glyphs[_random.Next(slot.Glyphs.Length)];
    }

    private static void SetGlyphScale(GlyphSlot slot, double scale)
    {
        if (slot.Block.RenderTransform is not CompositeTransform transform) return;
        transform.ScaleX = scale;
        transform.ScaleY = scale;
    }

    
    
    
    
    private double PrepareMorphTargets()
    {
        
        var glyphSlots = new List<int>();
        for (var i = 0; i < _slots.Count; i++)
        {
            if (!_slots[i].Space) glyphSlots.Add(i);
        }

        
        var survivors = new HashSet<int>();
        for (var k = 0; k < Brand.Length; k++)
        {
            var at = (int)Math.Round(k * (glyphSlots.Count - 1) / (double)(Brand.Length - 1));
            survivors.Add(glyphSlots[at]);
        }

        var lastResolve = 0.0;
        var rank = 0;
        var extras = new List<int>();

        for (var i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            slot.Advance = slot.Width;

            if (!survivors.Contains(i))
            {
                slot.Target = null;
                extras.Add(i);
                continue;
            }

            var ch = Brand[rank];
            slot.Target = ch;
            slot.BrandWidth = MeasureText(ch.ToString());
            slot.ResolveAt = 160 + rank * 46 + _random.NextDouble() * 70;
            lastResolve = Math.Max(lastResolve, slot.ResolveAt);
            rank++;
        }

        
        
        var half = (_slots.Count - 1) / 2.0;
        extras.Sort((a, b) => Math.Abs(b - half).CompareTo(Math.Abs(a - half)));
        var span = 620.0 / Math.Max(1, extras.Count - 1);
        var lastGone = 0.0;
        for (var j = 0; j < extras.Count; j++)
        {
            var slot = _slots[extras[j]];
            
            slot.EliminateAt = j * span + _random.NextDouble() * 50;
            lastGone = Math.Max(lastGone, slot.EliminateAt);
        }

        return Math.Max(lastResolve, lastGone) + 40;
    }

    
    
    
    
    
    
    
    private void HandOffToWordmark()
    {
        LineCanvas.Visibility = Visibility.Collapsed;
        LineCanvas.Opacity = 0;
        Wordmark.Opacity = 1;
    }

    private void Descend()
    {
        Logo.Opacity = 0;
        LogoShift.TranslateY = 14;
        LogoShift.ScaleX = 0.88;
        LogoShift.ScaleY = 0.88;

        WelcomeLine.Opacity = 0;
        WelcomeShift.Y = 18;

        VersionLine.Opacity = 0;
        VersionShift.Y = 8;

        var sb = new Storyboard();

        
        
        
        WordmarkShift.TranslateX = 0;
        var drop = new DoubleAnimation
        {
            From = _wordmarkLift,
            To = 0,
            Duration = Ms(620),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(drop, Wordmark);
        Storyboard.SetTargetProperty(drop, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");

        sb.Children.Add(drop);

        
        AddAnimation(sb, Logo, "Opacity", 0, 1, 420, 80);
        AddAnimation(sb, Logo, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)", 14, 0, 460, 80);
        AddAnimation(sb, Logo, "(UIElement.RenderTransform).(CompositeTransform.ScaleX)", 0.88, 1, 460, 80);
        AddAnimation(sb, Logo, "(UIElement.RenderTransform).(CompositeTransform.ScaleY)", 0.88, 1, 460, 80);

        
        AddAnimation(sb, WelcomeLine, "Opacity", 0, 1, 380, 230);
        AddAnimation(sb, WelcomeLine, "(UIElement.RenderTransform).(TranslateTransform.Y)", 18, 0, 420, 230);

        
        AddAnimation(sb, VersionLine, "Opacity", 0, 1, 320, 390);
        AddAnimation(sb, VersionLine, "(UIElement.RenderTransform).(TranslateTransform.Y)", 8, 0, 320, 390);

        sb.Completed += (_, _) =>
        {
            Logo.Opacity = 1;
            LogoShift.TranslateY = 0;
            LogoShift.ScaleX = 1;
            LogoShift.ScaleY = 1;
            WelcomeLine.Opacity = 1;
            WelcomeShift.Y = 0;
            VersionLine.Opacity = 1;
            VersionShift.Y = 0;
            WordmarkShift.TranslateX = 0;
            WordmarkShift.TranslateY = 0;
        };
        sb.Begin();
    }

    

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        
        _stop = true;
        StopMarquee();
        ShowFinalStatic();
        RaiseCompleted();
    }

    private void StopMarquee()
    {
        foreach (var board in _marqueeBoards) board.Stop();
        _marqueeBoards.Clear();
    }

    private void ShowFinalStatic()
    {
        MarqueeCanvas.Visibility = Visibility.Collapsed;
        LineCanvas.Visibility = Visibility.Collapsed;

        Logo.Opacity = 1;
        LogoShift.TranslateY = 0;
        LogoShift.ScaleX = 1;
        LogoShift.ScaleY = 1;

        WelcomeLine.Opacity = 1;
        WelcomeShift.Y = 0;

        Wordmark.Opacity = 1;
        WordmarkShift.TranslateX = 0;
        WordmarkShift.TranslateY = 0;
        WordmarkShift.ScaleX = 1;

        VersionLine.Opacity = 1;
        VersionShift.Y = 0;
    }

    private void RaiseCompleted()
    {
        if (_completed) return;
        _completed = true;
        Completed?.Invoke(this, EventArgs.Empty);
    }

    

    private static void Animate(FrameworkElement element, string property,
        double from, double to, double durationMs, double delayMs = 0)
    {
        var sb = new Storyboard();
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = Ms(durationMs),
            BeginTime = Time(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, property);

        sb.Children.Add(animation);
        sb.Completed += (_, _) => element.Opacity = to;
        sb.Begin();
    }

    
    private static double MeasureText(string text)
    {
        var probe = new TextBlock
        {
            Text = text,
            FontSize = SloganFontSize,
            FontWeight = FontWeights.Bold,
        };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    private static void AddAnimation(Storyboard sb, DependencyObject target, string path,
        double from, double to, double durationMs, double delayMs = 0)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = Ms(durationMs),
            BeginTime = Time(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, path);
        sb.Children.Add(animation);
    }

    private static Task Delay(double ms) => Task.Delay(TimeSpan.FromMilliseconds(ms));

    private static Duration Ms(double ms) => new(TimeSpan.FromMilliseconds(ms));

    
    private static TimeSpan Time(double ms) => TimeSpan.FromMilliseconds(ms);
}
