using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services.Updating;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>原生设置页：关于（最上，突出版本号）/ 外观 / 窗口行为 / 内容。改动即时生效。</summary>
public sealed partial class SettingsPage : Page
{
    private bool _loading = true;
    private bool _contentBusy;

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
        ThemeCombo.SelectedIndex = s.Theme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0
        };
        UpdateMinimizeAvailability();

        ChannelCombo.SelectedIndex = UpdateChannels.Parse(s.UpdateChannel) == UpdateChannel.Insider ? 1 : 0;
        AutoCheckSwitch.IsOn = s.AutoCheckUpdate;
        UpdateCurrent.Text = $"当前版本：{ShellConfig.VersionPrefix}{ShellConfig.ShellVersion}· 更新源：{UpdateService.CreateDefault().Source.DisplayName}";

        RefreshContentInfo();
            BuildAboutHeader();
            SettingsQuickGrid.ItemsSource = Core.QuickLinks.Build(App.Content.Ui);
            _loading = false;
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
            ? "开机启动时直接把窗口收进任务栏，不弹出来。"
            : "只有打开「开机自动启动」时，这个选项才有用。";
    }

    private void ThemeChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ThemeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string theme) return;
        App.MainWindow?.SetTheme(theme);       // 里面会 Save + 重算背景
        App.MainWindow?.Shell.UpdateThemeButton();
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

    /// <summary>检查更新：只要有新版，就直接进强制更新流程（没有「稍后」这个选项）。</summary>
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
                UpdateStatus.Text = $"发现新版本 {release.Tag}，开始强制更新…";

                if (!await UpdateFlow.RunAsync(XamlRoot, _updater, release))
                    UpdateStatus.Text = "更新失败：稍后可重试，或去发布页手动下载新版。";
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
        if (e.ClickedItem is Core.QuickLink link && link.Url.Length > 0)
            App.MainWindow?.OpenExternal(link.Url);
    }

    // ── 版本记录（本机运行过的版本） ────────────────────────────

    private void History_Expanding(Expander sender, ExpanderExpandingEventArgs args) => LoadLocalHistory();

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
