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
using Windows.UI;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 软件详情页（原生）。版式照站点 DownloadDetailPage.vue：
/// 头部（图标 / 名称 / 标语 / 分类徽标）→ 应用介绍 → 详细信息（版本·体积·系统·官网·GitHub）
/// → 小提示 → 下载（应用商店卡 + 下载项：平台·备注·体积·类型说明·校验值 + 主按钮 + GitHub 加速）。
/// 所有链接都交给系统或应用内浮层。
/// 主按钮的文案与行为随链接类型变（Data/DownloadLinkKind.cs，与站点 downloadLink.ts 同源）：
/// 直链才写「下载」，网盘 / 官网下载页分别写「前往网盘」「前往官网」。
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
        StoreTitle.Text = ui.T("detail.store-title", "在 Microsoft Store 中获取");
        StoreDesc.Text = ui.T("detail.store-desc", "由微软商店托管，安装后自动更新，无需手动下载安装包。");
        StoreButton.Content = ui.T("detail.store-button", "获取");
        StoreOnlyHint.Text = ui.T("detail.store-only", "该软件通过 Microsoft Store 分发，单击上方按钮打开商店页面即可获取");

        // 商店图标（2026-10-19）：内嵌资源解到缓存再绑，避免 ms-appx 在 unpackaged 下取不到
        if (StoreLogo.Source is null) StoreLogo.Source = LoadStoreLogo();

        // 换软件时先把上一个的图标通知退订，避免占位字形被旧软件的状态带歪
        var app = App.Content.FindById(e.Parameter as string ?? "");
        if (app is null)
        {
            AppName.Text = ui.T("detail.not-found", "未找到该软件。");
            AppTagline.Visibility = Visibility.Collapsed;
            CategoryBadge.Visibility = Visibility.Collapsed;
            HeroMeta.Visibility = Visibility.Collapsed;
            IntroTitle.Visibility = Visibility.Collapsed;
            AppDescription.Visibility = Visibility.Collapsed;
            InfoTitle.Visibility = Visibility.Collapsed;
            DownloadsTitle.Visibility = Visibility.Collapsed;
            Rail.Visibility = Visibility.Collapsed;
            RailCol.Width = new GridLength(0);
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
            NoticeBar.Visibility = Visibility.Visible;
        }
        else
        {
            NoticeBar.IsOpen = false;
            NoticeBar.Visibility = Visibility.Collapsed;
        }

        // ── 详细信息 ──
        InfoList.Children.Clear();
        AddInfoRow(ui.T("detail.version", "软件版本"), app.Version);
        AddInfoRow(ui.T("detail.size", "软件体积"), app.Size);
        AddInfoRow(ui.T("detail.system", "系统限制"), app.System);
        AddLinkRow(ui.T("detail.website", "官方网站"), app.Website);
        AddLinkRow(ui.T("detail.github", "GitHub"), app.Github);

        // ── 下载 ──
        // 商店项不进下载列表，交给上方那张商店卡（判据与站点一致：kindOf() === 'store'）
        _storeUrl = app.Store.Length > 0
            ? app.Store
            : app.Downloads.FirstOrDefault(d => d.Kind == DownloadKind.Store)?.Url ?? "";
        var others = app.Downloads.Where(d => d.Kind != DownloadKind.Store).ToList();

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
            IssueBar.Title = "该软件的数据存在提示";
            IssueBar.Message = string.Join("\n", issues.Select(i => i.File + "：" + i.Message));
            IssueBar.IsOpen = true;
        }

        // ── 顶栏副标题、推荐应用、主题色 ──
        var meta = string.Join("　·　", new[] { app.VersionText, app.Size, app.System }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        HeroMeta.Text = meta;
        HeroMeta.Visibility = meta.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        BuildRecommendations(app);
        _ = ApplyHeroColorAsync(app);
    }

    /// <summary>
    /// 右栏「推荐应用」：先取同分类的，不够就用其他软件补齐（Nick：搜不到就随便推荐）。
    /// ⛔ 标题不能叫「相似应用」—— 补齐的那部分只是别的软件，叫相似名不副实。
    /// ⛔ 用 Id 的稳定哈希排序而不是 Random —— 同一台机器每次进来顺序一致，不会看着"跳来跳去"。
    /// </summary>
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

    /// <summary>
    /// 顶部色带：一律等**图标主色**，颜色没就绪之前色带是透明的（等于页面底色）。
    ///
    /// ⛔⛔ 上一版先用主题强调色顶上、几百毫秒后再换成图标色，被 Nick 点名"非常割裂"。
    /// 现在只有两种情况会立刻上色：① 列表页已经预热过（缓存命中，进页面就是最终色）；
    /// ② 取不到色（黑白图标 / SVG / 网络失败）⇒ 才退回强调色，这是兜底、不是过渡。
    /// 其余情况取到后再淡入，读起来像"色带浮现"，不像"颜色换了"。
    /// </summary>
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

        // 兜底：没有主色可用，直接上强调色（不做淡入，免得看着像还在加载）
        var fallback = (Services.ThemeBrush.Get(this, "AccentFillColorDefaultBrush") as SolidColorBrush)?.Color
                       ?? Color.FromArgb(255, 0, 95, 184);
        PaintHero(app, fallback, fade: false);
    }

    private void PaintHero(SoftwareApp app, Color color, bool fade)
    {
        // 期间可能已经导航到别的软件 —— 比 Id，别把 A 的颜色刷到 B 页面上
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

    /// <summary>详细信息里的一行（左侧标签固定宽度，右侧值可选中复制）。</summary>
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

    private void AddLinkRow(string label, string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var link = new HyperlinkButton { Content = url, Padding = new Thickness(0), Tag = url };
        link.Click += (s, _) => { if (((HyperlinkButton)s).Tag is string u) Open(u); };
        InfoList.Children.Add(InfoRow(label, link));
    }

    /// <summary>
    /// 一行详细信息。
    /// ⛔ 不再放进灰色卡片（Nick：灰底很丑）—— 现在靠**行间细分割线 + 上下留白**分组，
    /// 和商店里详情区的做法一致。分割线只画在行与行之间，末行不拖一条尾巴。
    /// </summary>
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

    /// <summary>
    /// 微软商店图标（`Assets/StoreLogo.png`，内嵌资源 ⇒ 跟着 exe 走、离线可用）。
    /// ⚠️ unpackaged 应用里 `ms-appx:///` 取不到 Assets，这里走 <see cref="Services.EmbeddedAssets"/> 解到缓存再用文件路径。
    /// 取不到就返回 null，卡片照样能用（只是少一个图标），⛔ 别让整页崩掉。
    /// </summary>
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
        if (sender is Button b && b.DataContext is DownloadItem item)
            OpenDownload(item, b);
    }

    /// <summary>
    /// 下载按钮分流。类型判定见 <see cref="DownloadLinkKind"/>（与站点 src/gallery/downloadLink.ts 同源）：
    ///   · 微软商店本体 → 特例（商店没装的话商店链接是死的，得能把商店装回来）
    ///   · file     文件直链 → 原生下载
    ///   · netdisk  网盘分享页 → 只能跳转（要密码 / 登录 / 客户端）
    ///   · page     官网下载页 → 只能跳转（链接由 JS 生成或带签名，取不到）
    /// </summary>
    private async void OpenDownload(DownloadItem item, Button? source = null)
    {
        var url = item.Url;
        if (url.Length == 0) return;

        if (Services.StoreRepair.IsStoreItself(_app?.Id, url))
        {
            await ShowStoreSelfDialogAsync();
            return;
        }

        if (item.Kind == DownloadKind.File)
            App.MainWindow?.DownloadFile(url, source is null ? null : DownloadNameFor(source));
        else
            App.MainWindow?.ShowWebSheet(url, item.Kind == DownloadKind.Netdisk ? "网盘分享" : "官网下载");
    }

    /// <summary>
    /// 微软商店本体专用：一是打开商店；二是「没装 / 被精简掉了」时用 wsreset -i 装回来。
    /// </summary>
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

    /// <summary>
    /// 「校验」：带着这个校验值跳到「编码 / 哈希工具」的**文件模式**，用户把刚下好的安装包
    /// 拖进去就自动比对 —— 省掉"自己打开工具 → 找到哈希 → 复制 → 粘贴 → 再选文件"那一串。
    /// 走 <c>viaList: false</c>：不铺工具列表，看完返回一次就回到这张详情页。
    /// </summary>
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

    /// <summary>GitHub 直链才有的「加速下载」：列出所有镜像通道，点哪条走哪条。</summary>
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
                // 加速下载就是把这条直链套上镜像前缀，入口本身只对文件直链出现
                // ⇒ 显式声明 file：有些包的地址不带后缀（/package/win-autoupdate、
                //   /api/v1/release/dl/1），光看 URL 会被判成「官网页面」而开成浮层。
                if (((Button)s).Tag is string u)
                    OpenDownload(new DownloadItem { Url = u, DeclaredKind = "file" }, b);
                flyout.Hide();
            };
            panel.Children.Add(button);
        }

        panel.Children.Add(new TextBlock
        {
            Text = ui.T("detail.mirror-note", "镜像由第三方公益提供：本站仅做跳转，不中转、不修改文件，亦不保证始终可用。"),
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
