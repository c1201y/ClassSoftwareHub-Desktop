using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services.Updating;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>发布说明里的一行（已经转过 Markdown，直接能往界面上摆）。</summary>
public sealed class NoteLine
{
    public NoteLine(string text, double size, Windows.UI.Text.FontWeight weight, Thickness margin)
    {
        Text = text;
        Size = size;
        Weight = weight;
        Margin = margin;
    }

    public string Text { get; }
    public double Size { get; }
    public Windows.UI.Text.FontWeight Weight { get; }
    public Thickness Margin { get; }
}

/// <summary>更新日志里的一条发布。</summary>
public sealed class ReleaseRow
{
    public ReleaseRow(UpdateRelease release)
    {
        Tag = release.Tag;
        ChannelText = release.Prerelease ? "Insider" : "正式版";
        // 左侧版本清单那一栏窄，只放"日期 + 通道"两个字
        ChannelShort = release.Prerelease ? "预览" : "正式";
        DateShort = release.PublishedAt is null
            ? ""
            : release.PublishedAt.Value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        DateText = release.PublishedAt is null
            ? ""
            : release.PublishedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        BadgeBrush = Lookup(release.Prerelease ? "SystemFillColorAttentionBrush" : "SystemFillColorSuccessBrush");
        IsPrerelease = release.Prerelease;
        Lines = ChangelogPage.ParseNotes(release.Notes);
    }

    public string Tag { get; }
    public string ChannelText { get; }

    /// <summary>左侧清单用的两字通道名（"预览" / "正式"）。</summary>
    public string ChannelShort { get; }

    /// <summary>左侧清单用的短日期（不带时间）。</summary>
    public string DateShort { get; }

    public string DateText { get; }
    public Brush? BadgeBrush { get; }

    /// <summary>是不是预发布（「正式版本」筛选靠它）。</summary>
    public bool IsPrerelease { get; }

    public IReadOnlyList<NoteLine> Lines { get; }

    private static Brush? Lookup(string key)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b) return b;
            // 主题资源拿不到就退回次要文字色 —— 宁可没颜色，也不能让字看不见
            return Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var f) ? f as Brush : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// 更新日志页：显示每个版本改了什么。
///
/// 内容来源 = 更新仓库的 Release 说明（跟「检查更新」同一份数据，不另维护一份文案，
/// 否则两边迟早对不上）。仓库里没发过 Release、或者当前没网，就走兜底文案，
/// 并明确指向「设置 → 关于」里的本机版本记录，不让用户以为是空白页。
/// </summary>
public sealed partial class ChangelogPage : Page
{
    private readonly UpdateService _updater = UpdateService.CreateDefault();
    private bool _busy;

    /// <summary>仓库拉回来的**全部**发布（不过滤），筛选和左侧清单都在它上面做。</summary>
    private List<ReleaseRow> _all = new();

    /// <summary>当前筛出来的那批，跟右侧列表绑的是同一份顺序（左侧点第 n 项 → 滚到第 n 张卡）。</summary>
    private List<ReleaseRow> _rows = new();

    public ChangelogPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        CurrentVersionText.Text = ShellConfig.VersionPrefix + ShellConfig.ShellVersion;
        ChannelBadgeText.Text = UpdateChannels.ToDisplay(UpdateChannels.Parse(App.Settings.Current.UpdateChannel));

        var target = $"{ShellConfig.UpdateRepoOwner}/{ShellConfig.UpdateRepoName}";
        CurrentHint.Text = ShellConfig.IsInsider
            ? $"{target} 上的发布说明会显示在下面。当前是内测（Insider）构建，可能还没发到仓库 —— 那就先看已发布的版本。"
            : $"{target} 上的发布说明会显示在下面。";

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_busy) return;
        _busy = true;

        RefreshButton.IsEnabled = false;
        LoadingPanel.Visibility = Visibility.Visible;
        FallbackPanel.Visibility = Visibility.Collapsed;
        ReleaseList.ItemsSource = null;
        VersionList.ItemsSource = null;
        StateText.Text = "";

        try
        {
            if (!_updater.Source.IsConfigured)
            {
                ShowFallback("还没配置更新仓库地址，看不到发布说明。");
                return;
            }

            // ⚠️ 故意用 Insider 通道去拉（它是"两种都收"的permissive 那一档），
            //    这样「全部版本」在正式版通道的用户那儿也看得到完整历史 ——
            //    筛选交给上面的单选按钮，不再跟着用户的更新通道走（Nick 2026-09-26）。
            var releases = await _updater.GetHistoryAsync(UpdateChannel.Insider, 30);

            if (releases.Count == 0)
            {
                ShowFallback("更新仓库里还没有发布过版本。发布之后，每个版本的更新说明会出现在这里。");
                return;
            }

            _all = releases.Select(r => new ReleaseRow(r)).ToList();

            // 先算这句再 ApplyFilter —— 状态行里要带上它
            var hasCurrent = releases.Any(r =>
                string.Equals(r.Version, ShellConfig.ShellVersion, StringComparison.OrdinalIgnoreCase));
            _currentPublishedNote = hasCurrent
                ? ""
                : $" · 当前版本 {ShellConfig.VersionPrefix}{ShellConfig.ShellVersion} 还没发到仓库";

            ApplyFilter(scrollToTop: false);
        }
        catch (Exception ex)
        {
            ShowFallback($"读不到发布说明（{ex.GetType().Name}）。多半是没网，或者 GitHub 一时不通 —— 点「刷新」再试一次。");
        }
        finally
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = true;
            _busy = false;
        }
    }

    private string _currentPublishedNote = "";

    /// <summary>按顶部的「全部版本 / 正式版本」筛一遍，并把左侧版本清单一起刷新。</summary>
    private void ApplyFilter(bool scrollToTop)
    {
        var stableOnly = FilterStable.IsChecked == true;
        _rows = stableOnly ? _all.Where(r => !r.IsPrerelease).ToList() : new List<ReleaseRow>(_all);

        ReleaseList.ItemsSource = _rows;
        VersionList.ItemsSource = _rows;
        VersionList.SelectedIndex = _rows.Count > 0 ? 0 : -1;

        if (_rows.Count == 0)
        {
            ShowFallback(stableOnly
                ? "仓库里还没有正式版发布。切到「全部版本」可以看到内测（Insider）的发布记录。"
                : "更新仓库里还没有发布过版本。发布之后，每个版本的更新说明会出现在这里。");
        }
        else
        {
            FallbackPanel.Visibility = Visibility.Collapsed;
        }

        var stableCount = _all.Count(r => !r.IsPrerelease);
        StateText.Text = stableOnly
            ? $"共 {_rows.Count} 条正式版发布（仓库里一共 {_all.Count} 条，含 {_all.Count - stableCount} 条 Insider）{_currentPublishedNote}"
            : $"共 {_rows.Count} 条发布，其中 {stableCount} 条正式版、{_all.Count - stableCount} 条 Insider{_currentPublishedNote}";

        if (scrollToTop) ReleaseScroll.ChangeView(null, 0, null);
    }

    /// <summary>左侧点一个版本 → 右侧滚到那一版。</summary>
    private void VersionList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ReleaseRow row) return;

        var index = _rows.IndexOf(row);
        if (index < 0) return;
        VersionList.SelectedIndex = index;

        ScrollToRow(row);
    }

    /// <summary>
    /// 把右侧列表滚到某一版。ItemsControl 用 StackPanel（不虚拟化），卡片容器都在，
    /// 所以直接量"这张卡在滚动内容里的 Y"就是目标偏移量。
    /// </summary>
    private void ScrollToRow(ReleaseRow row)
    {
        var index = _rows.IndexOf(row);
        if (index < 0) return;

        try
        {
            if (ReleaseList.ContainerFromIndex(index) is FrameworkElement el
                && ReleaseScroll.Content is UIElement content)
            {
                var y = el.TransformToVisual(content).TransformPoint(new Point(0, 0)).Y;
                ReleaseScroll.ChangeView(null, Math.Max(0, y - 4), null);
            }
        }
        catch
        {
            // 量不到位置就只是不滚，别把页面带崩
        }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        // 数据还没回来时（XAML 里 FilterAll 的 IsChecked 也会触发一次 Checked）什么都不做
        if (_all.Count == 0) return;
        ApplyFilter(scrollToTop: true);
    }

    private void ShowFallback(string message)
    {
        FallbackText.Text = message + "\n本机跑过的版本记录在「设置 → 关于」里。";
        FallbackPanel.Visibility = Visibility.Visible;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private void OpenWeb_Click(object sender, RoutedEventArgs e)
        => App.MainWindow?.OpenExternal($"https://github.com/{ShellConfig.UpdateRepoOwner}/{ShellConfig.UpdateRepoName}/releases");

    // ══════════════════════════════════════════════════════════════
    //  发布说明是 Markdown。这里只做**轻量**处理，不做完整渲染：
    //  GitHub Release 正文基本就是「标题 + 一串 * 列表」，把这几样认出来、
    //  去掉标记符号，读起来就顺了。真上 Markdown 渲染器不划算。
    // ══════════════════════════════════════════════════════════════

    internal static IReadOnlyList<NoteLine> ParseNotes(string? markdown)
    {
        var lines = new List<NoteLine>();
        if (string.IsNullOrWhiteSpace(markdown)) return lines;

        var bold = Microsoft.UI.Text.FontWeights.SemiBold;
        var normal = Microsoft.UI.Text.FontWeights.Normal;

        foreach (var raw in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // Markdown 的 setext 标题下划线（一整行 --- 或 ===）：不是内容
            if (line.All(c => c is '-' or '=')) continue;

            // 图片行（![alt](url)）：应用里没必要显示
            if (line.StartsWith("![")) continue;

            if (line.StartsWith('#'))
            {
                var t = Inline(line.TrimStart('#').Trim());
                if (t.Length == 0) continue;
                lines.Add(new NoteLine(t, 15, bold, new Thickness(0, 8, 0, 2)));
                continue;
            }

            if (line.StartsWith("* ") || line.StartsWith("- ") || line.StartsWith("+ "))
            {
                var t = Inline(line[2..].Trim());
                if (t.Length == 0) continue;
                lines.Add(new NoteLine("· " + t, 13, normal, new Thickness(6, 0, 0, 0)));
                continue;
            }

            lines.Add(new NoteLine(Inline(line), 13, normal, new Thickness(0, 2, 0, 0)));
        }

        return lines;
    }

    /// <summary>去掉行内标记：[文字](链接)→文字，**粗体**→粗体，`代码`→代码，顺手删掉「（作者@xxx）」。</summary>
    private static string Inline(string s)
    {
        s = Regex.Replace(s, @"（作者\s*\[?@?[^）]*）", "");
        s = Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1");
        s = s.Replace("**", "").Replace("__", "").Replace("`", "");
        return s.Trim();
    }
}
