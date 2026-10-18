using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Services.Updating;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>原生设置页：关于（最上，突出版本号）/ 外观 / 窗口行为 / 内容。改动即时生效。</summary>
public sealed partial class SettingsPage : Page
{
    private bool _loading = true;
    private bool _contentBusy;
    /// <summary>
    /// 打开设置页后把滚动位置拉回顶部的定时器。
    /// ⚠️ 必须存字段 —— DispatcherQueueTimer 被 GC 收走就不会触发（本项目踩过）。
    /// </summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _settleTimer;

    /// <summary>
    /// 「本地缓存」「本地日志」两个列表的分页：每页最多这么多条。
    /// 2026-10-11 Nick：展开后一屏拉不到头，改成翻页。数量不超一页时整条分页栏收起来。
    /// </summary>
    private const int ListPageSize = 10;

    private int _cachePage = 1;
    private int _logPage = 1;
    private int _installerPage = 1;
    private int _localHistoryPage = 1;
    private int _historyPage = 1;

    /// <summary>
    /// 「从仓库装回旧版本」加载过的那一份（最多 20 条）。
    /// ⚠️ 翻页**只翻这份缓存**，⛔不重新打网络 —— GitHub 那一步又慢又容易被限流，
    ///    点一下「下一页」就重新拉一次，体验会很糟。要刷新得再点那颗「从仓库加载」按钮。
    /// </summary>
    private IReadOnlyList<UpdateRelease>? _historyReleases;

    /// <summary>「从仓库装回旧版本」列表末尾那句说明（稳定通道只列正式版）；没有就不显示。</summary>
    private string? _historyNote;

    public SettingsPage()
    {
        InitializeComponent();

        // 主题一切（浅色 ⇄ 深色）就把 SVG 快捷图标重染成对应颜色
        ActualThemeChanged += (_, _) => ApplyQuickLinkIcons();
    }

    /// <summary>给带 SVG 的快捷入口按当前主题色染图标；主题一切就重染（浅色/深色自适应）。</summary>
    private void ApplyQuickLinkIcons()
    {
        if (SettingsQuickGrid.ItemsSource is not List<Core.QuickLink> links) return;
        var color = ((SolidColorBrush)ThemeBrush.Get(this, "TextFillColorPrimaryBrush")).Color;
        foreach (var link in links)
            if (!string.IsNullOrEmpty(link.IconSvg))
                link.IconSource = QuickLinkIconLoader.GetIcon(link.IconSvg, color);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _loading = true;

        // 后台同步完成时这一屏要自己更新（安装包不自带清单，首次进来多半还空着）。
        // ⚠️ 本页 NavigationCacheMode=Enabled（实例长驻），先 -= 再 += 保证只挂一次。
        App.Content.Changed -= OnContentChanged;
        App.Content.Changed += OnContentChanged;

        var s = App.Settings.Current;
        BackdropCombo.SelectedIndex = s.Backdrop switch
        {
            "mica" => 1,
            "solid" => 2,
            _ => 0
        };
        TopMostSwitch.IsOn = s.AlwaysOnTop;
        AutoStartSwitch.IsOn = s.AutoStart;
        MinimizeSwitch.IsOn = s.MinimizeOnStart;
        TraySwitch.IsOn = s.CloseToTray;
        ThemeCombo.SelectedIndex = s.Theme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0
        };

        // GitHub 下载体验优化（下载路径）：认不出的值一律当「自动」
        GithubRouteCombo.SelectedIndex = Services.GithubRoute.Current switch
        {
            Services.GithubRoutes.SelfHosted => 1,
            Services.GithubRoutes.Official => 2,
            _ => 0,
        };
        UpdateGithubRouteHint();

        // 外部组件（分体）外观
        SplitThemeSwitch.IsOn = s.SplitTheme;
        ExtThemeCombo.SelectedIndex = s.ExternalTheme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0
        };
        UpdateExtThemeAvailability();
        UpdateMinimizeAvailability();

        ChannelCombo.SelectedIndex = UpdateChannels.Parse(s.UpdateChannel) == UpdateChannel.Insider ? 1 : 0;
        AutoCheckSwitch.IsOn = s.AutoCheckUpdate;
        // 安装包保留数量：下拉项 1~10 与索引一一对应；存档里的怪值夹回 1~10 再定位
        KeepCombo.SelectedIndex = Math.Clamp(s.InstallerKeepCount, 1, 10) - 1;
        // 缓存保留数量：同款下拉（1~10 与索引一一对应）
        CacheKeepCombo.SelectedIndex = Math.Clamp(s.CacheKeepCount, 1, 10) - 1;
        // 日志保留天数：3/7/15/30/永不（Tag 存天数，0 = 永不删除）；存档里出现别的值一律落回「7 天」
        LogKeepCombo.SelectedIndex = LogKeepIndex(s.LogKeepDays);
        UpdateCurrent.Text = $"当前版本：{ShellConfig.VersionPrefix}{ShellConfig.ShellVersion}· 更新源：{UpdateService.CreateDefault().Source.DisplayName}";

        RefreshContentInfo();
            BuildAboutHeader();
            SettingsQuickGrid.ItemsSource = Core.QuickLinks.Build(App.Content.Ui);
            ApplyQuickLinkIcons();
            _loading = false;

        // 「版本记录」固定折叠：里面是十几条运行记录，摊开会把设置页拉得极长。
        // ⚠️ 只在 XAML 里写 IsExpanded="False" 不够 —— 实测加载过程中仍会被撑开，这里再压一次。
        HistoryExpander.IsExpanded = false;
        InstallerExpander.IsExpanded = false;
        CacheExpander.IsExpanded = false;
        LogExpander.IsExpanded = false;
        CollapseEchoCave();

        // ⚠️ SettingsExpander 起手是摊开的（模板铺好那一刻内层 Expander 默认 IsExpanded=true，
        //    经 TwoWay 反推回外层）——所以除了下面那一拍，模板一挂上就再压一次，压掉"闪一下"。
        EchoCaveExpander.Loaded += (_, _) => CollapseEchoCave();

        // 打开设置页固定从顶部开始：卡片高度会被设置值二次刷新，ScrollViewer 的锚点跟着漂，
        // 实测会直接停到「常用工具」那一段（2026-09-28）。
        _settleTimer = DispatcherQueue.CreateTimer();
        _settleTimer.Interval = TimeSpan.FromMilliseconds(200);
        _settleTimer.IsRepeating = false;
        _settleTimer.Tick += (_, _) =>
        {
            _settleTimer.Stop();
            RootScroll.ChangeView(null, 0, null, true);

            // 折叠区在导航后 200ms（早已过布局）再压一次，防"首帧摊开"。
            HistoryExpander.IsExpanded = false;
            InstallerExpander.IsExpanded = false;
            LogExpander.IsExpanded = false;
            CollapseEchoCave();
        };
        _settleTimer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        App.Content.Changed -= OnContentChanged;
        base.OnNavigatedFrom(e);
    }

    /// <summary>内容变了 → 刷新这一组摘要（手动同步进行中就别抢它刚写上的状态行）。</summary>
    private void OnContentChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnContentChanged);
            return;
        }
        if (_contentBusy) return;
        RefreshContentInfo();
    }

    private void RefreshContentInfo()
    {
        var c = App.Content;
        ContentSummary.Text = c.HasData
            ? $"{c.Apps.Count} 个软件 · {c.Categories.Count} 个分类" +
              (c.ContentVersion.Length > 0 ? $" · 内容版本 {c.ContentVersion}" : "")
            : (c.IsSyncing ? "正在获取软件清单…" : "还没有获取到内容 —— 需要联网。");

        ContentSource.Text = $"内容来源：{c.SourceLabel}\n{c.Source}";

        ContentIssues.Text = c.Issues.Count > 0
            ? $"有 {c.Issues.Count} 条解析提示：" + string.Join("；", c.Issues.Take(3).Select(i => i.File + " — " + i.Message))
            : "";
        ContentIssues.Visibility = c.Issues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        AboutApp.Text = ShellConfig.AppName;
        AboutVersion.Text = ShellConfig.VersionPrefix + ShellConfig.ShellVersion;

        // 站点版本**以编译进程序的常量为准**，不读任何联网文本：
        // 以前读内容包的 app.version，而那份 text/ 不联网更新、装机即冻结，一直显示装机那天那版（实测停在 v2.3.2）。
        // 详见 ShellConfig.SiteVersionDisplay 的注释。
        AboutSiteVersion.Text = $"站点版本：{ShellConfig.SiteVersionDisplay}";

        // 条款与声明：三张卡片的标题 / 说明也走同一份数据（站点改了条款，这里跟着变）
        RefreshLegalCards();
    }

    private void UpdateMinimizeAvailability()
    {
        var on = AutoStartSwitch.IsOn;
        MinimizeSwitch.IsEnabled = on;
        MinimizeHint.Opacity = on ? 0.55 : 0.4;
        MinimizeHint.Text = on
            ? "开机启动时直接收进托盘（任务栏上不留按钮），想用的时候点托盘图标。"
            : "只有打开「开机自动启动」时，这个选项才有用。";
    }

    private void ThemeChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ThemeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string theme) return;
        App.MainWindow?.SetTheme(theme);       // 里面会 Save + 重算背景 + 通知外部组件
        App.MainWindow?.Shell.UpdateThemeButton();
    }

    // ── 外部组件（分体）外观 ─────────────────────────────────

    private void SplitTheme_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Current.SplitTheme = SplitThemeSwitch.IsOn;
        App.Settings.Save();
        UpdateExtThemeAvailability();
        Services.ThemeHost.Notify();           // 外部组件（侧边栏/浮窗/截图窗）立刻换过来
    }

    private void ExtTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ExtThemeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string theme) return;
        App.Settings.Current.ExternalTheme = theme;
        App.Settings.Save();
        Services.ThemeHost.Notify();
    }

    private void UpdateExtThemeAvailability()
    {
        ExtThemeCombo.IsEnabled = SplitThemeSwitch.IsOn;
        ExtThemeHint.Text = SplitThemeSwitch.IsOn
            ? "侧边栏 / 常用工具浮窗 / 截图编辑窗都跟着这个走"
            : "现在是关的：外部组件跟主界面的颜色模式保持一致";
    }

    private void BackdropChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (BackdropCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string kind) return;
        App.Settings.Current.Backdrop = kind;
        App.Settings.Save();
        App.MainWindow?.SetBackdrop(kind);
    }

    private void TopMostSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Current.AlwaysOnTop = TopMostSwitch.IsOn;
        App.Settings.Save();
        App.MainWindow?.SetAlwaysOnTop(TopMostSwitch.IsOn);
    }

    private void AutoStartSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Current.AutoStart = AutoStartSwitch.IsOn;
        App.Settings.Save();
        UpdateMinimizeAvailability();
        App.MainWindow?.SetAutoStart(AutoStartSwitch.IsOn);
    }

    private void MinimizeSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Current.MinimizeOnStart = MinimizeSwitch.IsOn;
        App.Settings.Save();
        App.MainWindow?.SetMinimizeOnStart(MinimizeSwitch.IsOn);
    }

    private void TraySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.MainWindow?.SetCloseToTray(TraySwitch.IsOn);
    }

    private void ReloadContent_Click(object sender, RoutedEventArgs e)
    {
        App.Content.Load();
        RefreshContentInfo();
    }

    /// <summary>手动从网络同步内容（失败也不影响本机已有的那份）。</summary>
    private async void SyncContent_Click(object sender, RoutedEventArgs e)
    {
        if (_contentBusy) return;
        _contentBusy = true;

        ContentSource.Text = "正在从网络获取…";
        try
        {
            var result = await Services.ContentUpdater.SyncAsync(
                new Progress<string>(text => ContentSource.Text = text));

            if (result.Updated) App.Content.Load();

            ContentIssues.Text = result.Message;
            ContentIssues.Visibility = Visibility.Visible;
            RefreshContentInfo();
        }
        catch (Exception ex)
        {
            ContentIssues.Text = "同步失败：" + ex.Message;
            ContentIssues.Visibility = Visibility.Visible;
        }
        finally
        {
            _contentBusy = false;
        }
    }

    private void OpenContentDir_Click(object sender, RoutedEventArgs e)    {
        try
        {
            var dir = App.Content.Source;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) dir = AppPaths.DataDir;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch { /* 打不开就算了 */ }
    }

    // ══════════════════════════ 诊断 ══════════════════════════

    /// <summary>设置 → 诊断 → 日志查看（Nick 2026-10-02：正式入口，不再放实验性分组）。
    /// 走 ContentFrame 真导航 —— NavView 的后退键能回到设置页（设置页有缓存，回来状态还在）。</summary>
    /// <summary>回声洞那张卡片整卡可点＝换一条（悬停底色铺满整行，正文自己不带框）。</summary>
    private void EchoCave_Click(object sender, RoutedEventArgs e) => EchoCave.ShowNext();

    /// <summary>
    /// 把回声洞折叠区收起来（设置页一打开必须是折叠的样子，2026-10-03 Nick 指定）。
    /// ⚠️ 外层 <c>SettingsExpander.IsExpanded</c> 与模板里那个内层 <see cref="Expander"/> 都要压 ——
    ///    内层才是真身（模板里绑的是 TwoWay，它起手展开会把 true 反推回外层），只压外层会被顶回来。
    /// ⛔ 别删。实测口径：收起后外层/内层 ActualHeight 都是 70（只剩表头行），摊开 125~165（随内容长短）。
    /// </summary>
    private void CollapseEchoCave()
    {
        // 光设 false 在这个控件上不够稳：MUX 的 Expander 只在 IsExpanded **发生变化**时才驱动
        // VisualState。所以先拨到 true 再拨回 false，强制它把"折叠"真的跑一遍。
        // 两次赋值在同一个 dispatcher 拍里完成、中间不渲染，肉眼看不到闪。
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
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            var deeper = FindDescendant<T>(child);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    /// <summary>
    /// 「投稿」弹层里的提交：走「提交软件」同一套自建服务（令牌在服务端）。
    /// 回执**就留在弹层里**（2026-10-03 Nick：卡面那一行状态字删掉了，永远不再显示）——
    /// 成功时给一句回执、停一下自己收起，失败时留着让用户看。
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

        var (ok, message) = await Services.EchoCaveService.SubmitAsync(text);

        EchoSubmitButton.IsEnabled = true;
        EchoSubmitButton.Content = "提交";
        ShowEchoSubmitStatus(message);
        // 服务端还没接上（或网络被挡）时给条退路：去 GitHub 网页投。成功时不给，免得画蛇添足。
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

    private void OpenLogViewer_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(LogViewerPage));
    }

    /// <summary>Hub AI 二级设置页（2026-10-18）。和日志查看同一套走法：Frame 直跳，返回靠导航栏。</summary>
    private void OpenHubAiSettings_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(HubAiSettingsPage));
    }

    // ══════════ 条款与声明（用户协议 / 隐私政策 / 免责声明）══════════
    //
    // 三份内容与站点同源：站点仓库根目录 `法律文本.ts` ⇒ Core/LegalText.cs
    // （由 tools/sync-legal.mjs 生成；安装程序那一页 installer/Legal.rtf 同一次生成）。
    // ⇒ 卡片文字**不许在 XAML 写死**，一律按这份数据刷，站点改条款桌面端自然跟着变。

    /// <summary>按 Core/LegalText.cs 刷三张卡片的标题与说明（每份文档的版本 / 生效日期也在里面）。</summary>
    private void RefreshLegalCards()
    {
        try
        {
            Bind(LegalAgreementCard, "agreement", "使用本站（网站与桌面客户端）前请完整阅读");
            Bind(LegalPrivacyCard, "privacy", "本站怎么处理你的信息、哪些存在本机");
            Bind(LegalDisclaimerCard, "disclaimer", "软件来源、下载安装风险与责任范围");

            void Bind(CommunityToolkit.WinUI.Controls.SettingsCard card, string key, string blurb)
            {
                var doc = LegalText.Find(key);
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

    // ══════════ GitHub 下载体验优化（下载路径的全局默认；设置 → 软件内容）══════════

    private void GithubRoute_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (GithubRouteCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string route) return;

        App.Settings.Current.GithubDownloadRoute = Services.GithubRoutes.Normalize(route);
        App.Settings.Save();

        // 「自动」那次探测结果立刻作废 —— 否则刚换完设置还要等最多 10 分钟才认。
        Services.GithubRoute.InvalidateCache();
        UpdateGithubRouteHint();
    }

    /// <summary>
    /// 把当前这条路径具体怎么干说清楚（选项名太短，说不清「取不到签名会退镜像」这种差别）。
    /// </summary>
    private void UpdateGithubRouteHint()
    {
        var current = Services.GithubRoute.Current;

        GithubRouteHint.Text = current switch
        {
            Services.GithubRoutes.SelfHosted =>
                "自建加速服务：向社区自建节点换取限时签名链接后中转下载，国内网络下更稳；"
                + "签名取不到时自动改用公益镜像，均不可用再回落 GitHub 源。",
            Services.GithubRoutes.Official =>
                "GitHub 源：直接访问 github.com，不作任何改写（国内网络环境中速率可能偏低或中断）。",
            _ =>
                "自动：下载前并行探测自建节点、各条公益镜像与 GitHub 源的速率，择快者使用；"
                + "首选不可用时自动换下一条。探测结果保留 8 分钟。",
        };
    }

    // ══════════════════════════ 更新 ══════════════════════════

    private readonly UpdateService _updater = UpdateService.CreateDefault();
    private bool _updateBusy;

    private void ChannelChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ChannelCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string channel) return;
        App.Settings.Current.UpdateChannel = channel;
        App.Settings.Current.UpdateChannelSetByUser = true;
        App.Settings.Save();

        UpdateStatus.Text = channel == UpdateChannels.Insider
            ? "Insider 通道：优先收 Pre-release；正式版发出来比当前还新时，也会自动跟上。"
            : "稳定通道：只收 Latest（正式发布）的版本。";
        UpdateNotes.Visibility = Visibility.Collapsed;
    }

    private void AutoCheckSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Current.AutoCheckUpdate = AutoCheckSwitch.IsOn;
        App.Settings.Save();
    }

    /// <summary>检查更新：查到新版先弹窗问用户要不要装（不强制）。</summary>
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        CheckButton.IsEnabled = false;
        UpdateNotes.Visibility = Visibility.Collapsed;
        UpdateStatus.Text = "正在检查更新…";

        try
        {
            var channel = UpdateChannels.Parse(App.Settings.Current.UpdateChannel);
            var result = await _updater.CheckAsync(channel, ShellConfig.ShellVersion);

            App.Settings.Current.LastUpdateCheck = DateTimeOffset.Now.ToUnixTimeSeconds();
            App.Settings.Save();

            UpdateStatus.Text = result.Message;

            if (result is { HasUpdate: true, Release: { } release })
            {
                var notes = Snip(release.Notes, 400);
                UpdateNotes.Text = notes;
                UpdateNotes.Visibility = notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                UpdateStatus.Text = $"发现新版本 {release.Tag}，可自行选择是否安装。";

                // 先问；选「稍后」就什么都不做
                var choice = await UpdateFlow.AskAsync(XamlRoot, release);
                if (choice == UpdateFlow.UpdateChoice.Later) return;

                if (choice == UpdateFlow.UpdateChoice.Background)
                {
                    if (UpdateFlow.StartBackgroundDownload(_updater, release))
                        UpdateStatus.Text = $"已转入「任务进行」页下载 {release.Tag}，可在该页查看进度并安装。";
                    else
                        UpdateStatus.Text = "已有一个更新正在后台下载，可在「任务进行」页查看。";
                    return;
                }

                if (!await UpdateFlow.RunAsync(XamlRoot, _updater, release))
                    UpdateStatus.Text = "更新失败：可稍后重试，或前往发布页手动下载新版本。";
            }
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "检查失败：" + ex.Message;
        }
        finally
        {
            _updateBusy = false;
            CheckButton.IsEnabled = true;
        }
    }

    /// <summary>关于区的软件图标（内嵌资源，单文件发布下也能取到）。</summary>
    private void BuildAboutHeader()
    {
        try
        {
            var path = Services.EmbeddedAssets.ExtractToCache("AppIcon-512.png", "app-icon.png");
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                AboutIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));
        }
        catch { /* 图标取不到就不显示 */ }
    }

    private void Quick_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Core.QuickLink link) return;

        // 跟首页那一行同一个口径：带 Tag 的走应用内导航（「更新日志」），其余才丢给系统浏览器
        if (link.Tag.Length > 0)
        {
            App.MainWindow?.Shell.NavigateTo(link.Tag);
            return;
        }

        if (link.Url.Length > 0)
            App.MainWindow?.OpenExternal(link.Url);
    }

    // ── 版本记录（本机运行过的版本） ────────────────────────────

    /// <summary>
    /// 版本记录展开时才去读本机记录（迁到 SettingsExpander 后事件名由 Expanding 变成 Expanded）。
    /// ⚠️ 签名写成 (object, EventArgs) 而不是 (SettingsExpander, ...)：事件参数类型无论哪个派生类都能接，
    /// 省得把 Toolkit 的内部事件参数类型引进来。
    /// </summary>
    private void History_Expanded(object sender, EventArgs args)
    {
        _localHistoryPage = 1;          // 重新展开＝从第一页看起
        LoadLocalHistory();
    }

    private void LocalHistoryPrev_Click(object sender, RoutedEventArgs args)
    {
        _localHistoryPage--;
        LoadLocalHistory();
    }

    private void LocalHistoryNext_Click(object sender, RoutedEventArgs args)
    {
        _localHistoryPage++;
        LoadLocalHistory();
    }

    /// <summary>本机记录：这台电脑运行过哪些版本（启动时自动记的），不是仓库的 Release 列表。</summary>
    private void LoadLocalHistory()
    {
        var records = Services.VersionHistory.Load();
        if (records.Count == 0)
        {
            LocalHistoryList.Children.Clear();
            LocalHistoryPager.Visibility = Visibility.Collapsed;
            LocalHistoryList.Children.Add(new TextBlock
            {
                Text = "本机还没有版本记录（这次启动就会记下第一条）。",
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        _localHistoryPage = RenderPagedList(LocalHistoryList, records, _localHistoryPage, LocalHistoryRow,
                                            LocalHistoryPager, LocalHistoryPageText,
                                            LocalHistoryPrevButton, LocalHistoryNextButton);
    }

    /// <summary>本机记录单行：版本号 + [通道] + 「当前版本」，下面一行首次／最近运行时间。</summary>
    private static StackPanel LocalHistoryRow(Services.VersionRecord record)
    {
        var current = Core.ShellConfig.ShellVersion;
        var isCurrent = string.Equals(record.Version, current, StringComparison.OrdinalIgnoreCase);
        var channel = record.Channel.Length > 0 ? $"  [{record.Channel}]" : "";

        var row = new StackPanel { Spacing = 2, Padding = new Thickness(0, 4, 0, 4) };
        row.Children.Add(new TextBlock
        {
            Text = Services.VersionHistory.Display(record) + channel + (isCurrent ? "  ·  当前版本" : ""),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        row.Children.Add(new TextBlock
        {
            Text = $"首次运行 {record.FirstSeen:yyyy-MM-dd HH:mm} · 最近一次 {record.LastSeen:yyyy-MM-dd HH:mm}",
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
        });
        return row;
    }

    // ── 历史版本（回滚） ──────────────────────────────────────

    private void HistoryPrev_Click(object sender, RoutedEventArgs args)
    {
        _historyPage--;
        RenderHistoryPage();
    }

    private void HistoryNext_Click(object sender, RoutedEventArgs args)
    {
        _historyPage++;
        RenderHistoryPage();
    }

    /// <summary>
    /// 画「从仓库装回旧版本」的**当前页**。翻页也走这里，用的是 <see cref="_historyReleases"/> 那份缓存，
    /// ⛔不会再打一次网络（GitHub 那步慢且易限流，点一下「下一页」就重拉一次体验很糟）。
    /// </summary>
    private void RenderHistoryPage()
    {
        var releases = _historyReleases;

        HistoryList.Children.Clear();

        if (releases is null || releases.Count == 0)
        {
            HistoryPager.Visibility = Visibility.Collapsed;
            HistoryList.Children.Add(new TextBlock
            {
                Text = _updater.Source.IsConfigured ? "没查到版本（仓库里还没发过 Release）。" : "还没配置更新仓库地址。",
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        _historyPage = RenderPagedList(HistoryList, releases, _historyPage, HistoryRow,
                                       HistoryPager, HistoryPageText, HistoryPrevButton, HistoryNextButton);

        // 尾注不属于数据行：正式版用户看不到预览版，说清楚免得以为列表缺斤少两。
        // ⚠️ 必须排在 RenderPagedList 之后 —— 它开头会 Clear 一次。
        if (_historyNote is { Length: > 0 })
        {
            HistoryList.Children.Add(new TextBlock
            {
                Text = _historyNote,
                FontSize = 12,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }
    }

    private async void LoadHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        LoadHistoryButton.IsEnabled = false;
        HistoryRing.Visibility = Visibility.Visible;
        HistoryRing.IsActive = true;
        HistoryList.Children.Clear();
        HistoryPager.Visibility = Visibility.Collapsed;

        try
        {
            var rollbackChannel = UpdateChannels.Parse(App.Settings.Current.UpdateChannel);

            _historyNote = rollbackChannel == UpdateChannel.Stable
                ? "当前是稳定通道，只列正式版；想看 Insider 版（Beta）就先在上面把更新通道切成 Insider 再加载。"
                : null;

            _historyReleases = await _updater.GetHistoryAsync(rollbackChannel, 20);
            _historyPage = 1;                     // 重新加载＝回第一页
            RenderHistoryPage();
        }
        catch (Exception ex)
        {
            _historyReleases = null;
            _historyNote = null;
            HistoryPager.Visibility = Visibility.Collapsed;
            HistoryList.Children.Clear();
            HistoryList.Children.Add(new TextBlock
            {
                Text = "加载失败：" + ex.Message,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        finally
        {
            _updateBusy = false;
            LoadHistoryButton.IsEnabled = true;
            HistoryRing.IsActive = false;
            HistoryRing.Visibility = Visibility.Collapsed;
        }
    }

    private Grid HistoryRow(UpdateRelease release)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var date = release.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? "";
        var size = release.Primary?.SizeText ?? "";
        var badge = release.Prerelease ? "Insider" : "正式版";

        var info = new StackPanel { Spacing = 2 };
        info.Children.Add(new TextBlock
        {
            Text = $"{release.Tag}   [{badge}]",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        info.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", new[] { date, size }.Where(s => s.Length > 0)),
            FontSize = 12,
            Opacity = 0.65,
        });

        var install = new Button { Content = "装这个", VerticalAlignment = VerticalAlignment.Center };
        install.Click += async (_, _) => await RollbackAsync(release);
        ToolTipService.SetToolTip(install, "安装这个版本（覆盖当前程序，设置和内容不会丢）");

        Grid.SetColumn(info, 0);
        Grid.SetColumn(install, 1);
        row.Children.Add(info);
        row.Children.Add(install);
        return row;
    }

    private async Task RollbackAsync(UpdateRelease release)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "回滚到这个版本？",
            Content = $"将要安装 {release.Tag}（{(release.Prerelease ? "Insider" : "正式版")}）。\n" +
                      "会覆盖当前程序文件；你的设置和软件内容不会丢。",
            PrimaryButtonText = "安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        await UpdateFlow.RunAsync(XamlRoot, _updater, release, "正在安装指定版本");
    }

    private static string Snip(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.Replace("\r", "").Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }

    // ── 安装包自动清理（updates 目录） ─────────────────────────

    /// <summary>保留数量改动：存档 + 后台立刻清一轮（不用等下次启动），列表若摊开着就跟着刷新。</summary>
    private void KeepCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (KeepCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (!int.TryParse(tag, out var keep)) return;

        App.Settings.Current.InstallerKeepCount = Math.Clamp(keep, 1, 10);
        App.Settings.Save();

        var target = App.Settings.Current.InstallerKeepCount;
        _ = Task.Run(() =>
        {
            try { InstallerCleanup.Clean(target); }
            catch { /* 清理失败不打扰界面，下次启动会再试 */ }
        }).ContinueWith(_ =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (InstallerExpander.IsExpanded) RefreshInstallerList();
            });
        });
    }

    private void Installer_Expanded(object sender, EventArgs args)
    {
        _installerPage = 1;             // 重新展开＝从第一页看起
        RefreshInstallerList();
    }

    private void InstallerPrev_Click(object sender, RoutedEventArgs args)
    {
        _installerPage--;
        RefreshInstallerList();
    }

    private void InstallerNext_Click(object sender, RoutedEventArgs args)
    {
        _installerPage++;
        RefreshInstallerList();
    }

    private void RefreshInstallerList()
    {
        InstallerDirText.Text = $"存放位置：{InstallerCleanup.InstallerDirectory}";

        var files = InstallerCleanup.Scan();
        if (files.Count == 0)
        {
            InstallerList.Children.Clear();
            InstallerPager.Visibility = Visibility.Collapsed;
            InstallerList.Children.Add(new TextBlock
            {
                Text = "本地暂无安装包。更新完成后会自动存放于此。",
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        _installerPage = RenderPagedList(InstallerList, files, _installerPage, BuildInstallerRow,
                                         InstallerPager, InstallerPageText, InstallerPrevButton, InstallerNextButton);
    }

    /// <summary>单行：左边名称 + 大小/时间，右边「覆盖安装」「删除」。实例方法 —— Click 里要用 this.XamlRoot。</summary>
    private StackPanel BuildInstallerRow(InstallerFileInfo f)
    {
        var meta = $"{FormatSize(f.Length)}　·　{f.ModifiedUtc.LocalDateTime:yyyy-MM-dd HH:mm}"
                   + (f.HasMd5File ? "　·　含校验文件" : "");

        var name = new TextBlock
        {
            Text = f.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        var sub = new TextBlock
        {
            Text = meta,
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
        };

        // 「覆盖安装」：留着这个包的直接用途 —— 原地覆盖装一遍当前（或更旧）版本，不用重新下载。
        // 走的是更新流程同一套「先退应用、安装程序后起」，见 UpdateFlow.InstallLocalAsync。
        var install = new Button { Content = "覆盖安装" };
        ToolTipService.SetToolTip(install, "用这个安装包原地覆盖安装，安装目录与各项设置保持不变。");
        install.Click += async (_, _) => await InstallFromLocalAsync(f);

        var del = new Button { Content = "删除" };
        del.Click += async (_, _) => await DeleteInstallerAsync(f);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        actions.Children.Add(install);
        actions.Children.Add(del);

        var left = new StackPanel { Spacing = 2 };
        left.Children.Add(name);
        left.Children.Add(sub);

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        left.VerticalAlignment = VerticalAlignment.Center;
        actions.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(left, 0);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(left);
        grid.Children.Add(actions);

        return new StackPanel { Spacing = 0, Children = { grid } };
    }

    /// <summary>
    /// 用本地已有的安装包覆盖安装（不必重新下载）。
    /// ⚠️ 真正的"先退应用、安装程序后起"由 <see cref="UpdateFlow.InstallLocalAsync"/> 保证 ——
    ///    顺序反了安装会被 Inno 静默取消（见 UpdateService.RunInstaller 的注释）。
    /// </summary>
    private async Task InstallFromLocalAsync(InstallerFileInfo f)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = f.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = "安装程序会按同一个应用标识原地覆盖安装，安装目录与各项设置都保持不变，无需重新下载。"
                 + "安装过程中应用会自动关闭，装好后自动重新打开（约十几秒），此过程并非程序异常。",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });

        var confirm = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "用这个安装包覆盖安装",
            Content = body,
            PrimaryButtonText = "覆盖安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        await UpdateFlow.InstallLocalAsync(f.FullPath);
    }

    private async Task DeleteInstallerAsync(InstallerFileInfo f)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "删除安装包",
            Content = $"删除 {f.Name}？删除后装回该版本需重新下载。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        InstallerCleanup.TryDeleteWithMd5(f.FullPath);
        RefreshInstallerList();
    }

    // ── 素材缓存（cache 目录，按版本分目录，见 CacheHome） ─────────────────────────

    /// <summary>保留数量改动：存档 + 后台立刻清一轮（不用等下次启动），列表摊开着就跟着刷新。</summary>
    private void CacheKeepCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (CacheKeepCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (!int.TryParse(tag, out var keep)) return;

        App.Settings.Current.CacheKeepCount = Math.Clamp(keep, 1, 10);
        App.Settings.Save();

        var target = App.Settings.Current.CacheKeepCount;
        _ = Task.Run(() =>
        {
            try { Services.CacheHome.Clean(target); }
            catch { /* 清理失败不打扰界面，下次启动会再试 */ }
        }).ContinueWith(_ =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (CacheExpander.IsExpanded) RefreshCacheList();
            });
        });
    }

    private void Cache_Expanded(object sender, EventArgs args)
    {
        _cachePage = 1;                 // 重新展开＝从第一页看起
        RefreshCacheList();
    }

    private void CachePrev_Click(object sender, RoutedEventArgs args)
    {
        _cachePage--;
        RefreshCacheList();
    }

    private void CacheNext_Click(object sender, RoutedEventArgs args)
    {
        _cachePage++;
        RefreshCacheList();
    }

    private void OpenCacheDir_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Services.CacheHome.Root);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Services.CacheHome.Root}\"") { UseShellExecute = true });
        }
        catch { /* 打不开就算了，本来只是给个方便 */ }
    }

    private void RefreshCacheList()
    {
        var items = Services.CacheHome.Scan();
        var (bytes, files) = Services.CacheHome.Measure();

        CacheDirText.Text = $"存放位置：{Services.CacheHome.Root}";
        CacheSummaryText.Text = items.Count == 0
            ? "还没有版本缓存目录（用到素材时会自动建立）。"
            : $"共 {items.Count} 个版本目录 · {FormatSize(bytes)} · {files} 个文件";

        _cachePage = RenderPagedList(CacheList, items, _cachePage, BuildCacheRow,
                                     CachePager, CachePageText, CachePrevButton, CacheNextButton);
    }

    /// <summary>单行：左边目录名 + 占用/时间，右边「打开」「删除」（当前正在用的那个不给删）。</summary>
    private StackPanel BuildCacheRow(Services.CacheInstanceInfo inst)
    {
        var meta = $"{FormatSize(inst.Bytes)}　·　{inst.FileCount} 个文件　·　建立于 {inst.CreatedLocal:yyyy-MM-dd HH:mm}"
                   + (inst.IsCurrent ? "　·　当前正在使用" : "");

        var name = new TextBlock
        {
            Text = inst.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        var sub = new TextBlock
        {
            Text = meta,
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
        };

        var open = new Button { Content = "打开" };
        ToolTipService.SetToolTip(open, "在文件资源管理器中打开这个缓存目录。");
        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{inst.FullPath}\"") { UseShellExecute = true }); }
            catch { /* 打不开就算了 */ }
        };

        var del = new Button { Content = "删除" };
        if (inst.IsCurrent)
        {
            del.IsEnabled = false;
            ToolTipService.SetToolTip(del, "当前正在使用的版本目录，不能删除。");
        }
        else
        {
            ToolTipService.SetToolTip(del, "删掉这个版本的素材缓存；素材在下次用到时会自动重新解包。");
            del.Click += async (_, _) => await DeleteCacheInstanceAsync(inst);
        }

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        actions.Children.Add(open);
        actions.Children.Add(del);

        var left = new StackPanel { Spacing = 2 };
        left.Children.Add(name);
        left.Children.Add(sub);

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        left.VerticalAlignment = VerticalAlignment.Center;
        actions.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(left, 0);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(left);
        grid.Children.Add(actions);

        return new StackPanel { Spacing = 0, Children = { grid } };
    }

    private async Task DeleteCacheInstanceAsync(Services.CacheInstanceInfo inst)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "删除缓存目录",
            Content = $"删除 {inst.Name}？\n\n里面的素材在下次用到时会自动重新解包，不影响设置与任何数据。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        Services.CacheHome.TryDelete(inst.Name);
        RefreshCacheList();
    }

    /// <summary>
    /// 把 <paramref name="items"/> 的第 <paramref name="page"/> 页填进 <paramref name="host"/>，并同步分页栏。
    /// 「本地缓存」和「本地日志」结构一模一样，就这一份逻辑共用。
    ///
    /// ⚠️ 返回值是**夹紧后**的页码，调用方必须写回自己的字段：删掉最后一页最后一条时页码会超界，
    ///    不写回的话下一轮刷新会从空页算起。
    /// ⚠️ 条数不超一页时整条分页栏 Collapsed —— 只有两三条也顶一排翻页按钮太闹。
    /// </summary>
    private static int RenderPagedList<T>(
        StackPanel host,
        IReadOnlyList<T> items,
        int page,
        Func<T, UIElement> buildRow,
        StackPanel pager,
        TextBlock pageText,
        Button prev,
        Button next)
    {
        var pageCount = Math.Max(1, (items.Count + ListPageSize - 1) / ListPageSize);
        page = Math.Clamp(page, 1, pageCount);

        host.Children.Clear();
        var from = (page - 1) * ListPageSize;
        var to = Math.Min(items.Count, from + ListPageSize);
        for (var i = from; i < to; i++) host.Children.Add(buildRow(items[i]));

        pager.Visibility = items.Count > ListPageSize ? Visibility.Visible : Visibility.Collapsed;
        pageText.Text = $"第 {page} / {pageCount} 页　·　共 {items.Count} 条";
        prev.IsEnabled = page > 1;
        next.IsEnabled = page < pageCount;
        return page;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024 * 1024 * 1024) return $"{bytes / 1024.0 / 1024 / 1024:0.0#} GB";
        if (bytes >= 1024 * 1024) return $"{bytes / 1024.0 / 1024:0.0#} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0} KB";
        return $"{bytes} B";
    }

    // ── 会话日志（logs 目录，按启动时间分目录，见 LogHome） ─────────────────────────

    /// <summary>「日志保留天数」下拉的索引：3/7/15/30 天 + 永不删除（0）。</summary>
    private static int LogKeepIndex(int days) => days switch
    {
        3 => 0,
        15 => 2,
        30 => 3,
        0 => 4,     // 永不删除
        _ => 1,     // 7 天（默认；存档里的怪值也落这里）
    };

    /// <summary>保留天数改动：存档 + 后台立刻清一轮（不用等下次启动），列表摊开着就跟着刷新。</summary>
    private void LogKeepCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (LogKeepCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (!int.TryParse(tag, out var days)) return;

        App.Settings.Current.LogKeepDays = days <= 0 ? 0 : days;
        App.Settings.Save();

        var target = App.Settings.Current.LogKeepDays;
        _ = Task.Run(() =>
        {
            try { Services.LogHome.Clean(target); }
            catch { /* 清理失败不打扰界面，下次启动会再试 */ }
        }).ContinueWith(_ =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (LogExpander.IsExpanded) RefreshLogList();
            });
        });
    }

    private void Log_Expanded(object sender, EventArgs args)
    {
        _logPage = 1;                   // 重新展开＝从第一页看起
        RefreshLogList();
    }

    private void LogPrev_Click(object sender, RoutedEventArgs args)
    {
        _logPage--;
        RefreshLogList();
    }

    private void LogNext_Click(object sender, RoutedEventArgs args)
    {
        _logPage++;
        RefreshLogList();
    }

    private void OpenLogDir_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Services.LogHome.Root);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Services.LogHome.Root}\"") { UseShellExecute = true });
        }
        catch { /* 打不开就算了，本来只是给个方便 */ }
    }

    private void RefreshLogList()
    {
        var items = Services.LogHome.Scan();
        var (bytes, files) = Services.LogHome.Measure();

        LogDirText.Text = $"存放位置：{Services.LogHome.Root}";
        LogSummaryText.Text = items.Count == 0
            ? "还没有日志目录（应用启动时会自动建立）。"
            : $"共 {items.Count} 次运行 · {FormatSize(bytes)} · {files} 个文件";

        _logPage = RenderPagedList(LogList, items, _logPage, BuildLogRow,
                                   LogPager, LogPageText, LogPrevButton, LogNextButton);
    }

    /// <summary>单行：左边会话名 + 占用/时间，右边「打开」「删除」（当前正在运行的那次不给删）。</summary>
    private StackPanel BuildLogRow(Services.LogSessionInfo s)
    {
        var title = s.IsLegacy
            ? "升级前的历史日志"
            : $"{s.StartedLocal:yyyy-MM-dd HH:mm:ss} 那次运行";

        var meta = $"{FormatSize(s.Bytes)}　·　{s.FileCount} 个文件"
                   + (s.IsLegacy ? "　·　旧版按模块分的日志，已归档" : "")
                   + (s.IsCurrent ? "　·　当前正在运行" : "");

        var name = new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        var sub = new TextBlock
        {
            Text = meta,
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
        };

        var open = new Button { Content = "打开" };
        ToolTipService.SetToolTip(open, "在文件资源管理器中打开这次运行的日志目录。");
        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{s.FullPath}\"") { UseShellExecute = true }); }
            catch { /* 打不开就算了 */ }
        };

        var del = new Button { Content = "删除" };
        if (s.IsCurrent)
        {
            del.IsEnabled = false;
            ToolTipService.SetToolTip(del, "当前正在运行的那份日志，不能删除。");
        }
        else
        {
            ToolTipService.SetToolTip(del, "删掉这次运行留下的全部日志。");
            del.Click += async (_, _) => await DeleteLogSessionAsync(s);
        }

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        actions.Children.Add(open);
        actions.Children.Add(del);

        var left = new StackPanel { Spacing = 2 };
        left.Children.Add(name);
        left.Children.Add(sub);

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        left.VerticalAlignment = VerticalAlignment.Center;
        actions.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(left, 0);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(left);
        grid.Children.Add(actions);

        return new StackPanel { Spacing = 0, Children = { grid } };
    }

    private async Task DeleteLogSessionAsync(Services.LogSessionInfo s)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "删除日志",
            Content = $"删除「{s.StartedLocal:yyyy-MM-dd HH:mm:ss}」这次运行的日志？\n\n"
                      + $"{FormatSize(s.Bytes)}、{s.FileCount} 个文件，删掉就找不回来了。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        Services.LogHome.TryDelete(s.Name);
        RefreshLogList();
    }
}
