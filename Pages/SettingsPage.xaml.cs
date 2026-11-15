using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Services.License;
using ClassSoftwareHub.Desktop.Services.Updating;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace ClassSoftwareHub.Desktop.Pages;


public sealed partial class SettingsPage : Page
{
    
    private HotkeyAction? _recordingHotkey;

    
    private Button? _recordingButton;

    
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _recordTimer;

    private bool _loading = true;
    private bool _contentBusy;
    
    
    
    
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _settleTimer;

    
    
    
    
    private const int ListPageSize = 10;

    private int _cachePage = 1;
    private int _logPage = 1;
    private int _installerPage = 1;
    private int _localHistoryPage = 1;
    private int _historyPage = 1;

    
    
    
    
    
    private IReadOnlyList<UpdateRelease>? _historyReleases;

    
    private string? _historyNote;

    public SettingsPage()
    {
        InitializeComponent();

        
        ActualThemeChanged += (_, _) => ApplyQuickLinkIcons();

    }

    
    private void ApplyQuickLinkIcons()
    {
        if (SettingsQuickGrid.ItemsSource is not List<Core.QuickLink> links) return;
        var color = ((SolidColorBrush)ThemeBrush.Get(this, "TextFillColorPrimaryBrush")).Color;
        foreach (var link in links)
            if (!string.IsNullOrEmpty(link.IconSvg))
                link.IconSource = QuickLinkIconLoader.GetIcon(link.IconSvg, color);
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    public void ActivateSection(string? section)
    {
        var key = (section ?? "").Trim().ToLowerInvariant();

        
        
        
        _settleTimer?.Stop();

        FrameworkElement? target = key switch
        {
            "appearance" or "theme" => ThemeCard,
            "startup" => StartupCard,
            "hotkeys" or "hotkey" or "keys" => HotkeyExpander,
            "hubai" or "ai" => HubAiCard,
            "cache" => CacheExpander,
            "logs" or "log" => LogExpander,
            "content" => ContentCard,
            "update" or "updates" or "version" => UpdateSectionHeader,
            "about" => AboutVersion,
            _ => null,
        };

        if (target is null)
        {
            try { RootScroll.ChangeView(null, 0, null, disableAnimation: true); } catch { }
            Core.AppLog.Info("settings", $"链接指向的分组认不出「{section}」，已停在页面顶部");
            return;
        }

        Bring(target);
    }

    
    
    
    
    
    
    
    private static void Bring(FrameworkElement target)
    {
        void ScrollNow()
        {
            try
            {
                target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
            }
            catch (Exception ex)
            {
                Core.AppLog.Warn("settings", "滚动到指定分组失败：" + ex.Message);
            }
        }

        if (target.IsLoaded)
        {
            ScrollNow();
            return;
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            target.Loaded -= OnLoaded;
            ScrollNow();
        }

        target.Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _loading = true;

        
        
        App.Content.Changed -= OnContentChanged;
        App.Content.Changed += OnContentChanged;

        var s = App.Settings.Current;
        BackdropCombo.SelectedIndex = s.Backdrop switch
        {
            "mica" => 1,
            "solid" => 2,
            _ => 0
        };
        AiTemplateCombo.SelectedIndex = s.HubAiTemplate switch
        {
            "course" => 1,
            "solver" => 2,
            "coder" => 3,
            _ => 0,
        };
        TopMostSwitch.IsOn = s.AlwaysOnTop;
        AutoStartSwitch.IsOn = s.AutoStart;
        MinimizeSwitch.IsOn = s.MinimizeOnStart;
        TraySwitch.IsOn = s.CloseToTray;
        HotkeysSwitch.IsOn = s.HotkeysEnabled;
        UpdateHotkeyButtons();
        UpdateHotkeyStatus();
        RefreshDisplayInfo();

        ThemeCombo.SelectedIndex = s.Theme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0
        };

        
        GithubRouteCombo.SelectedIndex = Services.GithubRoute.Current switch
        {
            Services.GithubRoutes.SelfHosted => 1,
            Services.GithubRoutes.Official => 2,
            _ => 0,
        };
        UpdateGithubRouteHint();
        RefreshDownloadFolder();

        
        DictSourceCombo.SelectedIndex = Core.Dictionary.DictSourceModes.Normalize(s.DictionarySource) switch
        {
            Core.Dictionary.DictSourceModes.OnlineFirst => 1,
            Core.Dictionary.DictSourceModes.OfflineOnly => 2,
            Core.Dictionary.DictSourceModes.OnlineOnly => 3,
            _ => 0,
        };

        
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
        
        KeepCombo.SelectedIndex = Math.Clamp(s.InstallerKeepCount, 1, 10) - 1;
        
        CacheKeepCombo.SelectedIndex = Math.Clamp(s.CacheKeepCount, 1, 10) - 1;
        
        LogKeepCombo.SelectedIndex = LogKeepIndex(s.LogKeepDays);
        UpdateCurrent.Text = $"当前版本：{ShellConfig.VersionPrefix}{ShellConfig.DisplayVersion}· 更新源：{UpdateService.CreateDefault().Source.DisplayName}";

        RefreshContentInfo();
        BuildAboutHeader();
        SettingsQuickGrid.ItemsSource = Core.QuickLinks.Build(App.Content.Ui);
        ApplyQuickLinkIcons();
        _loading = false;

        
        
        HistoryExpander.IsExpanded = false;
        InstallerExpander.IsExpanded = false;
        CacheExpander.IsExpanded = false;

        
        _settleTimer = DispatcherQueue.CreateTimer();
        _settleTimer.Interval = TimeSpan.FromMilliseconds(200);
        _settleTimer.IsRepeating = false;
        _settleTimer.Tick += (_, _) =>
        {
            _settleTimer.Stop();
            RootScroll.ChangeView(null, 0, null, true);

            
            HistoryExpander.IsExpanded = false;
            InstallerExpander.IsExpanded = false;
        };
        _settleTimer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        App.Content.Changed -= OnContentChanged;
        base.OnNavigatedFrom(e);
    }

    
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
        AboutVersion.Text = ShellConfig.VersionPrefix + ShellConfig.DisplayVersion;

        
        
        
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
        App.MainWindow?.SetTheme(theme);       
    }

    

    private void SplitTheme_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Current.SplitTheme = SplitThemeSwitch.IsOn;
        App.Settings.Save();
        UpdateExtThemeAvailability();
        Services.ThemeHost.Notify();           
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
            ? "只有侧边栏与「常用工具」浮窗跟着这个走；主界面与其它窗口仍跟「主题」"
            : "现在是关的：常用工具浮窗跟主界面的颜色模式保持一致";
    }

    private void BackdropChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (BackdropCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string kind) return;
        App.Settings.Current.Backdrop = kind;
        App.Settings.Save();
        App.MainWindow?.SetBackdrop(kind);
    }


    private void AiTemplate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (AiTemplateCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string template) return;
        App.Settings.Current.HubAiTemplate = template;
        App.Settings.Save();
    }

    private void Migration_Click(object sender, RoutedEventArgs e)
        => Frame.Navigate(typeof(MigrationWizardPage));

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
        catch {  }
    }

    

    
    
    private void OpenLogViewer_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(LogViewerPage));
    }

    
    private void OpenHubAiSettings_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(HubAiSettingsPage));
    }

    

    private void GithubRoute_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (GithubRouteCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string route) return;

        App.Settings.Current.GithubDownloadRoute = Services.GithubRoutes.Normalize(route);
        App.Settings.Save();

        
        Services.GithubRoute.InvalidateCache();
        UpdateGithubRouteHint();
    }

    

    
    
    
    
    private void RefreshDownloadFolder()
    {
        var custom = App.Settings.Current.DownloadFolder;
        var resolved = Services.DownloadService.SaveDir;

        if (string.IsNullOrWhiteSpace(custom))
            DownloadDirText.Text = "系统下载文件夹：" + resolved;
        else if (string.Equals(custom, resolved, StringComparison.OrdinalIgnoreCase))
            DownloadDirText.Text = resolved;
        else
            
            
            DownloadDirText.Text = "原设定的文件夹当前不可用，已改用：" + resolved;

        DownloadDirResetBtn.IsEnabled = !string.IsNullOrWhiteSpace(custom);
    }

    private async void DownloadDir_Change_Click(object sender, RoutedEventArgs e)
    {
        var picked = await PickFolderAsync(this);
        if (string.IsNullOrEmpty(picked)) return;

        App.Settings.Current.DownloadFolder = picked;
        App.Settings.Save();
        RefreshDownloadFolder();
    }

    private void DownloadDir_Open_Click(object sender, RoutedEventArgs e)
        => Services.DownloadService.OpenDownloadsFolder();

    private void DownloadDir_Reset_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Current.DownloadFolder = "";
        App.Settings.Save();
        RefreshDownloadFolder();
    }

    
    private static async Task<string?> PickFolderAsync(FrameworkElement owner)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
            };
            picker.FileTypeFilter.Add("*");
            Core.WindowHandle.BindPicker(picker, owner);

            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn("download", "选择下载文件夹失败：" + ex.Message);
            return null;
        }
    }

    

    private void DictSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (DictSourceCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string mode) return;

        App.Settings.Current.DictionarySource = Core.Dictionary.DictSourceModes.Normalize(mode);
        App.Settings.Save();
    }

    
    
    
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
    }

    private void AutoCheckSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Current.AutoCheckUpdate = AutoCheckSwitch.IsOn;
        App.Settings.Save();
    }

    
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        CheckButton.IsEnabled = false;
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
                
                
                
                UpdateStatus.Text = $"发现新版本 {release.Tag}，可自行选择是否安装。";

                
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

    
    private void BuildAboutHeader()
    {
        try
        {
            var path = Services.EmbeddedAssets.ExtractToCache("AppIcon-512.png", "app-icon.png");
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                AboutIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));
        }
        catch {  }
    }

    private void Quick_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Core.QuickLink link) return;

        
        if (link.Tag.Length > 0)
        {
            App.MainWindow?.Shell.NavigateTo(link.Tag);
            return;
        }

        if (link.Url.Length > 0)
            App.MainWindow?.OpenExternal(link.Url);
    }

    

    
    
    
    
    
    private void History_Expanded(object sender, EventArgs args)
    {
        _localHistoryPage = 1;          
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
            _historyPage = 1;                     
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
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
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
            catch {  }
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
        _installerPage = 1;             
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
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
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
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
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
            catch {  }
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
        _cachePage = 1;                 
        RefreshCacheList();
    }

    private void CachePrev_Click(object sender, RoutedEventArgs args)
    {
        _cachePage--;
        RefreshCacheList();
    }

    

    
    
    
    
    
    
    
    
    
    
    
    
    private async void CustomizeNav_Click(object sender, RoutedEventArgs e)
    {
        var switches = new List<(NavPlaceable Item, ToggleSwitch Switch)>();

        
        
        
        var body = new StackPanel { Spacing = 12, Margin = new Thickness(0, 0, 16, 0) };

        body.Children.Add(new TextBlock
        {
            Text = "勾选的页面单独放在导航栏上，取消勾选的收进所属分组。首页、软件下载、Hub AI、"
                 + "侧边布局、函数图像固定显示；实验性功能的页面始终留在分组内。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.7,
        });

        foreach (var group in new[] { ToolGroup.Classroom, ToolGroup.System })
        {
            body.Children.Add(new TextBlock
            {
                Text = group == ToolGroup.System ? "系统工具" : "课堂工具",
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 0),
            });

            var list = new StackPanel { Spacing = 2 };

            foreach (var item in NavLayout.All.Where(x => x.Group == group))
            {
                var toggle = new ToggleSwitch
                {
                    IsOn = NavLayout.IsTopLevel(item.Tag),
                    Tag = item.Tag,
                    MinWidth = 0,
                };

                
                toggle.Toggled += NavSwitch_Toggled;

                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                row.Children.Add(new TextBlock
                {
                    Text = item.Name,
                    FontSize = 13.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });

                Grid.SetColumn(toggle, 1);
                row.Children.Add(toggle);

                list.Children.Add(row);
                switches.Add((item, toggle));
            }

            body.Children.Add(list);
        }

        var scroller = new ScrollViewer
        {
            Content = body,
            
            Width = 436,
            MaxHeight = 460,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "自定义导航栏",
            Content = scroller,
            SecondaryButtonText = "恢复默认",
            CloseButtonText = "完成",
            DefaultButton = ContentDialogButton.Close,
        };

        
        
        
        dialog.Opened += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => scroller.ChangeView(null, 0, null, disableAnimation: true));
        };

        
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;      

            App.Settings.Current.NavTopLevel = Array.Empty<string>();
            App.Settings.Current.NavSecondLevel = Array.Empty<string>();
            App.Settings.Save();
            App.MainWindow?.Shell.ReloadNavLayout();

            foreach (var (item, toggle) in switches)
            {
                if (toggle.IsOn != item.DefaultTopLevel) toggle.IsOn = item.DefaultTopLevel;
            }
        };

        await dialog.ShowAsync();

        
        App.MainWindow?.Shell.ReloadNavLayout();
    }

    private void NavSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { Tag: string tag } toggle) return;

        NavLayout.SetTopLevel(tag, toggle.IsOn);
        App.Settings.Save();
        App.MainWindow?.Shell.ReloadNavLayout();
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
        catch {  }
    }

    
    
    
    
    
    
    
    
    
    
    
    private async void ClearCache_Click(object sender, RoutedEventArgs args)
    {
        var stale = Services.CacheHome.Scan().Where(x => !x.IsCurrent).ToList();

        if (stale.Count == 0)
        {
            await ShowCacheMessageAsync("清除缓存",
                "没有可以清除的旧版本缓存。\n\n当前正在使用的那个缓存目录会被保留。", "知道了");
            return;
        }

        var bytes = stale.Sum(x => x.Bytes);
        var confirm = new ContentDialog
        {
            RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme
                             ?? Microsoft.UI.Xaml.ElementTheme.Default,
            XamlRoot = this.XamlRoot,
            Title = "清除缓存",
            Content = $"将删除 {stale.Count} 个旧版本缓存目录，释放约 {FormatSize(bytes)}。\n\n"
                    + "当前正在使用的缓存目录会保留（里面的素材正被界面读取）；"
                    + "素材在下次用到时会自动重新解包，不影响设置与任何数据。",
            PrimaryButtonText = "清除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        var result = await Task.Run(() => Services.CacheHome.ClearAll());
        RefreshCacheList();

        var message = result.Removed == 0
            ? $"没有删除任何目录：{result.Failed} 个缓存目录正被占用，下次启动时会自动清理。"
            : $"已删除 {result.Removed} 个缓存目录，释放 {FormatSize(result.FreedBytes)}。";
        if (result.Removed > 0 && result.Failed > 0)
            message += $"\n另有 {result.Failed} 个目录正被占用，下次启动时会自动清理。";

        await ShowCacheMessageAsync("清除缓存", message, "好");
    }

    
    private async Task ShowCacheMessageAsync(string title, string message, string closeText)
    {
        var dialog = new ContentDialog
        {
            RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme
                             ?? Microsoft.UI.Xaml.ElementTheme.Default,
            XamlRoot = this.XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = closeText,
        };
        await dialog.ShowAsync();
    }

    
    private void RefreshDisplayInfo()
    {
        var window = App.MainWindow;
        if (window is null)
        {
            DisplayInfoText.Text = "未获取到显示器信息";
            return;
        }

        Services.DisplayCapabilities.Refresh(window);
        DisplayInfoText.Text = Services.DisplayCapabilities.Current.Summary;
    }

    
    
    

    private void HotkeysSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Current.HotkeysEnabled = HotkeysSwitch.IsOn;
        App.Settings.Save();
        App.MainWindow?.RefreshHotkeys();
        UpdateHotkeyButtons();
        UpdateHotkeyStatus();
    }

    private void HotkeyShow_Click(object sender, RoutedEventArgs e) => BeginHotkeyRecord(HotkeyAction.ShowMain, HotkeyShowButton);

    private void HotkeyAnnounce_Click(object sender, RoutedEventArgs e) => BeginHotkeyRecord(HotkeyAction.Announce, HotkeyAnnounceButton);

    private void HotkeyTimer_Click(object sender, RoutedEventArgs e) => BeginHotkeyRecord(HotkeyAction.Timer, HotkeyTimerButton);

    private void HotkeyPpt_Click(object sender, RoutedEventArgs e) => BeginHotkeyRecord(HotkeyAction.PptTouch, HotkeyPptButton);

    private void HotkeyReset_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings.Current;
        s.HotkeyShow = AppSettings.DefaultHotkeyShow;
        s.HotkeyAnnounce = AppSettings.DefaultHotkeyAnnounce;
        s.HotkeyTimer = AppSettings.DefaultHotkeyTimer;
        s.HotkeyPptTouch = AppSettings.DefaultHotkeyPptTouch;
        App.Settings.Save();
        App.MainWindow?.RefreshHotkeys();
        UpdateHotkeyButtons();
        UpdateHotkeyStatus();
    }

    
    
    
    
    private void BeginHotkeyRecord(HotkeyAction action, Button button)
    {
        CancelHotkeyRecord();

        if (!App.Settings.Current.HotkeysEnabled)
        {
            App.Settings.Current.HotkeysEnabled = true;
            App.Settings.Save();
            HotkeysSwitch.IsOn = true;      
        }

        _recordingHotkey = action;
        _recordingButton = button;
        button.Content = "按下组合键…";
        button.Focus(FocusState.Programmatic);

        
        App.MainWindow?.SuspendHotkeys();
        HotkeyStatusText.Text = "正在录制：按下想用的组合键，Esc 取消。";

        
        _recordTimer ??= DispatcherQueue.CreateTimer();
        _recordTimer.Interval = TimeSpan.FromSeconds(5);
        _recordTimer.IsRepeating = false;
        _recordTimer.Tick -= OnRecordTimeout;
        _recordTimer.Tick += OnRecordTimeout;
        _recordTimer.Start();

        Core.AppLog.Info("hotkey", $"设置页开始录制：{HotkeyService.ActionName(action)}");
    }

    private void OnRecordTimeout(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_recordingHotkey is null) return;

        Core.AppLog.Info("hotkey", "录制超时，已自动取消");
        CancelHotkeyRecord();
        UpdateHotkeyStatus("录制超时，未设置新组合键。");
    }

    
    private void HotkeyButton_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_recordingHotkey is null) return;
        CancelHotkeyRecord();
        UpdateHotkeyStatus();
    }

    private void CancelHotkeyRecord()
    {
        _recordTimer?.Stop();

        var wasRecording = _recordingHotkey is not null;

        if (_recordingButton is not null && _recordingHotkey is not null)
            _recordingButton.Content = ComboOf(App.Settings.Current, _recordingHotkey.Value);

        _recordingHotkey = null;
        _recordingButton = null;

        
        if (wasRecording) App.MainWindow?.ResumeHotkeys();
    }

    
    
    
    
    private void HotkeyButton_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_recordingHotkey is null) return;

        e.Handled = true;

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CancelHotkeyRecord();
            return;
        }

        var vk = (uint)e.Key;
        if (HotkeyCombo.IsModifierKey(vk)) return;   

        var modifiers = CurrentModifiers();
        if (modifiers == 0) return;                   

        var action = _recordingHotkey.Value;
        if (!HotkeyCombo.TryBuild(modifiers, vk, out var text, out var error))
        {
            HotkeyStatusText.Text = error ?? "这个键不能用";
            return;
        }

        SetCombo(App.Settings.Current, action, text);
        App.Settings.Save();
        Core.AppLog.Info("hotkey", $"设置页录到新组合键：{HotkeyService.ActionName(action)} → {text}");
        CancelHotkeyRecord();      
        UpdateHotkeyButtons();
        UpdateHotkeyStatus();
    }

    
    private static uint CurrentModifiers()
    {
        uint mods = 0;
        try
        {
            if (IsKeyDown(Windows.System.VirtualKey.Control)) mods |= HotkeyCombo.ModControl;
            if (IsKeyDown(Windows.System.VirtualKey.Menu)) mods |= HotkeyCombo.ModAlt;
            if (IsKeyDown(Windows.System.VirtualKey.Shift)) mods |= HotkeyCombo.ModShift;
            if (IsKeyDown(Windows.System.VirtualKey.LeftWindows) || IsKeyDown(Windows.System.VirtualKey.RightWindows))
                mods |= HotkeyCombo.ModWin;
        }
        catch { }
        return mods;
    }

    private static bool IsKeyDown(Windows.System.VirtualKey key)
        => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down)
           == Windows.UI.Core.CoreVirtualKeyStates.Down;

    private void UpdateHotkeyButtons()
    {
        var s = App.Settings.Current;
        HotkeyShowButton.Content = ComboOf(s, HotkeyAction.ShowMain);
        HotkeyAnnounceButton.Content = ComboOf(s, HotkeyAction.Announce);
        HotkeyTimerButton.Content = ComboOf(s, HotkeyAction.Timer);
        HotkeyPptButton.Content = ComboOf(s, HotkeyAction.PptTouch);

        
        var enabled = s.HotkeysEnabled;
        HotkeyShowButton.IsEnabled = enabled;
        HotkeyAnnounceButton.IsEnabled = enabled;
        HotkeyTimerButton.IsEnabled = enabled;
        HotkeyPptButton.IsEnabled = enabled;
    }

    
    private void UpdateHotkeyStatus(string? extra = null)
    {
        var s = App.Settings.Current;
        if (!s.HotkeysEnabled)
        {
            HotkeyStatusText.Text = "已关闭。";
            return;
        }

        var conflicts = App.MainWindow?.HotkeyConflicts;
        var text = conflicts is { Count: > 0 }
            ? "未生效：" + string.Join("；", conflicts)
            : "组合键都已生效。";

        if (extra is not null) text += "  " + extra;
        HotkeyStatusText.Text = text;
    }

    private static string ComboOf(AppSettings s, HotkeyAction action) => action switch
    {
        HotkeyAction.ShowMain => s.HotkeyShow,
        HotkeyAction.Announce => s.HotkeyAnnounce,
        HotkeyAction.Timer => s.HotkeyTimer,
        _ => s.HotkeyPptTouch,
    };

    private static void SetCombo(AppSettings s, HotkeyAction action, string text)
    {
        switch (action)
        {
            case HotkeyAction.ShowMain: s.HotkeyShow = text; break;
            case HotkeyAction.Announce: s.HotkeyAnnounce = text; break;
            case HotkeyAction.Timer: s.HotkeyTimer = text; break;
            default: s.HotkeyPptTouch = text; break;
        }
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
            catch {  }
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
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
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

    

    
    private static int LogKeepIndex(int days) => days switch
    {
        3 => 0,
        15 => 2,
        30 => 3,
        0 => 4,     
        _ => 1,     
    };

    
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
            catch {  }
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
        _logPage = 1;                   
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
        catch {  }
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
            catch {  }
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
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
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
