using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Services;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>
/// .NET 环境补全。页面按「设置」页的写法组织：一张卡一项设置，
/// 同一通道的四项用 SettingsExpander 收在一起，旧版本单独成组并默认折叠（2026-11-22）。
/// </summary>
public sealed partial class DotNetPage : Page
{
    private sealed record RowUi(SettingsCard Card, TextBlock State, Button Install, ProgressRing Ring, TextBlock InstallText);

    private readonly Dictionary<DotNetComponent, RowUi> _rows = new();
    private List<DotNetChannel> _channels = new();
    private CancellationTokenSource? _cts;
    private bool _busy;

    public DotNetPage()
    {
        InitializeComponent();
        Loaded += (_, _) => _ = LoadAsync();
        Unloaded += (_, _) =>
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        };
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _cts?.Cancel();
        base.OnNavigatedFrom(e);
    }

    // ───────────────────────────── 读取 ─────────────────────────────

    private async Task LoadAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        SetLoading(true);
        try
        {
            _channels = await DotNetEnvironment.FetchChannelsAsync(token);
            token.ThrowIfCancellationRequested();

            DotNetEnvironment.RefreshInstalled(_channels);
            BuildUi();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowNotice(InfoBarSeverity.Error, "读取版本清单失败", ex.Message);
            SummaryText.Text = "";
        }
        finally
        {
            SetLoading(false);
        }
    }

    /// <summary>装完只重扫已安装版本，不再走一遍网络。</summary>
    private void RefreshInstalledOnly()
    {
        DotNetEnvironment.RefreshInstalled(_channels);
        BuildUi();
    }

    private void SetLoading(bool loading)
    {
        LoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.IsEnabled = !loading;
        RefreshButton.IsEnabled = !loading;
    }

    // ───────────────────────────── 界面 ─────────────────────────────

    private void BuildUi()
    {
        var query = (SearchBox.Text ?? "").Trim();
        ChannelPanel.Children.Clear();
        _rows.Clear();

        // 受支持的排前面，已停止支持的落在下面；同一个通道直接铺四张卡，不再套展开器。
        var ordered = _channels
            .OrderBy(c => c.Expired)
            .ThenByDescending(c => DotNetEnvironment.ParseVersion(c.ChannelVersion + ".0"))
            .ToList();

        var shown = 0;
        foreach (var channel in ordered)
        {
            var components = channel.Components.Where(c => Matches(c, channel, query)).ToList();
            if (components.Count == 0) continue;

            ChannelPanel.Children.Add(BuildChannelHeader(channel));

            // 和设置页一样：一组卡片放在 Spacing=4 的 StackPanel 里
            var cards = new StackPanel { Spacing = 4 };
            foreach (var component in components) cards.Children.Add(BuildComponent(component));
            ChannelPanel.Children.Add(cards);

            shown++;
        }

        if (shown == 0)
        {
            ChannelPanel.Children.Add(new TextBlock
            {
                Text = _channels.Count == 0 ? "没有读取到可用的通道。" : "没有匹配的组件。",
                FontSize = 12.5,
                Opacity = 0.65,
                Margin = new Thickness(2, 8, 2, 0),
            });
        }

        UpdateSummary();
    }

    /// <summary>通道标题：和设置页的分组标题同一套写法，支持状态挂在同一行右侧。</summary>
    private FrameworkElement BuildChannelHeader(DotNetChannel channel)
    {
        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 24, 0, 8),
        };

        line.Children.Add(new TextBlock
        {
            Text = channel.Title,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center,
        });

        line.Children.Add(new TextBlock
        {
            Text = channel.LifecycleText,
            FontSize = 12,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return line;
    }

    private static bool Matches(DotNetComponent component, DotNetChannel channel, string query)
    {
        if (query.Length == 0) return true;

        return channel.ChannelVersion.Contains(query, StringComparison.OrdinalIgnoreCase)
               || component.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || component.LatestVersion.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private SettingsCard BuildComponent(DotNetComponent component)
    {
        var state = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrush.Get(this, "TextFillColorSecondaryBrush"),
        };

        var ring = new ProgressRing
        {
            Width = 14,
            Height = 14,
            IsActive = false,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var installText = new TextBlock { Text = "安装", VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };

        var install = new Button
        {
            Tag = component,
            MinWidth = 96,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { ring, installText },
            },
        };
        install.Click += Install_Click;

        var card = new SettingsCard
        {
            Header = component.Label,
            HeaderIcon = new FontIcon { Glyph = GlyphOf(component.Kind) },
            Description = state,
            Content = install,
        };

        var row = new RowUi(card, state, install, ring, installText);
        _rows[component] = row;
        ApplyRowState(component, row);
        return card;
    }

    private static string GlyphOf(DotNetKind kind) => kind switch
    {
        DotNetKind.Runtime => "\uE756",       // 命令行
        DotNetKind.AspNetCore => "\uE774",    // 地球
        DotNetKind.Desktop => "\uE7F4",       // 显示器
        _ => "\uE943",                        // 代码
    };

    private void ApplyRowState(DotNetComponent component, RowUi row)
    {
        row.Ring.IsActive = false;
        row.Ring.Visibility = Visibility.Collapsed;
        row.InstallText.Text = "安装";

        if (component.UpToDate)
        {
            row.State.Text = $"最新 {component.LatestVersion} · 已安装 {component.InstalledBest}";
            row.State.Foreground = ThemeBrush.Get(this, "TextFillColorSecondaryBrush");
            row.Install.Visibility = Visibility.Collapsed;
            return;
        }

        row.Install.Visibility = Visibility.Visible;
        row.Install.IsEnabled = !_busy;

        if (component.InstalledAny)
        {
            row.State.Text = $"最新 {component.LatestVersion} · 已安装 {component.InstalledBest}，可更新";
            row.State.Foreground = ThemeBrush.Get(this, "SystemFillColorCautionBrush");
            row.InstallText.Text = "更新";
        }
        else
        {
            row.State.Text = $"最新 {component.LatestVersion} · 未安装";
            row.State.Foreground = ThemeBrush.Get(this, "TextFillColorSecondaryBrush");
            row.InstallText.Text = "安装";
        }
    }

    private void UpdateSummary()
    {
        if (_channels.Count == 0)
        {
            SummaryText.Text = "";
            return;
        }

        var components = _channels.SelectMany(c => c.Components).ToList();
        var ready = components.Count(c => c.UpToDate);
        var pending = components.Count(x => !x.UpToDate);

        var runtimeVersions = DotNetEnvironment.InstalledVersions(DotNetKind.Runtime).Count;
        var sdkVersions = DotNetEnvironment.InstalledVersions(DotNetKind.Sdk).Count;

        SummaryText.Text = $"架构 x64 · 已安装运行时 {runtimeVersions} 个版本、SDK {sdkVersions} 个版本 · " +
                           $"已就绪 {ready} 项，可补全 {pending} 项";
    }

    // ───────────────────────────── 交互 ─────────────────────────────

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        BuildUi();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not Button button || button.Tag is not DotNetComponent component) return;
        if (!_rows.TryGetValue(component, out var row)) return;

        _busy = true;
        NoticeBar.IsOpen = false;
        SetAllButtonsEnabled(false);

        row.Ring.Visibility = Visibility.Visible;
        row.Ring.IsActive = true;
        row.InstallText.Text = "安装中";
        row.State.Text = "正在准备…";

        var progress = new Progress<DotNetInstallProgress>(p =>
        {
            row.State.Text = p.Phase switch
            {
                DotNetInstallPhase.Downloading => p.Percent > 0 ? $"正在下载 {p.Percent:0}%（{p.Detail}）" : "正在连接下载地址…",
                DotNetInstallPhase.Verifying => "正在校验文件",
                DotNetInstallPhase.Installing => "正在安装，请稍候",
                _ => "已完成",
            };
        });

        try
        {
            var message = await DotNetEnvironment.InstallAsync(component, progress, _cts?.Token ?? CancellationToken.None);
            ShowNotice(InfoBarSeverity.Success, $"{component.Label} {component.LatestVersion}", message);
            RefreshInstalledOnly();
        }
        catch (OperationCanceledException ex)
        {
            ShowNotice(InfoBarSeverity.Warning, "安装没有进行", ex.Message.Length > 0 ? ex.Message : "已取消。");
        }
        catch (Exception ex)
        {
            ShowNotice(InfoBarSeverity.Error, "安装失败", ex.Message);
        }
        finally
        {
            _busy = false;

            // 装完界面可能已经重建，按当前组件重新铺一遍状态
            foreach (var (item, ui) in _rows) ApplyRowState(item, ui);
        }
    }

    private void SetAllButtonsEnabled(bool enabled)
    {
        foreach (var row in _rows.Values) row.Install.IsEnabled = enabled;
    }

    private void ShowNotice(InfoBarSeverity severity, string title, string message)
    {
        NoticeBar.Severity = severity;
        NoticeBar.Title = title;
        NoticeBar.Message = message;
        NoticeBar.IsOpen = true;
    }
}
