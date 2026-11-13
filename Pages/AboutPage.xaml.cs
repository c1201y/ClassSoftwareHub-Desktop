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

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 「关于」页（2026-11-13，Nick 要求加）。
///
/// <para>
/// 版式只借了 ClassIsland「关于」的**层级关系**（横幅 → 标题 → 一列可展开的分组），
/// 不是照搬：横幅在这里**铺满整个页面宽度**、下缘渐隐融进内容区，内容仍走本站母版的三列 Grid。
/// 内容是把散在两处的几样东西收拢到一处：
/// <list type="bullet">
///   <item>底部导航原来各占一颗的「反馈中心」「更新日志」→ 收进「帮助与反馈」分组；</item>
///   <item>设置页中段的「回声洞」「授权」「条款与声明」→ 整块搬来（口径与实现一字未改）；</item>
///   <item>新增「应用信息」分组：版本、版权、常用链接。</item>
/// </list>
/// </para>
///
/// <para>
/// ⚠️ 反馈中心与更新日志**仍是独立页面**（FeedbackPage / ChangelogPage），这里只是入口 ——
/// 它们的正文排版与站点对齐，塞进折叠区会变成两不像的东西。
/// </para>
///
/// <para>
/// ⚠️ 横幅与图标都是内嵌资源，运行时解到缓存再绑：unpackaged 下 <c>ms-appx:///</c> 取不到 Assets。
/// </para>
/// </summary>
public sealed partial class AboutPage : Page
{
    /// <summary>横幅底图的合成表面（页面缓存复用，留在字段里防止被提早回收）。</summary>
    private LoadedImageSurface? _bannerSurface;

    /// <summary>横幅那层合成视觉（尺寸随容器变化，见 <see cref="ResizeBanner"/>）。</summary>
    private SpriteVisual? _bannerVisual;

    public AboutPage()
    {
        InitializeComponent();

        // 授权状态每次进页面都重读：可能在别处（授权提示框）刚激活完
        Loaded += (_, _) => Load();
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

        // 回声洞起手摊开是 SettingsExpander 模板的默认行为（内层 Expander 会把 true 反推回外层），
        // 现在压一次，模板挂上那一刻与布局落定之后再各压一次 —— 少了后面两拍会看到"展开再收起"的闪动。
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

    /// <summary>
    /// 横幅：铺满整页宽度，下缘渐隐进内容区。
    ///
    /// <para>
    /// ⚠️ WinUI 3 **没有** WPF 的 <c>UIElement.OpacityMask</c>（写了会 XamlCompiler error WMC0011），
    ///    所以渐隐只能走合成层：<see cref="LoadedImageSurface"/> 当底图，
    ///    <see cref="CompositionMaskBrush"/> 配一条「白 → 全透明」的线性渐变当遮罩，
    ///    再挂到 <c>BannerHost</c> 的合成子视觉上。这样不必知道背景是 Mica 还是纯色，都能融进去。
    /// </para>
    /// <para>
    /// ⚠️ 底图用 <c>VerticalAlignmentRatio = 0</c>（顶部对齐）：窗口变宽时只裁底部那截，
    ///    标题那行永远不会被切。
    /// </para>
    /// </summary>
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
            surfaceBrush.VerticalAlignmentRatio = 0f;      // 顶部对齐 → 只裁底部

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

    /// <summary>「应用信息」分组左边那颗应用图标（跟设置页顶部同一份内嵌 PNG）。</summary>
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

    // ══════════════════ 入口：反馈 / 日志 / 诊断 ══════════════════

    private void OpenFeedback_Click(object sender, RoutedEventArgs e)
        => App.MainWindow?.Shell.NavigateTo("feedback");

    private void OpenChangelog_Click(object sender, RoutedEventArgs e)
        => App.MainWindow?.Shell.NavigateTo("changelog");

    /// <summary>诊断信息 = 设置里的「日志与诊断」那一段（本机日志、缓存目录、运行记录都在那儿）。</summary>
    private void OpenLogs_Click(object sender, RoutedEventArgs e)
        => App.MainWindow?.Shell.NavigateToSettings("logs");

    // ══════════════════ 常用链接 ══════════════════

    /// <summary>
    /// 链接地址统一从 <see cref="QuickLinks"/> 取 —— 与首页、设置页那两行是同一份数据，
    /// ⛔ 别在这里再写一遍网址（改一处就要改三处的坑踩过）。
    /// </summary>
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

    /// <summary>「开源许可」：仓库根目录那份 LICENSE 原文（GPL-3.0）。</summary>
    private void OpenLicenseText_Click(object sender, RoutedEventArgs e)
        => OpenUrl($"https://github.com/{ShellConfig.UpdateRepoOwner}/{ShellConfig.UpdateRepoName}/blob/main/LICENSE.txt");

    /// <summary>「隐私政策」：直接开条款阅读窗的隐私那一档（跟下面「条款与声明」同一个窗口）。</summary>
    private void OpenPrivacy_Click(object sender, RoutedEventArgs e) => Views.LegalWindow.Show("privacy");

    // ══════════════════ 授权（2026-11-13 从设置页搬来，逻辑未改）══════════════════

    /// <summary>
    /// 刷新「授权状态」这一张卡。
    /// <para>
    /// 三种情况，文案只写用户需要知道的（2026-11-06 Nick 定）：
    /// <list type="bullet">
    ///   <item>官方版本 —— 直接说能用了，**不显示「输入激活码」按钮**；</item>
    ///   <item>激活码激活 —— 只显示激活时间，不显示剩余天数与到期日；</item>
    ///   <item>没激活 —— 提示去输码。</item>
    /// </list>
    /// ⛔ 不写令牌 / 密钥 / 签名 / 服务端判定这类词。
    /// </para>
    /// </summary>
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
        // 对话框本体在 Views/LicenseDialog：请求被闸门挡下来时弹的是同一个，两处必须一致。
        await Views.LicenseDialog.ShowAsync(XamlRoot);
        RefreshLicense();
    }

    // ══════════════════ 回声洞（2026-11-13 从设置页整块搬来）══════════════════

    /// <summary>回声洞那张卡片整卡可点＝换一条（悬停底色铺满整行，正文自己不带框）。</summary>
    private void EchoCave_Click(object sender, RoutedEventArgs e) => EchoCave.ShowNext();

    /// <summary>
    /// 把回声洞折叠区收起来（一进页面必须是折叠的样子，2026-10-03 Nick 指定）。
    /// ⚠️ 外层 <c>SettingsExpander.IsExpanded</c> 与模板里那个内层 <see cref="Expander"/> 都要压 ——
    ///    内层才是真身（模板里绑的是 TwoWay，它起手展开会把 true 反推回外层），只压外层会被顶回来。
    /// ⛔ 别删。
    /// </summary>
    private void CollapseEchoCave()
    {
        // 光设 false 在这个控件上不够稳：MUX 的 Expander 只在 IsExpanded **发生变化**时才驱动
        // VisualState。所以先拨到 true 再拨回 false，强制它把"折叠"真的跑一遍。
        if (!EchoCaveExpander.IsExpanded) EchoCaveExpander.IsExpanded = true;
        EchoCaveExpander.IsExpanded = false;

        var inner = FindDescendant<Expander>(EchoCaveExpander);
        if (inner is not null)
        {
            if (!inner.IsExpanded) inner.IsExpanded = true;
            inner.IsExpanded = false;
        }
    }

    /// <summary>深度优先找一个后裔元素（模板里的内层控件用；找不到返回 null）。</summary>
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

    /// <summary>
    /// 「投稿」弹层里的提交：回执**就留在弹层里**（2026-10-03 Nick：卡面那行状态字删掉了）——
    /// 成功时给一句回执、停一下自己收起，失败时留着让用户看，并给一条去 GitHub 网页投稿的退路。
    /// </summary>
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

            // 回执看一眼够了就自己收（成功才收；失败留着，还能点兜底链接）。
            // 用文本比对作令牌：中途又提交过一次的话，这句旧回执不该再把弹层收掉。
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

    // ══════════════════ 条款与声明（2026-11-13 从设置页搬来）══════════════════
    //
    // 真源：站点仓库根目录 `法律文本.ts` ⇒ 站点 `public/legal.json`
    //      ⇒ Services/LegalTextSource（在线 → 缓存 → 内置兜底）。
    // ⇒ 卡片文字**不许在 XAML 写死**，一律按这份数据刷。

    /// <summary>按当前生效的条款数据刷三张卡片的标题与说明（每份文档的版本 / 生效日期也在里面）。</summary>
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
                card.Tag = doc.Key;                       // 以数据为准（XAML 里也写了，防手误不一致）
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("legal", "条款卡片刷新失败：" + ex.Message);
        }
    }

    /// <summary>三张卡片共用一个处理：按 Tag 上的 key 打开阅读窗对应那一档。</summary>
    private void OpenLegal_Click(object sender, RoutedEventArgs e)
    {
        var key = (sender as FrameworkElement)?.Tag as string;
        Views.LegalWindow.Show(key);
    }
}
