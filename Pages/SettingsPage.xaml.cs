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
    /// <summary>「贴在哪条边」下拉正在按模式重建选项（期间忽略 SelectionChanged）。</summary>
    private bool _edgeRebuild;
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

        // 常用工具窗口 / 侧边栏
        PaletteTopSwitch.IsOn = s.PaletteOnTop;
        SidebarSwitch.IsOn = s.SidebarEnabled;

        // 贴靠模式只认左右两条边（Nick 2026-09-28 定：贴靠 = 左右模式）。
        // 老设置里若留着 top/bottom，这里归到右边 —— 想贴上下边需切到自由模式。
        if (s.SidebarEdge is not ("left" or "both" or "right"))
        {
            s.SidebarEdge = "right";
            App.Settings.Save();
        }

        // 自由模式：左 / 右 / 上 / 下四条边
        if (s.SidebarFreeEdge is not ("left" or "right" or "top" or "bottom"))
        {
            s.SidebarFreeEdge = "right";
            App.Settings.Save();
        }

        SidebarModeCombo.SelectedIndex = s.SidebarMode == "free" ? 1 : 0;
        RebuildEdgeCombo();
        UpdateSidebarHints();

        // 截图自动保存
        ShotAutoSaveSwitch.IsOn = s.ShotAutoSave;
        RefreshShotDir();
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
        UpdateCurrent.Text = $"当前版本：{ShellConfig.VersionPrefix}{ShellConfig.ShellVersion}· 更新源：{UpdateService.CreateDefault().Source.DisplayName}";

        RefreshContentInfo();
            BuildAboutHeader();
            SettingsQuickGrid.ItemsSource = Core.QuickLinks.Build(App.Content.Ui);
            _loading = false;

        // 「版本记录」固定折叠：里面是十几条运行记录，摊开会把设置页拉得极长。
        // ⚠️ 只在 XAML 里写 IsExpanded="False" 不够 —— 实测加载过程中仍会被撑开，这里再压一次。
        HistoryExpander.IsExpanded = false;

        // 打开设置页固定从顶部开始：卡片高度会被设置值二次刷新，ScrollViewer 的锚点跟着漂，
        // 实测会直接停到「常用工具」那一段（2026-09-28）。
        _settleTimer = DispatcherQueue.CreateTimer();
        _settleTimer.Interval = TimeSpan.FromMilliseconds(200);
        _settleTimer.IsRepeating = false;
        _settleTimer.Tick += (_, _) =>
        {
            _settleTimer.Stop();
            RootScroll.ChangeView(null, 0, null, true);
        };
        _settleTimer.Start();
    }

    private void RefreshContentInfo()
    {
        var c = App.Content;
        ContentSummary.Text = c.HasData
            ? $"{c.Apps.Count} 个软件 · {c.Categories.Count} 个分类" +
              (c.ContentVersion.Length > 0 ? $" · 内容版本 {c.ContentVersion}" : "")
            : "没有读到内容。";

        ContentSource.Text = $"内容来源：{c.SourceLabel}\n{c.Source}";

        ContentIssues.Text = c.Issues.Count > 0
            ? $"有 {c.Issues.Count} 条解析提示：" + string.Join("；", c.Issues.Take(3).Select(i => i.File + " — " + i.Message))
            : "";
        ContentIssues.Visibility = c.Issues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        AboutApp.Text = ShellConfig.AppName;
        AboutVersion.Text = ShellConfig.VersionPrefix + ShellConfig.ShellVersion;
        var siteVersion = App.Content.Ui.AppVersion;
        AboutSiteVersion.Text = siteVersion.Length > 0
            ? $"站点版本：{siteVersion}"
            : $"站点版本：{ShellConfig.SiteVersionTarget}";
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

    private void PaletteTopSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.MainWindow?.SetPaletteOnTop(PaletteTopSwitch.IsOn);
    }

    private void SidebarSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Current.SidebarEnabled = SidebarSwitch.IsOn;
        App.Settings.Save();
        Views.ToolSidebarWindow.ApplySetting();
    }

    // ── 侧边栏放置模式 / 贴在哪条边 ───────────────────────────
    // 2026-09-28 Nick：这两项本质是"多个互斥选项里选一个"，跟「颜色模式」「更新通道」同类，
    // 一律做成下拉；不做成一排单选按钮（会把 Header 和控件挤到卡片两端，中间空出一大片）。

    /// <summary>
    /// 按当前放置模式重建「贴在哪条边」的选项：
    /// 贴靠 = 左 / 左右两边 / 右；自由 = 左 / 右 / 上 / 下。
    /// ⚠️ 重建期间必须挡住 SelectionChanged —— Items.Clear() 会把 SelectedIndex 打成 -1，
    ///    不然会把设置误写成空值。
    /// </summary>
    private void RebuildEdgeCombo()
    {
        var s = App.Settings.Current;
        var free = s.SidebarMode == "free";

        _edgeRebuild = true;
        try
        {
            SidebarEdgeCombo.Items.Clear();
            if (free)
            {
                AddEdgeItem("左边", "left");
                AddEdgeItem("右边", "right");
                AddEdgeItem("上边", "top");
                AddEdgeItem("下边", "bottom");
                SelectEdge(s.SidebarFreeEdge);
            }
            else
            {
                AddEdgeItem("左边", "left");
                AddEdgeItem("左右两边", "both");
                AddEdgeItem("右边", "right");
                SelectEdge(s.SidebarEdge);
            }
        }
        finally
        {
            _edgeRebuild = false;
        }
    }

    private void AddEdgeItem(string text, string tag) =>
        SidebarEdgeCombo.Items.Add(new ComboBoxItem { Content = text, Tag = tag });

    private void SelectEdge(string tag)
    {
        foreach (var item in SidebarEdgeCombo.Items)
        {
            if (item is ComboBoxItem it && it.Tag as string == tag)
            {
                SidebarEdgeCombo.SelectedItem = it;
                return;
            }
        }
        if (SidebarEdgeCombo.Items.Count > 0) SidebarEdgeCombo.SelectedIndex = 0;
    }

    /// <summary>说明文案随模式切换，免得对着下拉不知道是两条边还是四条边。</summary>
    private void UpdateSidebarHints()
    {
        var free = App.Settings.Current.SidebarMode == "free";

        ModeHint.Text = free
            ? "侧边栏可吸附屏幕任意一条边；贴上边或下边时呈横条。"
            : "侧边栏只吸附屏幕左右两条边，可选左右同时显示。";

        EdgeHint.Text = free
            ? "四条边均可吸附；贴上边或下边时侧边栏为横条。拖动收起状态的抓手也可改边。"
            : "选「左右两边」时两侧同时显示，上下位置保持一致，拖动其中一条另一条同步移动。拖动收起状态的抓手也可改边。";
    }

    private void SidebarMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (SidebarModeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string mode) return;

        App.Settings.Current.SidebarMode = mode;
        App.Settings.Save();

        // 模式变了 → 可选的边也变了，下拉要整个换一套
        RebuildEdgeCombo();
        UpdateSidebarHints();

        if (App.Settings.Current.SidebarEnabled) Views.ToolSidebarWindow.ApplySetting();
    }

    private void SidebarEdge_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _edgeRebuild) return;
        if (SidebarEdgeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string edge) return;

        var s = App.Settings.Current;
        if (s.SidebarMode == "free") s.SidebarFreeEdge = edge;
        else s.SidebarEdge = edge;
        App.Settings.Save();

        if (s.SidebarEnabled) Views.ToolSidebarWindow.ApplySetting();
    }

    // ── 截图自动保存 ──────────────────────────────────────────

    private void ShotAutoSave_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Current.ShotAutoSave = ShotAutoSaveSwitch.IsOn;
        App.Settings.Save();
        RefreshShotDir();
    }

    /// <summary>把当前保存位置显示出来（没设 = 桌面）。</summary>
    private void RefreshShotDir()
    {
        var dir = Services.ShotSaver.DirSetting();
        var custom = !string.IsNullOrWhiteSpace(App.Settings.Current.ShotSaveDir);
        ShotDirText.Text = custom ? dir : $"{dir}（默认：桌面，没改过）";
        ShotDirText.Opacity = ShotAutoSaveSwitch.IsOn ? 0.7 : 0.4;
    }

    private async void ShotDir_Change_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop,
            };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            App.Settings.Current.ShotSaveDir = folder.Path;
            App.Settings.Save();
            RefreshShotDir();
        }
        catch (Exception ex)
        {
            Services.ScreenCapture.Log("选截图目录失败: " + ex.Message);
        }
    }

    private void ShotDir_Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Services.ShotSaver.Dir();
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Services.ScreenCapture.Log("打开截图目录失败: " + ex.Message);
        }
    }

    private void ShotDir_Reset_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Current.ShotSaveDir = "";                // 空 = 桌面
        App.Settings.Save();
        RefreshShotDir();
    }

    private void ReloadContent_Click(object sender, RoutedEventArgs e)
    {
        App.Content.Load();
        RefreshContentInfo();
    }

    /// <summary>手动同步内容包（远端发布了才有东西下；失败也不影响本机数据）。</summary>
    private async void SyncContent_Click(object sender, RoutedEventArgs e)
    {
        if (_contentBusy) return;
        _contentBusy = true;

        ContentSource.Text = "正在同步内容包…";
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
                if (!await UpdateFlow.AskAsync(XamlRoot, release)) return;

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
}
