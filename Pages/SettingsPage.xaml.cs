using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services.Updating;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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

    public SettingsPage()
    {
        InitializeComponent();
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
        UpdateCurrent.Text = $"当前版本：{ShellConfig.VersionPrefix}{ShellConfig.ShellVersion}· 更新源：{UpdateService.CreateDefault().Source.DisplayName}";

        RefreshContentInfo();
            BuildAboutHeader();
            SettingsQuickGrid.ItemsSource = Core.QuickLinks.Build(App.Content.Ui);
            _loading = false;

        // 「版本记录」固定折叠：里面是十几条运行记录，摊开会把设置页拉得极长。
        // ⚠️ 只在 XAML 里写 IsExpanded="False" 不够 —— 实测加载过程中仍会被撑开，这里再压一次。
        HistoryExpander.IsExpanded = false;
        InstallerExpander.IsExpanded = false;
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

            // 三个折叠区在导航后 200ms（早已过布局）再压一次，防"首帧摊开"。
            HistoryExpander.IsExpanded = false;
            InstallerExpander.IsExpanded = false;
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
                        UpdateStatus.Text = $"已转为后台下载 {release.Tag}，完成后通过系统通知提醒。";
                    else
                        UpdateStatus.Text = "已有一个更新正在后台下载，完成后会通知。";
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
            var path = Services.EmbeddedAssets.ExtractToCache("AppIcon-512.png", "AppIcon-512.png");
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
    private void History_Expanded(object sender, EventArgs args) => LoadLocalHistory();

    /// <summary>本机记录：这台电脑运行过哪些版本（启动时自动记的），不是仓库的 Release 列表。</summary>
    private void LoadLocalHistory()
    {
        LocalHistoryList.Children.Clear();
        var records = Services.VersionHistory.Load();
        if (records.Count == 0)
        {
            LocalHistoryList.Children.Add(new TextBlock
            {
                Text = "本机还没有版本记录（这次启动就会记下第一条）。",
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        var current = Core.ShellConfig.ShellVersion;
        foreach (var record in records)
        {
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
            LocalHistoryList.Children.Add(row);
        }
    }

    // ── 历史版本（回滚） ──────────────────────────────────────

    private async void LoadHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        LoadHistoryButton.IsEnabled = false;
        HistoryRing.Visibility = Visibility.Visible;
        HistoryRing.IsActive = true;
        HistoryList.Children.Clear();

        try
        {
            var rollbackChannel = UpdateChannels.Parse(App.Settings.Current.UpdateChannel);
            var releases = await _updater.GetHistoryAsync(rollbackChannel, 20);
            if (releases.Count == 0)
            {
                HistoryList.Children.Add(new TextBlock
                {
                    Text = _updater.Source.IsConfigured ? "没查到版本（仓库里还没发过 Release）。" : "还没配置更新仓库地址。",
                    FontSize = 12,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }
            foreach (var release in releases)
                HistoryList.Children.Add(HistoryRow(release));

            // 正式版用户看不到预览版：说清楚，免得以为列表缺斤少两
            if (rollbackChannel == UpdateChannel.Stable)
            {
                HistoryList.Children.Add(new TextBlock
                {
                    Text = "当前是稳定通道，只列正式版；想看 Insider 版（Beta）就先在上面把更新通道切成 Insider 再加载。",
                    FontSize = 12,
                    Opacity = 0.6,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0),
                });
            }
        }
        catch (Exception ex)
        {
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

    private void Installer_Expanded(object sender, EventArgs args) => RefreshInstallerList();

    private void RefreshInstallerList()
    {
        InstallerList.Children.Clear();
        InstallerDirText.Text = $"存放位置：{InstallerCleanup.InstallerDirectory}";

        var files = InstallerCleanup.Scan();
        if (files.Count == 0)
        {
            InstallerList.Children.Add(new TextBlock
            {
                Text = "本地暂无安装包。更新完成后会自动存放于此。",
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var f in files)
            InstallerList.Children.Add(BuildInstallerRow(f));
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

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024 * 1024 * 1024) return $"{bytes / 1024.0 / 1024 / 1024:0.0#} GB";
        if (bytes >= 1024 * 1024) return $"{bytes / 1024.0 / 1024:0.0#} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0} KB";
        return $"{bytes} B";
    }
}
