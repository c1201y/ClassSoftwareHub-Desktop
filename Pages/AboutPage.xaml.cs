using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Services.License;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Dispatching;
using Windows.Foundation;

namespace ClassSoftwareHub.Desktop.Pages;
























public sealed partial class AboutPage : Page
{
    
    private LoadedImageSurface? _bannerSurface;

    
    private SpriteVisual? _bannerVisual;
    private int _appInfoPressCount;
    private bool _appInfoOverlayOpen;
    private DispatcherQueueTimer? _appInfoTapTimer;

    public AboutPage()
    {
        InitializeComponent();
        AppInfoCard.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(AppInfoCard_PointerPressed), true);

        
        Loaded += (_, _) => Load();
    }

    private DispatcherQueueTimer EnsureAppInfoTapTimer()
    {
        if (_appInfoTapTimer is null)
        {
            _appInfoTapTimer = DispatcherQueue.CreateTimer();
            _appInfoTapTimer.IsRepeating = false;
            _appInfoTapTimer.Interval = TimeSpan.FromMilliseconds(900);
            _appInfoTapTimer.Tick += (_, _) =>
            {
                _appInfoTapTimer!.Stop();
                _appInfoPressCount = 0;
            };
        }

        return _appInfoTapTimer;
    }

    private async void AppInfoCard_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(AppInfoCard).Properties.IsLeftButtonPressed) return;
        _appInfoPressCount++;
        var timer = EnsureAppInfoTapTimer();
        timer.Stop();
        timer.Start();

        if (_appInfoPressCount < 5 || _appInfoOverlayOpen) return;

        _appInfoPressCount = 0;
        timer.Stop();
        _appInfoOverlayOpen = true;

        try
        {
            await ShowAppInfoOverlayAsync();
        }
        finally
        {
            _appInfoOverlayOpen = false;
        }
    }

    private async Task ShowAppInfoOverlayAsync()
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(80, 255, 208, 124)),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 208, 124)),
            Child = new FontIcon
            {
                Glyph = "\uE734",
                FontSize = 18,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 208, 124)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        header.Children.Add(new TextBlock
        {
            Text = "CLASS SOFTWARE HUB",
            FontSize = 10,
            CharacterSpacing = 150,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 150, 177, 220)),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var title = new TextBlock
        {
            Text = "有些东西，只有留下来的人才看得到",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 228, 237, 255)),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 30,
            Margin = new Thickness(0, 14, 0, 12),
        };

        var poem = new TextBlock
        {
            Text = "欲买桂花同载酒\n终不似，少年游",
            FontSize = 19,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 214, 140)),
            TextAlignment = TextAlignment.Center,
            LineHeight = 32,
            Margin = new Thickness(0, 14, 0, 14),
        };

        var poemCard = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(32, 255, 255, 255)),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(14, 255, 255, 255)),
            Child = poem,
        };

        var body = new TextBlock
        {
            Text = "继续往前走，星海还在。",
            FontSize = 12,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(160, 176, 195, 226)),
            Margin = new Thickness(0, 12, 0, 0),
        };

        var stack = new StackPanel
        {
            Width = 420,
            Spacing = 0,
        };
        stack.Children.Add(header);
        stack.Children.Add(title);
        stack.Children.Add(poemCard);
        stack.Children.Add(body);

        var card = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(24),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(36, 255, 255, 255)),
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Windows.UI.Color.FromArgb(255, 12, 19, 36), Offset = 0 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(255, 7, 11, 23), Offset = 0.55 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(255, 4, 6, 13), Offset = 1 },
                },
            },
            Child = stack,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "ClassSoftwareHub",
            Content = card,
            PrimaryButtonText = "去看看星海",
            CloseButtonText = "留在这里",
            DefaultButton = ContentDialogButton.Primary,
        };

        try
        {
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
                Views.AboutAmbientWindow.Show();
        }
        catch (Exception ex)
        {
            AppLog.Warn("about", "关于入口弹层打开失败：" + ex.Message);
        }
    }

    private void Load()
    {
        LoadBanner();
        LoadAppIcon();

        VersionText.Text = ShellConfig.VersionPrefix + ShellConfig.DisplayVersion;
        CopyrightText.Text =
            "作者：ClassSoftwareHub开发团队\nCopyright © 2026 c1201y\n本项目基于 GNU General Public License v3.0 许可发布。";

        RefreshLicense();
        RefreshLegalCards();

        
        
        CollapseEchoCave();
        EchoCaveExpander.Loaded += (_, _) => CollapseEchoCave();

        var settle = DispatcherQueue.CreateTimer();
        settle.Interval = TimeSpan.FromMilliseconds(200);
        settle.IsRepeating = false;
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            CollapseEchoCave();
        };
        settle.Start();
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private void LoadBanner()
    {
        try
        {
            var path = EmbeddedAssets.ExtractToCache("about-banner.jpg", "about-banner.jpg");
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                AppLog.Warning("about", $"横幅取不到：{path ?? "(空)"}");
                return;
            }

            var compositor = ElementCompositionPreview.GetElementVisual(BannerHost).Compositor;

            _bannerSurface = LoadedImageSurface.StartLoadFromUri(new Uri(path));
            var surfaceBrush = compositor.CreateSurfaceBrush(_bannerSurface);
            surfaceBrush.Stretch = CompositionStretch.UniformToFill;
            surfaceBrush.HorizontalAlignmentRatio = 0.5f;
            surfaceBrush.VerticalAlignmentRatio = 0f;      

            var fade = compositor.CreateLinearGradientBrush();
            fade.StartPoint = new Vector2(0f, 0.45f);
            fade.EndPoint = new Vector2(0f, 1f);
            fade.ColorStops.Add(compositor.CreateColorGradientStop(0f, Colors.White));
            fade.ColorStops.Add(compositor.CreateColorGradientStop(1f, Colors.Transparent));

            var masked = compositor.CreateMaskBrush();
            masked.Source = surfaceBrush;
            masked.Mask = fade;

            _bannerVisual = compositor.CreateSpriteVisual();
            _bannerVisual.Brush = masked;
            ElementCompositionPreview.SetElementChildVisual(BannerHost, _bannerVisual);

            ResizeBanner();
            BannerHost.SizeChanged += (_, _) => ResizeBanner();

            AppLog.Step("about", $"横幅已载入：{path}");
        }
        catch (Exception ex)
        {
            AppLog.Warning("about", "横幅载入失败：" + ex.Message);
        }
    }

    private void ResizeBanner()
    {
        if (_bannerVisual is null) return;
        _bannerVisual.Size = new Vector2((float)BannerHost.ActualWidth, (float)BannerHost.ActualHeight);
    }

    
    private void LoadAppIcon()
    {
        try
        {
            var path = EmbeddedAssets.ExtractToCache("AppIcon-512.png", "about-icon.png");
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;

            AppIcon.Source = new BitmapImage(new Uri(path)) { DecodePixelWidth = 64 };
        }
        catch { }
    }

    

    private void OpenFeedback_Click(object sender, RoutedEventArgs e)
        => App.MainWindow?.Shell.NavigateTo("feedback");

    private void OpenChangelog_Click(object sender, RoutedEventArgs e)
        => App.MainWindow?.Shell.NavigateTo("changelog");

    
    private void OpenLogs_Click(object sender, RoutedEventArgs e)
        => App.MainWindow?.Shell.NavigateToSettings("logs");

    

    
    
    
    
    private static string UrlOf(string name)
    {
        try
        {
            return QuickLinks.Build(App.Content.Ui).FirstOrDefault(l => l.Name == name)?.Url ?? "";
        }
        catch { return ""; }
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        App.MainWindow?.OpenExternal(url);
    }

    private void OpenRepo_Click(object sender, RoutedEventArgs e) => OpenUrl(UrlOf("项目仓库"));

    private void OpenAuthor_Click(object sender, RoutedEventArgs e) => OpenUrl(UrlOf("作者主页"));

    private void OpenGroup_Click(object sender, RoutedEventArgs e) => OpenUrl(UrlOf("加入Q群"));

    private void OpenSponsor_Click(object sender, RoutedEventArgs e) => OpenUrl(UrlOf("赞助作者"));

    
    private void OpenLicenseText_Click(object sender, RoutedEventArgs e)
        => OpenUrl($"https://github.com/{ShellConfig.UpdateRepoOwner}/{ShellConfig.UpdateRepoName}/blob/main/LICENSE.txt");

    
    private void OpenPrivacy_Click(object sender, RoutedEventArgs e) => Views.LegalWindow.Show("privacy");

    

    
    
    
    
    
    
    
    
    
    
    
    
    private void RefreshLicense()
    {
        try
        {
            var ticket = LicenseStore.Current;

            if (ticket is null && BuildIdentity.IsOfficial)
            {
                LicenseStatus.Text = "官方版本，在线服务可直接使用。";
                LicenseActionButton.Visibility = Visibility.Collapsed;
                return;
            }

            LicenseActionButton.Visibility = Visibility.Visible;

            if (ticket is not null)
            {
                var activated = ticket.ActivatedAtLocal;
                LicenseStatus.Text = activated is null
                    ? "已激活。"
                    : $"已激活，激活时间 {activated:yyyy-MM-dd}。";
                LicenseActionButton.Content = "更换激活码";
                return;
            }

            LicenseStatus.Text = "未激活。输入激活码后可使用投稿、反馈、喊话等在线服务。";
            LicenseActionButton.Content = "输入激活码";
        }
        catch (Exception ex)
        {
            AppLog.Exception("license", ex, "刷新授权状态");
            LicenseStatus.Text = "无法读取本机的授权状态。";
        }
    }

    private async void LicenseAction_Click(object sender, RoutedEventArgs e)
    {
        
        await Views.LicenseDialog.ShowAsync(XamlRoot);
        RefreshLicense();
    }

    

    
    private void EchoCave_Click(object sender, RoutedEventArgs e) => EchoCave.ShowNext();

    
    
    
    
    
    
    private void CollapseEchoCave()
    {
        
        
        if (!EchoCaveExpander.IsExpanded) EchoCaveExpander.IsExpanded = true;
        EchoCaveExpander.IsExpanded = false;

        var inner = FindDescendant<Expander>(EchoCaveExpander);
        if (inner is not null)
        {
            if (!inner.IsExpanded) inner.IsExpanded = true;
            inner.IsExpanded = false;
        }
    }

    
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            var deeper = FindDescendant<T>(child);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    
    
    
    
    private async void SubmitEchoCave_Click(object sender, RoutedEventArgs e)
    {
        var text = EchoSubmitBox.Text.Trim();
        if (text.Length == 0)
        {
            ShowEchoSubmitStatus("还没写内容。");
            return;
        }

        EchoSubmitButton.IsEnabled = false;
        EchoSubmitButton.Content = "正在提交";
        ShowEchoSubmitStatus("正在提交…");

        var (ok, message) = await EchoCaveService.SubmitAsync(text);

        EchoSubmitButton.IsEnabled = true;
        EchoSubmitButton.Content = "提交";
        ShowEchoSubmitStatus(message);
        EchoSubmitFallback.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;

        if (ok)
        {
            EchoSubmitBox.Text = "";

            
            
            await Task.Delay(1800);
            if (EchoSubmitStatus.Text == message) EchoSubmitFlyout.Hide();
        }
    }

    private async void SubmitEchoCaveFallback_Click(object sender, RoutedEventArgs e)
    {
        if (await EchoCave.OpenSubmitPage()) EchoSubmitFlyout.Hide();
        else ShowEchoSubmitStatus("无法打开浏览器，请手动访问 GitHub 投稿。");
    }

    private void ShowEchoSubmitStatus(string message)
    {
        EchoSubmitStatus.Text = message;
        EchoSubmitStatus.Visibility = Visibility.Visible;
    }

    
    
    
    
    

    
    private void RefreshLegalCards()
    {
        try
        {
            Bind(LegalAgreementCard, "agreement", "使用本站（网站与桌面客户端）前请完整阅读");
            Bind(LegalPrivacyCard, "privacy", "本站怎么处理你的信息、哪些存在本机");
            Bind(LegalDisclaimerCard, "disclaimer", "软件来源、下载安装风险与责任范围");

            void Bind(CommunityToolkit.WinUI.Controls.SettingsCard card, string key, string blurb)
            {
                var doc = LegalTextSource.Find(key);
                if (doc is null) return;
                card.Header = doc.Title;
                card.Description = $"{blurb} · {doc.VersionLine}";
                card.Tag = doc.Key;                       
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("legal", "条款卡片刷新失败：" + ex.Message);
        }
    }

    
    private void OpenLegal_Click(object sender, RoutedEventArgs e)
    {
        var key = (sender as FrameworkElement)?.Tag as string;
        Views.LegalWindow.Show(key);
    }
}
