using System;
using System.Linq;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 软件详情页（原生）。版式照站点 DownloadDetailPage.vue：
/// 头部（图标 / 名称 / 标语 / 分类徽标）→ 应用介绍 → 详细信息（版本·体积·系统·官网·GitHub）
/// → 小提示 → 下载（应用商店卡 + 下载项：平台·备注·体积·校验值 + 下载 + GitHub 加速）。
/// 所有链接都交给系统（浏览器 / 下载工具）。
/// </summary>
public sealed partial class DetailPage : Page
{
    private string _storeUrl = "";
    private Button? _lastCopyButton;
    private DispatcherQueueTimer? _copyTimer;
    private SoftwareApp? _app;

    public DetailPage()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            // 离开页面把「已复制」提示定时器停掉
            _copyTimer?.Stop();
            DetachApp();
        };
    }

    /// <summary>
    /// 退订图标状态通知。
    ///
    /// ⚠️ 必须在**离开页面时**退订。`_app` 来自 App.Content.Apps —— 那是进程级静态单例，
    /// 委托是"单例持有页面"，页面不持有单例；而 ContentFrame.CacheSize = 0、页面根本不缓存。
    /// 所以只靠 OnNavigatedTo 里"退订上一个"救不了：**每进一次详情页就永久泄漏一个 Page**，
    /// 翻得越多漏得越多（教学机内存小，必须堵）。
    ///
    /// OnNavigatedFrom 与 Unloaded 都挂一道：前者是导航时必定触发，后者兜住非导航的移除，
    /// 重复调用无害（`_app` 已置空就直接返回）。
    /// </summary>
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

        // 换软件时先把上一个的图标通知退订，避免占位字形被旧软件的状态带歪
        if (_app is not null) _app.PropertyChanged -= App_PropertyChanged;

        var ui = App.Content.Ui;
        IntroTitle.Text = ui.T("detail.intro", "应用介绍");
        InfoTitle.Text = ui.T("detail.info", "详细信息");
        DownloadsTitle.Text = ui.T("detail.downloads", "下载");
        StoreTitle.Text = ui.T("detail.store-title", "使用 Microsoft Store 下载");
        StoreDesc.Text = ui.T("detail.store-desc", "由应用商店托管，安装后自动更新，不用手动跟版本。");
        StoreButton.Content = ui.T("detail.store-button", "下载");
        StoreOnlyHint.Text = ui.T("detail.store-only", "该软件通过 Microsoft Store 分发，点上方按钮打开商店页面即可获取");

        var app = App.Content.FindById(e.Parameter as string ?? "");
        if (app is null)
        {
            AppName.Text = ui.T("detail.not-found", "未找到该软件。");
            AppTagline.Visibility = Visibility.Collapsed;
            CategoryBadge.Visibility = Visibility.Collapsed;
            IntroTitle.Visibility = Visibility.Collapsed;
            AppDescription.Visibility = Visibility.Collapsed;
            InfoTitle.Visibility = Visibility.Collapsed;
            DownloadsTitle.Visibility = Visibility.Collapsed;
            return;
        }

        // ── 头部 ──
        AppName.Text = app.Name;
        AppTagline.Text = app.Tagline;
        AppTagline.Visibility = app.Tagline.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var category = app.CategoryDisplay.Length > 0 ? app.CategoryDisplay : App.Content.CategoryName(app.Category);
        CategoryText.Text = category;
        CategoryBadge.Visibility = category.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 占位字形先兜底（没图/加载中/失败都显示），图片加载成功后由 IconPlaceholder 通知收掉
        _app = app;
        app.PropertyChanged += App_PropertyChanged;
        IconBox.Background = app.IconImage is null
            ? null
            : new ImageBrush
            {
                ImageSource = app.IconImage,
                Stretch = Stretch.UniformToFill,   // 填满 72×72，多的部分裁掉
                AlignmentX = AlignmentX.Center,    // 关键：从正中间裁，不是左上角
                AlignmentY = AlignmentY.Center,
            };
        IconFallback.Visibility = app.IconPlaceholder;

        // ── 应用介绍 ──
        AppDescription.Text = app.Description;
        AppDescription.Visibility = app.Description.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        IntroTitle.Visibility = AppDescription.Visibility;

        // ── 小提示 ──
        if (app.Notice.Length > 0)
        {
            NoticeBar.Title = ui.T("detail.notice-title", "小提示");
            NoticeBar.Message = app.Notice;
            NoticeBar.IsOpen = true;
        }

        // ── 详细信息 ──
        InfoList.Children.Clear();
        AddInfoRow(ui.T("detail.version", "软件版本"), app.Version);
        AddInfoRow(ui.T("detail.size", "软件体积"), app.Size);
        AddInfoRow(ui.T("detail.system", "系统限制"), app.System);
        AddLinkRow(ui.T("detail.website", "官方网站"), app.Website);
        AddLinkRow(ui.T("detail.github", "GitHub"), app.Github);

        // ── 下载 ──
        _storeUrl = app.Store.Length > 0
            ? app.Store
            : app.Downloads.FirstOrDefault(d => GithubMirror.IsStoreUrl(d.Url))?.Url ?? "";
        var others = app.Downloads.Where(d => !GithubMirror.IsStoreUrl(d.Url)).ToList();

        StoreCard.Visibility = _storeUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        StoreOnlyHint.Visibility = _storeUrl.Length > 0 && others.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DownloadList.ItemsSource = others;

        // 微软商店本体是个特例：这台电脑要是没装商店，商店链接点开是没反应的 —— 提示一下怎么装回来
        var isStoreItself = Services.StoreRepair.IsStoreItself(app.Id, _storeUrl);
        if (isStoreItself && _storeUrl.Length > 0)
        {
            StoreOnlyHint.Text = "本机未安装 Microsoft Store 时，「打开」将无响应，此时可选择「一键恢复商店」。";
            StoreOnlyHint.Visibility = Visibility.Visible;
        }

        // ── 数据问题 ──
        var issues = App.Content.Issues
            .Where(i => i.File.Contains(app.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (issues.Count > 0)
        {
            IssueBar.Title = "这个软件的数据有提示";
            IssueBar.Message = string.Join("\n", issues.Select(i => i.File + "：" + i.Message));
            IssueBar.IsOpen = true;
        }
    }

    /// <summary>详细信息里的一行（左侧标签固定宽度，右侧值可选中复制）。</summary>
    private void AddInfoRow(string label, string value)
    {
        var row = NewInfoRow(label);
        var text = new TextBlock
        {
            Text = value.Length > 0 ? value : App.Content.Ui.T("detail.pending", "待补充"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = true,
            Opacity = value.Length > 0 ? 1.0 : 0.6,
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        InfoList.Children.Add(row);
    }

    private void AddLinkRow(string label, string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var row = NewInfoRow(label);
        var link = new HyperlinkButton { Content = url, Padding = new Thickness(0), Tag = url };
        link.Click += (s, _) => { if (((HyperlinkButton)s).Tag is string u) Open(u); };
        Grid.SetColumn(link, 1);
        row.Children.Add(link);
        InfoList.Children.Add(row);
    }

    private static Grid NewInfoRow(string label)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = label, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private async void Store_Click(object sender, RoutedEventArgs e)
    {
        if (_storeUrl.Length == 0) return;

        // 微软商店本体 → 特例（能顺带把商店装回来）
        if (Services.StoreRepair.IsStoreItself(_app?.Id, _storeUrl))
        {
            await ShowStoreSelfDialogAsync();
            return;
        }

        // 其余软件：转成商店协议 → 直接拉起「微软商店」应用（别再开浏览器了）
        App.MainWindow?.OpenStore(_storeUrl);
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string url && url.Length > 0)
            OpenDownload(url, b);
    }

    /// <summary>
    /// 下载按钮分流：
    ///   · 微软商店本体 → 特例（商店没装的话商店链接是死的，得能把商店装回来）
    ///   · 链接本身就是文件（.exe/.zip/…）→ 原生下载
    ///   · 链接其实是官网页面（很多软件更新频繁，站点就丢个官网地址）→ 开在浮层里，让人自己点
    /// </summary>
    private async void OpenDownload(string url, Button? source = null)
    {
        if (Services.StoreRepair.IsStoreItself(_app?.Id, url))
        {
            await ShowStoreSelfDialogAsync();
            return;
        }

        if (Services.DownloadService.IsDirectFileUrl(url))
            App.MainWindow?.DownloadFile(url, source is null ? null : DownloadNameFor(source));
        else
            App.MainWindow?.ShowWebSheet(url, "官网下载");
    }

    /// <summary>
    /// 微软商店本体专用：一是打开商店；二是「没装 / 被精简掉了」时用 wsreset -i 装回来。
    /// </summary>
    private async Task ShowStoreSelfDialogAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Microsoft Store（微软商店）",
            Content = new TextBlock
            {
                Text = "若本机未安装 Microsoft Store，商店链接将无法打开 —— 安装过程依赖商店自身。\n\n" +
                       "· 打开微软商店：跳转到「Microsoft Store」应用\n" +
                       "· 未安装或已损坏：调用系统自带方式重新安装（约 1～2 分钟，期间可能出现命令行窗口，请勿关闭）",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "打开微软商店",
            SecondaryButtonText = "未安装？一键恢复",
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
                        XamlRoot = XamlRoot,
                        Title = "已开始安装微软商店",
                        Content = new TextBlock
                        {
                            Text = "如出现命令行窗口，请等待其自动关闭（约 1～2 分钟）。\n" +
                                   "安装完成后，Microsoft Store 会出现在开始菜单中，返回此处再次点击「打开微软商店」即可。",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        CloseButtonText = "知道了",
                    }.ShowAsync();
                }
                else
                {
                    await new ContentDialog
                    {
                        XamlRoot = XamlRoot,
                        Title = "无法启动安装",
                        Content = new TextBlock
                        {
                            Text = "可在开始菜单中搜索 Microsoft Store，或用浏览器访问 " +
                                   "https://apps.microsoft.com/detail/9wzdncrfjbmp 手动安装。",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        CloseButtonText = "知道了",
                    }.ShowAsync();
                }
                break;
        }
    }

    /// <summary>下载文件名：优先「软件名 + 版本」，实在没有就让 DownloadService 从 URL 猜。</summary>
    private string? DownloadNameFor(Button b)
    {
        if (_app is null) return null;

        var item = b.DataContext as DownloadItem;
        var platform = item?.Platform ?? "";

        // 用软件名当文件名，扩展名从 URL 里取 —— 这样下载下来是「微信 4.0.exe」而不是一串 hash
        var name = _app.Name;
        if (!string.IsNullOrWhiteSpace(platform)) name += " " + platform;
        if (!string.IsNullOrWhiteSpace(_app.Version)) name += " " + _app.Version;
        return name.Trim();
    }

    /// <summary>校验值：点一下把完整哈希复制走，2 秒后文案还原。</summary>
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
            return; // 剪贴板拿不到就什么都不做，完整值在 tooltip 里还能手选
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

    private void CopyTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_lastCopyButton?.DataContext is DownloadItem item)
            _lastCopyButton.Content = item.HashChipText;
        _lastCopyButton = null;
    }

    /// <summary>GitHub 直链才有的「加速下载」：列出所有镜像通道，点哪条走哪条。</summary>
    private void Mirror_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string url || url.Length == 0) return;

        var ui = App.Content.Ui;
        var panel = new StackPanel { Spacing = 8, MaxWidth = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = ui.T("detail.mirror-desc", "下面的镜像站会把上面的链接原样转发一份，国内下载通常快很多。"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
            FontSize = 12.5,
        });

        var flyout = new Flyout { Content = panel };
        foreach (var channel in GithubMirror.Channels)
        {
            var target = GithubMirror.MirrorUrl(url, channel);
            var button = new Button
            {
                Content = channel.Name,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Tag = target,
            };
            button.Click += (s, _) =>
            {
                // 加速下载也是下载：直接文件就原生下，指到官网就开浮层
                if (((Button)s).Tag is string u) OpenDownload(u, b);
                flyout.Hide();
            };
            panel.Children.Add(button);
        }

        panel.Children.Add(new TextBlock
        {
            Text = ui.T("detail.mirror-note", "镜像由第三方公益提供：本站只做跳转，不中转、不修改文件，也不保证它们一直可用。"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.6,
            FontSize = 11.5,
        });

        flyout.ShowAt(b);
    }

    /// <summary>
    /// 打开链接：http(s) 一律走应用内网页浮层（不再甩到浏览器），
    /// 其它协议（ms-windows-store: 等）才交给系统。
    /// </summary>
    private void Open(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            App.MainWindow?.ShowWebSheet(url);
            return;
        }

        try { _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url)); } catch { /* 打不开就算了 */ }
    }

    // 图标加载状态变了（成功 → 收掉占位字形；失败 → 继续顶着）
    private void App_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SoftwareApp.IconPlaceholder) && _app is not null)
            IconFallback.Visibility = _app.IconPlaceholder;
    }
}
