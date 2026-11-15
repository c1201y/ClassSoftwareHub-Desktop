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

namespace ClassSoftwareHub.Desktop.Pages;
























public sealed partial class AboutPage : Page
{
    
    private LoadedImageSurface? _bannerSurface;

    
    private SpriteVisual? _bannerVisual;
    private int _appInfoTaps;
    private bool _eggShowing;
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
                _appInfoTaps = 0;
            };
        }

        return _appInfoTapTimer;
    }

    private async void AppInfoCard_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(AppInfoCard).Properties.IsLeftButtonPressed) return;
        _appInfoTaps++;
        var timer = EnsureAppInfoTapTimer();
        timer.Stop();
        timer.Start();

        if (_appInfoTaps < 5 || _eggShowing) return;

        _appInfoTaps = 0;
        timer.Stop();
        _eggShowing = true;

        try
        {
            await ShowEggDialogAsync();
        }
        finally
        {
            _eggShowing = false;
        }
    }

    private async Task ShowEggDialogAsync()
    {
        var first = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "彩蛋",
            Content = new TextBlock
            {
                Text = "恭喜你发现了一个彩蛋",
                FontSize = 20,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 8),
            },
            CloseButtonText = "确定",
        };
        await first.ShowAsync();

        var poem = new Button
        {
            Content = new TextBlock
            {
                Text = "欲买桂花同载酒，终不似，少年游",
                FontSize = 21,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                LineHeight = 34,
            },
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(16, 14, 16, 14),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var second = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "彩蛋",
            Content = poem,
            CloseButtonText = "关闭",
        };
        poem.Click += (_, _) =>
        {
            second.Hide();
            Views.StarfieldWindow.Show();
        };
        await second.ShowAsync();
    }

    private void Load()
    {
        LoadBanner();
        LoadAppIcon();

        VersionText.Text = ShellConfig.VersionPrefix + ShellConfig.DisplayVersion;
        CopyrightText.Text =
            "Copyright © 2026 c1201y\n本项目基于 GNU General Public License v3.0 许可发布。";

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
