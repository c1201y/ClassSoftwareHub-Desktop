using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace ClassSoftwareHub.Desktop.Pages;





public sealed partial class WelcomePage : Page
{
    public WelcomePage()
    {
        InitializeComponent();

        
        
        
        
        Loaded += (_, _) =>
        {
            
            _recommendSeed = Random.Shared.Next();

            App.Content.Changed -= OnContentChanged;
            App.Content.Changed += OnContentChanged;

            
            Services.Updating.UpdateNotice.Changed -= OnUpdateNoticeChanged;
            Services.Updating.UpdateNotice.Changed += OnUpdateNoticeChanged;

            Populate();
            RefreshUpdateCard();

            
            _ = LoadNoticesAsync();
        };

        
        QuickGrid.SizeChanged += (_, e) => FitQuickRow(e.NewSize.Width);

        
        ActualThemeChanged += (_, _) => ApplyQuickLinkIcons();

        
        RecommendViewport.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                RecommendViewport.Clip = new RectangleGeometry
                {
                    Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
                };
            }

            if (RecommendStrip.Children.Count == 0 && RecommendSection.Visibility == Visibility.Visible)
                LayoutRecommend();
        };

        
        Unloaded += (_, _) =>
        {
            _recommendStoryboard?.Stop();
            _recommendStoryboard = null;
        };
    }

    
    private void OnContentChanged()
    {
        
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnContentChanged);
            return;
        }

        RefreshCollectedCount();
        BuildRecommendations();
    }

    
    
    
    
    private void OnUpdateNoticeChanged()
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            RefreshUpdateCard();
            return;
        }

        DispatcherQueue.TryEnqueue(RefreshUpdateCard);
    }

    

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    

    
    
    
    
    
    
    
    
    
    
    
    private async Task LoadNoticesAsync()
    {
        try
        {
            await Task.Delay(1200);
            var items = await Services.NoticeFeed.LoadAsync(ShellConfig.ShellVersion);
            RenderNotices(items);
        }
        catch (Exception ex)
        {
            
            AppLog.Warn("notice", "分发展示失败：" + ex.GetType().Name + " " + ex.Message);
        }
    }

    
    private void RenderNotices(IReadOnlyList<NoticeItem> items)
    {
        if (NoticeHost is null) return;

        NoticeHost.Children.Clear();
        foreach (var item in items)
            NoticeHost.Children.Add(BuildNoticeBar(item));
    }

    
    
    
    
    
    
    
    
    
    
    
    private InfoBar BuildNoticeBar(NoticeItem item) => new()
    {
        IsOpen = true,
        
        
        IsClosable = false,
        Severity = item.Level switch
        {
            "success" => InfoBarSeverity.Success,
            "warning" => InfoBarSeverity.Warning,
            "error" => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        },
        Title = item.Title,
        Message = item.Body,
        ActionButton = BuildNoticeAction(item),
    };

    
    
    
    
    
    
    private HyperlinkButton? BuildNoticeAction(NoticeItem item)
    {
        if (item.Link.Length == 0) return null;
        if (!Uri.TryCreate(item.Link, UriKind.Absolute, out var uri)) return null;

        return new HyperlinkButton
        {
            Content = item.LinkText.Length > 0 ? item.LinkText : "查看详情",
            NavigateUri = uri,
        };
    }

    private void RefreshUpdateCard()
    {
        var s = App.Settings.Current;
        var path = s.UpdatePendingPath ?? "";
        var pending = false;

        if (path.Length > 0)
        {
            
            if (!System.IO.File.Exists(path)
                || s.UpdatePendingTag == ShellConfig.VersionPrefix + ShellConfig.ShellVersion)
            {
                s.UpdatePendingPath = "";
                s.UpdatePendingTag = "";
                App.Settings.Save();
            }
            else
            {
                pending = true;
            }
        }

        
        
        
        
        ApplyUpdateHighlight(pending || Services.Updating.UpdateNotice.Pending is not null);

        if (pending)
        {
            
            var tag = s.UpdatePendingTag.Length > 0 ? s.UpdatePendingTag : "新版本";
            ChangelogCardTitle.Text = $"{tag} 已下载就绪";
            ChangelogCardSubtitle.Text = "安装包已通过校验，可立即安装";
            UpdateInstallButton.Visibility = Visibility.Visible;
            QuickUpdateButton.Visibility = Visibility.Collapsed;
            ChangelogChevron.Visibility = Visibility.Collapsed;
            return;
        }

        
        if (Services.Updating.UpdateNotice.Pending is { } found)
        {
            ChangelogCardTitle.Text = $"{found.Tag} 可用";
            ChangelogCardSubtitle.Text = "发现新版本，可立即更新";
            UpdateInstallButton.Visibility = Visibility.Collapsed;
            QuickUpdateButton.Visibility = Visibility.Visible;
            ChangelogChevron.Visibility = Visibility.Collapsed;
            return;
        }

        
        ChangelogCardTitle.Text = "查看更新内容";
        ChangelogCardSubtitle.Text =
            $"当前版本 {ShellConfig.VersionPrefix}{ShellConfig.DisplayVersion} · 已是最新版本";
        UpdateInstallButton.Visibility = Visibility.Collapsed;
        QuickUpdateButton.Visibility = Visibility.Collapsed;
        ChangelogChevron.Visibility = Visibility.Visible;
    }

    
    
    
    
    private void ChangelogCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (UpdateInstallButton.Visibility == Visibility.Visible) return;
        if (QuickUpdateButton.Visibility == Visibility.Visible) return;
        App.MainWindow?.Shell.NavigateTo("changelog");
    }

    
    
    
    
    private void ApplyUpdateHighlight(bool highlight)
    {
        Brush Fill(string key) => (Brush)Application.Current.Resources[key];

        if (highlight)
        {
            ChangelogCard.Background = Fill("SystemFillColorSuccessBackgroundBrush");
            ChangelogIcon.Glyph = "\uE930";                      
            ChangelogIcon.Foreground = Fill("TextFillColorPrimaryBrush");
            ChangelogCardTitle.Foreground = Fill("TextFillColorPrimaryBrush");
            ChangelogCardSubtitle.Foreground = Fill("TextFillColorPrimaryBrush");
            ChangelogChevron.Foreground = Fill("TextFillColorPrimaryBrush");
        }
        else
        {
            ChangelogCard.Background = Fill("AccentFillColorDefaultBrush");
            ChangelogIcon.Glyph = "\uE81C";                      
            ChangelogIcon.Foreground = Fill("TextOnAccentFillColorPrimaryBrush");
            ChangelogCardTitle.Foreground = Fill("TextOnAccentFillColorPrimaryBrush");
            ChangelogCardSubtitle.Foreground = Fill("TextOnAccentFillColorPrimaryBrush");
            ChangelogChevron.Foreground = Fill("TextOnAccentFillColorPrimaryBrush");
        }
    }

    
    
    
    
    
    private async void QuickUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (Services.Updating.UpdateNotice.Pending is not { } release) return;
        if (XamlRoot is not { } root) return;

        
        if (sender is Button b) b.IsEnabled = false;
        try
        {
            var service = Services.Updating.UpdateService.CreateDefault();
            await Services.Updating.UpdateFlow.RunAsync(root, service, release);
        }
        finally
        {
            
            if (sender is Button bb) bb.IsEnabled = true;
        }
    }

    private async void InstallPending_Click(object sender, RoutedEventArgs e)
    {
        
        if (sender is Button b) b.IsEnabled = false;
        await Services.Updating.UpdateFlow.InstallPendingNowAsync();
    }

    private async void Populate()
    {
        
        
        
        
        ShellVersionText.Text = ShellConfig.VersionPrefix + ShellConfig.DisplayVersion;

        RefreshCollectedCount();

        LoadBanner();
        BuildQuickLinks();
        BuildRecommendations();

        
        
        try
        {
            var q = await System.Threading.Tasks.Task.Run(Core.SystemInfo.Gather);
            FillRows(HardwareList, q.Hardware);
            FillRows(SystemList, q.System);
        }
        catch (Exception ex)
        {
            
            PerfLog.Mark("首页系统信息读取失败：" + ex.Message);
        }
    }

    
    
    
    
    
    
    
    
    
    private void RefreshCollectedCount()
    {
        var count = App.Content.Apps.Count;

        if (count <= 0)
        {
            CollectedBar.IsOpen = false;       
            return;
        }

        CollectedBar.Title = $"已收录 {count} 款软件";
        CollectedBar.Message = string.Empty;   
        CollectedBar.IsOpen = true;

        _ = RefreshRecentAddedAsync(count);
    }

    
    
    
    
    
    
    
    
    private async Task RefreshRecentAddedAsync(int collected)
    {
        var added = await NewlyAddedService.GetRecentAsync(TimeSpan.FromHours(48));
        if (added is null || added.Count <= 0) return;

        
        
        var names = added.Ids.Select(id => App.Content.FindById(id)?.Name ?? id).ToList();

        
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ApplyRecentAdded(collected, names));
            return;
        }

        ApplyRecentAdded(collected, names);
    }

    
    
    private void ApplyRecentAdded(int collected, IReadOnlyList<string> names)
    {
        
        if (App.Content.Apps.Count != collected) return;

        CollectedBar.Title = $"已收录 {collected} 款软件";
        CollectedBar.Message = "过去48小时收录 " + names.Count + " 款：" + string.Join("、", names);
        CollectedBar.IsOpen = true;
    }

    
    private void BuildQuickLinks()
    {
        QuickGrid.ItemsSource = QuickLinks.Build(App.Content.Ui);
        ApplyQuickLinkIcons();

        
        DispatcherQueue.TryEnqueue(() => FitQuickRow(QuickGrid.ActualWidth));
    }

    
    private void ApplyQuickLinkIcons()
    {
        if (QuickGrid.ItemsSource is not List<Core.QuickLink> links) return;
        var color = ((SolidColorBrush)ThemeBrush.Get(this, "TextFillColorPrimaryBrush")).Color;
        foreach (var link in links)
            if (!string.IsNullOrEmpty(link.IconSvg))
                link.IconSource = QuickLinkIconLoader.GetIcon(link.IconSvg, color);
    }

    
    
    
    
    
    
    
    
    private void FitQuickRow(double width)
    {
        if (QuickGrid.ItemsPanelRoot is not ItemsWrapGrid panel) return;

        var count = QuickGrid.Items.Count;
        if (count == 0 || width <= 0) return;

        
        
        var itemWidth = Math.Clamp(Math.Floor(width / count) - 1d, 104d, 192d);
        if (Math.Abs(panel.ItemWidth - itemWidth) > 0.5) panel.ItemWidth = itemWidth;
    }

    

    
    private int _recommendSeed;

    
    private List<SoftwareApp> _recommendPicks = new();

    
    private Storyboard? _recommendStoryboard;

    private const double RecommendCardW = 128;     
    private const double RecommendGap = 12;       
    private const double RecommendSpeed = 24;     
    private const double RecommendTopH = 128;     
    private const double RecommendIconSize = 64;  
    private const double RecommendCardRadius = 8; 
    private const double RecommendIconRadius = 12;

    
    
    
    
    private static readonly Windows.UI.Color[] RecommendTileColors =
    {
        Windows.UI.Color.FromArgb(255, 0x1D, 0x9E, 0x75), 
        Windows.UI.Color.FromArgb(255, 0x37, 0x8A, 0xDD), 
        Windows.UI.Color.FromArgb(255, 0xD4, 0x53, 0x7E), 
        Windows.UI.Color.FromArgb(255, 0xE2, 0x4B, 0x4A), 
        Windows.UI.Color.FromArgb(255, 0x7F, 0x77, 0xDD), 
        Windows.UI.Color.FromArgb(255, 0xBA, 0x75, 0x17), 
        Windows.UI.Color.FromArgb(255, 0x18, 0x5F, 0xA5), 
        Windows.UI.Color.FromArgb(255, 0x5F, 0x5E, 0x5A), 
    };

    private void BuildRecommendations()
    {
        var apps = App.Content.Apps;

        _recommendStoryboard?.Stop();
        _recommendStoryboard = null;

        _recommendPicks = apps.Count == 0
            ? new List<SoftwareApp>()
            : PickRecommendations(apps, 12, _recommendSeed);

        if (_recommendPicks.Count == 0)
        {
            RecommendSection.Visibility = Visibility.Collapsed;
            RecommendStrip.Children.Clear();
            return;
        }

        RecommendSection.Visibility = Visibility.Visible;

        
        DispatcherQueue.TryEnqueue(LayoutRecommend);
    }

    private readonly List<SoftwareApp> _iconSubscribed = new();
    private readonly Dictionary<SoftwareApp, List<(Image? Backdrop, Border? Crisp, Border? Monogram)>> _iconLayers = new();

    
    
    
    
    private void LayoutRecommend()
    {
        if (_recommendPicks.Count == 0) return;

        var viewport = RecommendViewport.ActualWidth;
        if (viewport <= 0) return;   

        var unit = RecommendCardW + RecommendGap;
        var perGroup = Math.Max(_recommendPicks.Count,
            (int)Math.Ceiling((viewport + RecommendGap) / unit));

        var group = new List<SoftwareApp>(perGroup);
        for (var i = 0; i < perGroup; i++) group.Add(_recommendPicks[i % _recommendPicks.Count]);

        
        
        foreach (var a in _iconSubscribed) a.PropertyChanged -= OnAppIconChanged;
        _iconSubscribed.Clear();
        _iconLayers.Clear();

        RecommendStrip.Children.Clear();

        
        for (var copy = 0; copy < 2; copy++)
            foreach (var app in group)
                RecommendStrip.Children.Add(BuildRecommendCard(app));

        var distance = group.Count * unit;                 
        var seconds = Math.Max(20, distance / RecommendSpeed);
        StartRecommendScroll(distance, seconds);
    }

    private void StartRecommendScroll(double distance, double seconds)
    {
        
        _recommendStoryboard?.Stop();
        _recommendStoryboard = null;

        var anim = new DoubleAnimation
        {
            From = 0,
            To = -distance,
            Duration = new Duration(TimeSpan.FromSeconds(seconds)),
        };
        Storyboard.SetTarget(anim, RecommendTransform);
        Storyboard.SetTargetProperty(anim, "X");

        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        sb.Children.Add(anim);

        _recommendStoryboard = sb;
        sb.Begin();
    }

    
    
    
    
    
    
    private static Microsoft.UI.Xaml.Media.ImageSource? LocalIcon(SoftwareApp app)
    {
        if (string.IsNullOrWhiteSpace(app.Icon)) return null;
        return AppIconCache.TryGet(app.Icon, out var local, out _) ? local : null;
    }

    
    private void OnAppIconChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SoftwareApp.IconImage)) return;
        if (sender is not SoftwareApp app) return;
        if (!_iconLayers.TryGetValue(app, out var layers)) return;

        var next = app.IconImage;
        if (next is null)
        {
            
            foreach (var (_, _, mono) in layers)
                if (mono is not null) mono.Visibility = Visibility.Visible;
            return;
        }

        foreach (var (backdrop, crisp, mono) in layers)
        {
            if (backdrop is not null) backdrop.Source = next;
            if (crisp is { Background: ImageBrush brush }) brush.ImageSource = next;
            if (mono is not null) mono.Visibility = Visibility.Collapsed;
        }
    }

    private FrameworkElement BuildRecommendCard(SoftwareApp app)
    {
        
        var baseTint = ToCardTint(RecommendTileColors[
            (int)((uint)Core.StableRank.Of(app.Id) % (uint)RecommendTileColors.Length)]);

        var card = new Border
        {
            Width = RecommendCardW,
            CornerRadius = new CornerRadius(RecommendCardRadius),
            
            
            Background = ThemeBrushOf("CardBackgroundFillColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrushOf("CardStrokeColorDefaultBrush"),
        };

        
        var acrylic = new AcrylicBrush
        {
            TintColor = baseTint,
            TintOpacity = 0.18,
            TintLuminosityOpacity = 0.85,
            FallbackColor = baseTint,
        };

        var top = new Border
        {
            Height = RecommendTopH,
            CornerRadius = new CornerRadius(RecommendCardRadius, RecommendCardRadius, 0, 0),
            Background = new SolidColorBrush(baseTint),
        };

        var topGrid = new Grid();

        
        
        
        
        var icon = LocalIcon(app) ?? app.IconImage;
        Image? backdropLayer = null;
        if (icon is { } backdrop)
        {
            backdropLayer = new Image
            {
                Source = backdrop,
                Stretch = Stretch.UniformToFill,
            };
            topGrid.Children.Add(backdropLayer);
        }

        
        topGrid.Children.Add(new Border
        {
            Background = acrylic,
            CornerRadius = new CornerRadius(RecommendCardRadius, RecommendCardRadius, 0, 0),
        });

        
        var monogram = new Border
        {
            Width = RecommendIconSize,
            Height = RecommendIconSize,
            CornerRadius = new CornerRadius(RecommendIconRadius),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(46, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = FirstChar(app.Name),
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        
        
        
        
        monogram.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;
        topGrid.Children.Add(monogram);

        
        Border? crispLayer = null;
        if (icon is { } crisp)
        {
            crispLayer = new Border
            {
                Width = RecommendIconSize,
                Height = RecommendIconSize,
                CornerRadius = new CornerRadius(RecommendIconRadius),
                Background = new ImageBrush { ImageSource = crisp, Stretch = Stretch.UniformToFill },
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            topGrid.Children.Add(crispLayer);
        }

        
        
        if (!_iconLayers.TryGetValue(app, out var layers))
            _iconLayers[app] = layers = new List<(Image? Backdrop, Border? Crisp, Border? Monogram)>();
        layers.Add((backdropLayer, crispLayer, monogram));

        if (!_iconSubscribed.Contains(app))
        {
            app.PropertyChanged += OnAppIconChanged;
            _iconSubscribed.Add(app);
        }

        top.Child = topGrid;

        
        var text = new StackPanel
        {
            Padding = new Thickness(10, 9, 10, 10),
            Spacing = 3,
        };
        text.Children.Add(new TextBlock
        {
            Text = app.Name,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemeBrushOf("TextFillColorPrimaryBrush"),
        });
        text.Children.Add(new TextBlock
        {
            Text = IntroOf(app),
            FontSize = 11,
            Height = 30,
            MaxLines = 2,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemeBrushOf("TextFillColorSecondaryBrush"),
        });

        var content = new StackPanel();
        content.Children.Add(top);
        content.Children.Add(text);
        card.Child = content;

        card.Tapped += (_, _) => Frame.Navigate(typeof(DetailPage), app.Id);
        ToolTipService.SetToolTip(card, app.Name);

        
        _ = ApplyIconColorAsync(app, top, acrylic);

        return card;
    }

    private static readonly System.Net.Http.HttpClient IconHttp =
        new() { Timeout = TimeSpan.FromSeconds(6) };

    private static readonly Dictionary<string, Task<Windows.UI.Color?>> IconColorCache =
        new(StringComparer.OrdinalIgnoreCase);

    
    private async Task ApplyIconColorAsync(SoftwareApp app, Border top, AcrylicBrush acrylic)
    {
        var color = await GetIconColorAsync(app.Icon);
        if (color is not { } c) return;

        var tint = ToCardTint(c);

        DispatcherQueue.TryEnqueue(() =>
        {
            top.Background = new SolidColorBrush(tint);
            acrylic.TintColor = tint;
            acrylic.FallbackColor = tint;
        });
    }

    private static Task<Windows.UI.Color?> GetIconColorAsync(string url)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<Windows.UI.Color?>(null);

        lock (IconColorCache)
        {
            if (!IconColorCache.TryGetValue(url, out var task))
            {
                task = LoadIconColorAsync(url);
                IconColorCache[url] = task;
            }
            return task;
        }
    }

    
    
    
    
    private static async Task<Windows.UI.Color?> LoadIconColorAsync(string url)
    {
        try
        {
            var bytes = await IconHttp.GetByteArrayAsync(url);

            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);

            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);

            const int size = 20;

            
            using var small = await decoder.GetSoftwareBitmapAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                new Windows.Graphics.Imaging.BitmapTransform
                {
                    ScaledWidth = size,
                    ScaledHeight = size,
                    InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant,
                },
                Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);

            var buffer = new byte[size * size * 4];
            small.CopyToBuffer(buffer.AsBuffer());

            long r = 0, g = 0, b = 0, weight = 0;
            for (var i = 0; i < buffer.Length; i += 4)
            {
                var bb = buffer[i];
                var gg = buffer[i + 1];
                var rr = buffer[i + 2];
                var aa = buffer[i + 3];
                if (aa < 40) continue;

                var ur = rr * 255 / aa;
                var ug = gg * 255 / aa;
                var ub = bb * 255 / aa;
                if (ur > 245 && ug > 245 && ub > 245) continue;   
                if (ur < 12 && ug < 12 && ub < 12) continue;      

                var max = Math.Max(ur, Math.Max(ug, ub));
                var min = Math.Min(ur, Math.Min(ug, ub));
                var wgt = (max - min) + 24;                       

                r += (long)ur * wgt;
                g += (long)ug * wgt;
                b += (long)ub * wgt;
                weight += wgt;
            }

            return weight > 0
                ? Windows.UI.Color.FromArgb(255,
                    (byte)(r / weight), (byte)(g / weight), (byte)(b / weight))
                : Windows.UI.Color.FromArgb(255, 90, 90, 90);
        }
        catch
        {
            
            return null;
        }
    }

    
    private static Windows.UI.Color ToCardTint(Windows.UI.Color c) =>
        Windows.UI.Color.FromArgb(255,
            (byte)Math.Clamp(c.R * 0.34 + 14, 0, 255),
            (byte)Math.Clamp(c.G * 0.34 + 14, 0, 255),
            (byte)Math.Clamp(c.B * 0.34 + 14, 0, 255));

    private static string FirstChar(string name)
    {
        var t = name?.Trim() ?? "";
        return t.Length == 0 ? "?" : t[..1].ToUpperInvariant();
    }

    
    
    
    
    
    
    
    
    private static Brush ThemeBrushOf(string key) => ThemeBrush.Get(null, key);

    
    private static string IntroOf(SoftwareApp app)
    {
        if (!string.IsNullOrWhiteSpace(app.Tagline)) return app.Tagline.Trim();
        if (!string.IsNullOrWhiteSpace(app.Description)) return app.Description.Trim();
        return app.CategoryDisplay;
    }

    
    private static List<SoftwareApp> PickRecommendations(IReadOnlyList<SoftwareApp> apps, int count, int seed)
    {
        return apps
            .OrderBy(a => Core.StableRank.Of(a.Id + "#" + seed))
            .Take(count)
            .ToList();
    }

    private void Recommend_Reroll(object sender, RoutedEventArgs e)
    {
        _recommendSeed = Random.Shared.Next();
        BuildRecommendations();
    }

    private void Recommend_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        _recommendStoryboard?.Pause();

    private void Recommend_PointerExited(object sender, PointerRoutedEventArgs e) =>
        _recommendStoryboard?.Resume();

    private void Quick_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not QuickLink link) return;

        
        if (link.Tag.Length > 0)
        {
            App.MainWindow?.Shell.NavigateTo(link.Tag);
            return;
        }

        if (link.Url.Length > 0)
            App.MainWindow?.OpenExternal(link.Url);
    }

    
    private void FillRows(StackPanel panel, IReadOnlyList<SystemInfo.InfoLine> lines)
    {
        panel.Children.Clear();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0 && Resources["InfoRowDivider"] is Style divider)
                panel.Children.Add(new Border { Style = divider });
            panel.Children.Add(BuildRow(lines[i]));
        }
    }

    private static Grid BuildRow(SystemInfo.InfoLine line)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 9, 0, 9) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(148) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new FontIcon
        {
            Glyph = RowGlyphs.TryGetValue(line.Label, out var glyph) ? glyph : "\uE946",
            FontSize = 14,
            Opacity = 0.75,
            Width = 20,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var label = new TextBlock
        {
            Text = line.Label,
            FontSize = 12,
            Opacity = 0.65,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var value = new TextBlock
        {
            Text = line.Value,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = true,
        };
        ToolTipService.SetToolTip(value, line.Value);

        Grid.SetColumn(icon, 0);
        Grid.SetColumn(label, 1);
        Grid.SetColumn(value, 2);
        row.Children.Add(icon);
        row.Children.Add(label);
        row.Children.Add(value);
        return row;
    }

    
    private static readonly Dictionary<string, string> RowGlyphs = new()
    {
        ["处理器"] = "\uEEA1",   
        ["内存"] = "\uEEA0",     
        ["硬盘"] = "\uEDA2",     
        ["触摸"] = "\uEDA4",     
        ["显卡"] = "\uE7F4",     
        ["显存"] = "\uE714",     
        ["显示器"] = "\uE7F3",   
        ["操作系统"] = "\uE770",  
        ["系统版本"] = "\uE946",  
        ["安装日期"] = "\uE787",  
        ["虚拟内存"] = "\uEDA2",  
        ["虚拟化"] = "\uEEA3",   
        ["DirectX"] = "\uE7FC",  
    };

    private void LoadBanner()
    {
        try
        {
            
            
            
            var path = EmbeddedAssets.ExtractToCache("dv1.2.png", "banner.png");
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            {
                
                
                Banner.Source = new BitmapImage(new Uri(path)) { DecodePixelHeight = 280 };
            }
        }
        catch {  }
    }

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement el) el.Opacity = 0.88;
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement el) el.Opacity = 1.0;
    }

    private void Card_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag as string;
        switch (tag)
        {
            case "web":
                
                
                App.MainWindow?.OpenExternal(ShellConfig.SiteUrl);
                break;
            case "apps":
                App.MainWindow?.Shell.NavigateTo("apps");
                break;
            case "tools-classroom":
                
                App.MainWindow?.Shell.NavigateToClassroomTools();
                break;
            case "tools-system":
                App.MainWindow?.Shell.NavigateToSystemTools();
                break;
            case "hubai":
                App.MainWindow?.Shell.NavigateTo("hubai");
                break;
            case "sidebar":
                App.MainWindow?.Shell.NavigateTo("sidebar");
                break;
            case "announce":
                
                App.MainWindow?.Shell.NavigateTo("announce");
                break;
            case "function-plot":
                
                
                
                App.MainWindow?.Shell.NavigateTo("function-plot");
                break;
            case "experimental":
                
                App.MainWindow?.Shell.NavigateToExperimental();
                break;
            case "submit":
                
                ClassSoftwareHub.Desktop.Views.Submit.SubmitWizardWindow.Show();
                break;
            case "feedback":
                App.MainWindow?.Shell.NavigateTo("feedback");
                break;
            case "settings":
                App.MainWindow?.Shell.NavigateTo("settings");
                break;
            case "changelog":
                
                App.MainWindow?.Shell.NavigateTo("changelog");
                break;
        }
    }
}
