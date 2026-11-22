using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;

namespace ClassSoftwareHub.Desktop.Pages;









public sealed partial class DetailPage : Page
{
    private string _storeUrl = "";
    private string _shareLink = "";
    private Button? _lastCopyButton;
    private DispatcherQueueTimer? _copyTimer;
    private SoftwareApp? _app;

    public DetailPage()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            
            _copyTimer?.Stop();
            DetachApp();
        };
    }

    
    
    
    
    
    
    
    
    
    
    
    /// <summary>分享链接固定指向主域网页，镜像域名不参与分享。</summary>
    private static string ShareLinkFor(string id)
        => "https://classsoftwarehub.cn/#/download/" + Uri.EscapeDataString(id);

    private async void Share_Click(object sender, RoutedEventArgs e)
    {
        if (_app is null || _shareLink.Length == 0) return;

        var qr = Services.QrCodeImage.Render(_shareLink);

        // 二维码必须白底才扫得出，不跟主题走。
        var paper = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Image
            {
                Source = qr.Bitmap,
                Width = 232,
                Height = 232,
                Stretch = Stretch.Uniform,
            },
        };

        var body = new StackPanel { Spacing = 12, Width = 300 };
        body.Children.Add(paper);

        body.Children.Add(new TextBlock
        {
            Text = _shareLink,
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            IsTextSelectionEnabled = true,
        });

        body.Children.Add(new TextBlock
        {
            Text = "用手机扫码打开这个页面",
            FontSize = 12,
            Opacity = 0.55,
            TextAlignment = TextAlignment.Center,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _app.Name,
            Content = body,
            PrimaryButtonText = "复制链接",
            SecondaryButtonText = "保存二维码",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
        };

        // 复制后留在弹窗里，方便接着保存；按钮文字临时反馈一下。
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            if (CopyShareLink()) dialog.PrimaryButtonText = "已复制";
        };

        dialog.SecondaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            await SaveQrAsync(qr);
        };

        await dialog.ShowAsync();
    }

    private bool CopyShareLink()
    {
        try
        {
            var data = new DataPackage();
            data.SetText(_shareLink);
            Clipboard.SetContent(data);
            Clipboard.Flush();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("detail", "复制分享链接失败：" + ex.Message);
            return false;
        }
    }

    private async Task SaveQrAsync(Services.QrRenderResult qr)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = "csh-" + (_app?.Id ?? "software"),
            };
            picker.FileTypeChoices.Add("PNG 图片", new List<string> { ".png" });
            WindowHandle.BindPicker(picker, this);

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            var png = await Services.QrCodeImage.EncodePngAsync(qr.Pixels, qr.Width, qr.Height);
            await FileIO.WriteBytesAsync(file, png);
        }
        catch (Exception ex)
        {
            AppLog.Warn("detail", "保存分享二维码失败：" + ex.Message);
        }
    }

    private void DetachApp()
    {
        if (_app is null) return;
        _app.PropertyChanged -= App_PropertyChanged;
        _app = null;
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        DetachApp();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        
        if (_app is not null) _app.PropertyChanged -= App_PropertyChanged;

        var ui = App.Content.Ui;
        IntroTitle.Text = ui.T("detail.intro", "应用介绍");
        InfoTitle.Text = ui.T("detail.info", "详细信息");
        DownloadsTitle.Text = ui.T("detail.downloads", "下载");
        StoreTitle.Text = ui.T("detail.store-title", "在 Microsoft Store 中获取");
        StoreDesc.Text = ui.T("detail.store-desc", "由微软商店托管，安装后自动更新，无需手动下载安装包。");
        StoreButton.Content = ui.T("detail.store-button", "获取");
        StoreOnlyHint.Text = ui.T("detail.store-only", "该软件通过 Microsoft Store 分发，单击上方按钮打开商店页面即可获取");

        
        if (StoreLogo.Source is null) StoreLogo.Source = LoadStoreLogo();

        
        var app = App.Content.FindById(e.Parameter as string ?? "");
        if (app is null)
        {
            AppName.Text = ui.T("detail.not-found", "未找到该软件。");
            AppTagline.Visibility = Visibility.Collapsed;
            CategoryBadge.Visibility = Visibility.Collapsed;
            HeroMeta.Visibility = Visibility.Collapsed;
            IntroTitle.Visibility = Visibility.Collapsed;
            AppDescription.Visibility = Visibility.Collapsed;
            ShareCard.Visibility = Visibility.Collapsed;
            InfoTitle.Visibility = Visibility.Collapsed;
            DownloadsTitle.Visibility = Visibility.Collapsed;
            Rail.Visibility = Visibility.Collapsed;
            RailCol.Width = new GridLength(0);
            return;
        }

        
        AppName.Text = app.Name;
        AppTagline.Text = app.Tagline;
        AppTagline.Visibility = app.Tagline.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        
        ShareTitle.Text = ui.T("detail.share-title", "分享这个软件");
        ShareButtonText.Text = ui.T("detail.share", "分享");
        _shareLink = ShareLinkFor(app.Id);
        ShareUrlText.Text = _shareLink;

        var category = app.CategoryDisplay.Length > 0 ? app.CategoryDisplay : App.Content.CategoryName(app.Category);
        CategoryText.Text = category;
        CategoryBadge.Visibility = category.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        
        _app = app;
        app.PropertyChanged += App_PropertyChanged;
        IconBox.Background = MakeIconBrush(app);
        IconFallback.Visibility = app.IconPlaceholder;

        
        AppDescription.Text = app.Description;
        AppDescription.Visibility = app.Description.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        IntroTitle.Visibility = AppDescription.Visibility;

        
        
        
        if (app.HasOssDownload)
        {
            OssBar.Title = ui.T("detail.oss-title", "来源说明");
            OssBar.Message = ui.T("detail.oss-notice",
                "文件由投稿者上传，本站未作修改，请自行核对来源与安全性。");
            OssBar.IsOpen = true;
            OssBar.Visibility = Visibility.Visible;
        }
        else
        {
            OssBar.IsOpen = false;
            OssBar.Visibility = Visibility.Collapsed;
        }

        
        if (app.Notice.Length > 0)
        {
            NoticeBar.Title = ui.T("detail.notice-title", "小提示");
            NoticeBar.Message = app.Notice;
            NoticeBar.IsOpen = true;
            NoticeBar.Visibility = Visibility.Visible;
        }
        else
        {
            NoticeBar.IsOpen = false;
            NoticeBar.Visibility = Visibility.Collapsed;
        }

        
        InfoList.Children.Clear();
        AddInfoRow(ui.T("detail.version", "软件版本"), app.Version);
        AddInfoRow(ui.T("detail.size", "软件体积"), app.Size);
        AddInfoRow(ui.T("detail.system", "系统限制"), app.System);
        AddLinkRow(ui.T("detail.website", "官方网站"), app.Website);
        AddLinkRow(ui.T("detail.github", "GitHub"), app.Github);
        
        
        AddOptionalRow(ui.T("detail.author", "开发者"), app.Author);
        AddOptionalRow(ui.T("detail.license", "开源协议"), app.License);

        
        
        _storeUrl = app.Store.Length > 0
            ? app.Store
            : app.Downloads.FirstOrDefault(d => d.Kind == DownloadKind.Store)?.Url ?? "";
        var others = app.Downloads.Where(d => d.Kind != DownloadKind.Store).ToList();

        StoreCard.Visibility = _storeUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        StoreOnlyHint.Visibility = _storeUrl.Length > 0 && others.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DownloadList.ItemsSource = others;

        
        var isStoreItself = Services.StoreRepair.IsStoreItself(app.Id, _storeUrl);
        if (isStoreItself && _storeUrl.Length > 0)
        {
            StoreOnlyHint.Text = "本机未安装 Microsoft Store 时，「打开」将无响应，此时可选择「一键恢复商店」。";
            StoreOnlyHint.Visibility = Visibility.Visible;
        }

        
        var issues = App.Content.Issues
            .Where(i => i.File.Contains(app.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (issues.Count > 0)
        {
            IssueBar.Title = "该软件的数据存在提示";
            IssueBar.Message = string.Join("\n", issues.Select(i => i.File + "：" + i.Message));
            IssueBar.IsOpen = true;
        }

        
        var meta = string.Join("　·　", new[] { app.VersionText, app.Size, app.System }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        HeroMeta.Text = meta;
        HeroMeta.Visibility = meta.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        BuildRecommendations(app);
        _ = ApplyHeroColorAsync(app);
    }

    
    
    
    
    
    private void BuildRecommendations(SoftwareApp app)
    {
        var pool = App.Content.Apps
            .Where(a => !string.Equals(a.Id, app.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var same = pool
            .Where(a => string.Equals(a.Category, app.Category, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => Core.StableRank.Of(a.Id))
            .ToList();

        var picks = same.Take(6).ToList();

        if (picks.Count < 6)
        {
            picks.AddRange(pool
                .Except(picks)
                .OrderBy(a => Core.StableRank.Of(a.Id + "|rest"))
                .Take(6 - picks.Count));
        }

        RecommendList.ItemsSource = picks;
        RecommendList.Visibility = picks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RailEmpty.Visibility = picks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Recommend_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SoftwareApp target) return;
        Frame.Navigate(typeof(DetailPage), target.Id);
    }

    
    
    
    
    
    
    
    
    private async Task ApplyHeroColorAsync(SoftwareApp app)
    {
        if (Services.IconPalette.TryGetCached(app.Icon, out var ready))
        {
            PaintHero(app, ready, fade: false);
            return;
        }

        var picked = await Services.IconPalette.PickAsync(app.Icon);

        if (picked is { } color)
        {
            PaintHero(app, color, fade: true);
            return;
        }

        
        var fallback = (Services.ThemeBrush.Get(this, "AccentFillColorDefaultBrush") as SolidColorBrush)?.Color
                       ?? Color.FromArgb(255, 0, 95, 184);
        PaintHero(app, fallback, fade: false);
    }

    private void PaintHero(SoftwareApp app, Color color, bool fade)
    {
        
        if (_app is null || !string.Equals(_app.Id, app.Id, StringComparison.OrdinalIgnoreCase)) return;

        Hero.Background = Services.IconPalette.Gradient(color);

        if (!fade)
        {
            Hero.Opacity = 1;
            return;
        }

        var anim = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(260)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, Hero);
        Storyboard.SetTargetProperty(anim, "Opacity");

        var story = new Storyboard();
        story.Children.Add(anim);
        story.Begin();
    }

    
    private void AddInfoRow(string label, string value)
    {
        var text = new TextBlock
        {
            Text = value.Length > 0 ? value : App.Content.Ui.T("detail.pending", "待补充"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = true,
            Opacity = value.Length > 0 ? 1.0 : 0.6,
        };
        InfoList.Children.Add(InfoRow(label, text));
    }

    
    
    
    
    
    
    private void AddOptionalRow(string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        InfoList.Children.Add(InfoRow(label, new TextBlock
        {
            Text = value.Trim(),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = true,
        }));
    }

    private void AddLinkRow(string label, string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var link = new HyperlinkButton { Content = url, Padding = new Thickness(0), Tag = url };
        link.Click += (s, _) => { if (((HyperlinkButton)s).Tag is string u) Open(u); };
        InfoList.Children.Add(InfoRow(label, link));
    }

    
    
    
    
    
    private Border InfoRow(string label, FrameworkElement value)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            Opacity = 0.58,
            VerticalAlignment = VerticalAlignment.Center,
        });

        Grid.SetColumn(value, 1);
        grid.Children.Add(value);

        var first = InfoList.Children.Count == 0;

        return new Border
        {
            Padding = new Thickness(0, 13, 0, 13),
            BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0),
            BorderBrush = Services.ThemeBrush.Get(this, "DividerStrokeColorDefaultBrush"),
            Child = grid,
        };
    }

    
    
    
    
    
    private static Microsoft.UI.Xaml.Media.ImageSource? LoadStoreLogo()
    {
        try
        {
            var path = Services.EmbeddedAssets.ExtractToCache("StoreLogo.png", "store-logo.png");
            return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));
        }
        catch (Exception ex)
        {
            AppLog.Warn("detail", "商店图标加载失败：" + ex.Message);
            return null;
        }
    }

    private async void Store_Click(object sender, RoutedEventArgs e)
    {
        if (_storeUrl.Length == 0) return;

        
        if (Services.StoreRepair.IsStoreItself(_app?.Id, _storeUrl))
        {
            await ShowStoreSelfDialogAsync();
            return;
        }

        
        App.MainWindow?.OpenStore(_storeUrl);
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is DownloadItem item)
            OpenDownload(item, el);
    }

    
    
    
    
    
    private void DownloadSplit_Click(SplitButton sender, SplitButtonClickEventArgs args)
    {
        if (sender.DataContext is DownloadItem item)
            OpenDownload(item, sender);
    }

    
    
    
    
    
    
    
    
    
    
    private DownloadItem? _menuRow;

    private void DownloadMenu_Opening(object? sender, object e)
    {
        var flyout = sender as Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase;
        _menuRow = (flyout?.Target as FrameworkElement)?.DataContext as DownloadItem;

        if (_menuRow is null && flyout is MenuFlyout { Items.Count: > 0 } menu && menu.Items[0] is FrameworkElement first)
            _menuRow = first.DataContext as DownloadItem;
    }

    
    private async void DownloadTo_Click(object sender, RoutedEventArgs e)
    {
        var url = (sender as FrameworkElement)?.Tag as string;
        if (string.IsNullOrEmpty(url)) url = _menuRow?.Url ?? "";
        if (url.Length == 0) return;

        var folder = await PickFolderAsync();
        if (string.IsNullOrEmpty(folder)) return;

        App.MainWindow?.DownloadFile(url, DownloadNameFor(_menuRow), folder);
    }

    private void OpenDownloadFolder_Click(object sender, RoutedEventArgs e)
        => Services.DownloadService.OpenDownloadsFolder();

    
    private async Task<string?> PickFolderAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
            };
            picker.FileTypeFilter.Add("*");

            
            WindowHandle.BindPicker(picker, this);

            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception ex)
        {
            AppLog.Warn("download", "选择下载文件夹失败：" + ex.Message);
            return null;
        }
    }

    
    
    
    
    
    
    
    private async void OpenDownload(DownloadItem item, FrameworkElement? source = null)
    {
        var url = item.Url;
        if (url.Length == 0) return;

        if (Services.StoreRepair.IsStoreItself(_app?.Id, url))
        {
            await ShowStoreSelfDialogAsync();
            return;
        }

        if (item.Kind == DownloadKind.File)
            App.MainWindow?.DownloadFile(url, DownloadNameFor(source is null ? null : item));
        else
            App.MainWindow?.ShowWebSheet(url, item.Kind == DownloadKind.Netdisk ? "网盘分享" : "官网下载");
    }

    
    
    
    private async Task ShowStoreSelfDialogAsync()
    {
        var dialog = new ContentDialog
        {
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
            XamlRoot = XamlRoot,
            Title = "Microsoft Store（微软商店）",
            Content = new TextBlock
            {
                Text = "若本机未安装 Microsoft Store，商店链接将无法打开：安装过程依赖商店自身。\n\n" +
                       "· 打开微软商店：跳转到「Microsoft Store」应用\n" +
                       "· 未安装或已损坏：调用系统自带方式重新安装（约 1 至 2 分钟，期间可能出现命令行窗口，请勿关闭）",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "打开微软商店",
            SecondaryButtonText = "一键恢复商店",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                App.MainWindow?.OpenStore(_storeUrl);
                break;

            case ContentDialogResult.Secondary:
                if (Services.StoreRepair.Reinstall())
                {
                    await new ContentDialog
                    {
                                        RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
                        XamlRoot = XamlRoot,
                        Title = "已开始安装微软商店",
                        Content = new TextBlock
                        {
                            Text = "如出现命令行窗口，请等待其自动关闭（约 1 至 2 分钟）。\n" +
                                   "安装完成后，Microsoft Store 会出现在开始菜单中，返回此处再次单击「打开微软商店」即可。",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        CloseButtonText = "确定",
                    }.ShowAsync();
                }
                else
                {
                    await new ContentDialog
                    {
                                        RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
                        XamlRoot = XamlRoot,
                        Title = "无法启动安装",
                        Content = new TextBlock
                        {
                            Text = "可在开始菜单中搜索 Microsoft Store，或用浏览器访问 " +
                                   "https://apps.microsoft.com/detail/9wzdncrfjbmp 手动安装。",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        CloseButtonText = "确定",
                    }.ShowAsync();
                }
                break;
        }
    }

    
    private string? DownloadNameFor(DownloadItem? item)
    {
        if (_app is null) return null;

        var platform = item?.Platform ?? "";

        
        var name = _app.Name;
        if (!string.IsNullOrWhiteSpace(platform)) name += " " + platform;
        if (!string.IsNullOrWhiteSpace(_app.Version)) name += " " + _app.Version;
        return name.Trim();
    }

    
    private void CopyHash_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string hash || hash.Length == 0) return;

        try
        {
            var package = new DataPackage();
            package.SetText(hash);
            Clipboard.SetContent(package);
        }
        catch
        {
            return; 
        }

        b.Content = App.Content.Ui.T("detail.hash-copied", "已复制");
        _lastCopyButton = b;

        _copyTimer ??= DispatcherQueue.CreateTimer();
        _copyTimer.Interval = TimeSpan.FromSeconds(2);
        _copyTimer.IsRepeating = false;
        _copyTimer.Tick -= CopyTimer_Tick;
        _copyTimer.Tick += CopyTimer_Tick;
        _copyTimer.Start();
    }

    
    
    
    
    
    private void HashCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string hash || hash.Length == 0) return;

        try
        {
            App.MainWindow?.Shell.NavigateToTool(
                typeof(Tools.EncodingToolPage),
                new Tools.HashCheckRequest(hash, _app?.Name ?? ""),
                viaList: false);
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("detail", ex, "跳到「编码 / 哈希工具」的文件模式");
        }
    }

    private void CopyTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_lastCopyButton?.DataContext is DownloadItem item)
            _lastCopyButton.Content = item.HashChipText;
        _lastCopyButton = null;
    }

    
    private void Mirror_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string url || url.Length == 0) return;

        var ui = App.Content.Ui;
        var panel = new StackPanel { Spacing = 8, MaxWidth = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = ui.T("detail.mirror-desc", "下列镜像站将上方链接原样转发，国内下载速度通常显著提升。"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
            FontSize = 12.5,
        });

        var flyout = new Flyout { Content = panel };

        void AddChannelButton(string name, string target)
        {
            var button = new Button
            {
                Content = name,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Tag = target,
            };
            button.Click += (s, _) =>
            {
                
                
                
                if (((Button)s).Tag is string u)
                    OpenDownload(new DownloadItem { Url = u, DeclaredKind = "file" }, b);
                flyout.Hide();
            };
            panel.Children.Add(button);
        }

        foreach (var channel in GithubMirror.Channels)
            AddChannelButton(channel.Name, GithubMirror.MirrorUrl(url, channel));

        
        
        if (Core.GithubIpRelay.CanRelay(url))
            AddChannelButton(Core.GithubIpRelay.Name, Core.GithubIpRelay.Relay(url));

        panel.Children.Add(new TextBlock
        {
            Text = ui.T("detail.mirror-note", "镜像由第三方公益提供：本站仅做跳转，不中转、不修改文件，亦不保证始终可用。"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.6,
            FontSize = 11.5,
        });

        flyout.ShowAt(b);
    }

    
    
    
    
    private void Open(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            App.MainWindow?.ShowWebSheet(url);
            return;
        }

        try { _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url)); } catch {  }
    }

    
    private void App_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_app is null) return;

        
        
        
        if (e.PropertyName == nameof(SoftwareApp.IconImage))
            IconBox.Background = MakeIconBrush(_app);

        if (e.PropertyName is nameof(SoftwareApp.IconImage) or nameof(SoftwareApp.IconPlaceholder))
            IconFallback.Visibility = _app.IconPlaceholder;
    }

    
    private static Brush? MakeIconBrush(SoftwareApp app)
    {
        var source = app.IconImage;
        return source is null ? null : new ImageBrush
        {
            ImageSource = source,
            Stretch = Stretch.UniformToFill,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center,
        };
    }
}
