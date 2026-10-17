using System;
using System.Collections.Generic;
using System.ComponentModel;
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

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>发布说明里的一条（已经转过 Markdown，直接能往界面上摆）。</summary>
public sealed class NoteLine
{
    public NoteLine(string text, bool isBullet)
    {
        Text = text;
        IsBullet = isBullet;
    }

    public string Text { get; }

    public bool IsBullet { get; }

    /// <summary>有圆点才留圆点那一列的宽度，正文段落不留空档。</summary>
    public Visibility BulletVisibility => IsBullet ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>正文段落比条目淡一点，读起来有主次。</summary>
    public double Opacity => IsBullet ? 0.95 : 0.85;
}

/// <summary>
/// 发布说明里的一个**分组**：Markdown 的标题连同它下面的条目。
///
/// <para>
/// ⛔ 原来是一整条平铺的 <c>NoteLine</c> 列表，标题只比正文大一号 —— "前言/更新日志/其他内容"
/// 全糊在一起（Nick 点名）。分组之后组标题独立成行、组间留白，层级才立得住。
/// </para>
/// </summary>
public sealed class NoteBlock
{
    public NoteBlock(string title, IReadOnlyList<NoteLine> lines, Thickness margin)
    {
        Title = title;
        Lines = lines;
        Margin = margin;
    }

    public string Title { get; }

    public Visibility TitleVisibility => Title.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public IReadOnlyList<NoteLine> Lines { get; }

    /// <summary>组间距（第一组不留上边距）。</summary>
    public Thickness Margin { get; }
}

/// <summary>更新日志里的一条发布。</summary>
public sealed class ReleaseRow
{
    public ReleaseRow(UpdateRelease release)
    {
        Tag = release.Tag;
        ChannelText = release.Prerelease ? "Insider" : "正式版";
        ChannelShort = release.Prerelease ? "预览" : "正式";
        DateShort = release.PublishedAt is null
            ? ""
            : release.PublishedAt.Value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        DateText = release.PublishedAt is null
            ? ""
            : release.PublishedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        BadgeBrush = Lookup(release.Prerelease ? "SystemFillColorAttentionBrush" : "SystemFillColorSuccessBrush");
        IsPrerelease = release.Prerelease;
        Blocks = ChangelogPage.ParseBlocks(release.Notes);

        // 副标题：日期 · 通道 —— 版本号单独占主标题，不再三样挤一行
        SubLine = DateShort.Length == 0 ? ChannelShort : $"{DateShort} · {ChannelShort}";
    }

    public string Tag { get; }

    public string ChannelText { get; }

    /// <summary>左侧清单用的两字通道名（"预览" / "正式"）。</summary>
    public string ChannelShort { get; }

    /// <summary>左侧清单用的短日期（不带时间）。</summary>
    public string DateShort { get; }

    /// <summary>左侧清单的副标题："2026-10-17 · 预览"。</summary>
    public string SubLine { get; }

    public string DateText { get; }

    public Brush? BadgeBrush { get; }

    /// <summary>是不是预发布（「正式版本」筛选靠它）。</summary>
    public bool IsPrerelease { get; }

    public IReadOnlyList<NoteBlock> Blocks { get; }

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

    /// <summary>当前筛出来的那批，左侧清单与右侧正文绑的是同一份顺序。</summary>
    private List<ReleaseRow> _rows = new();

    /// <summary>左侧当前选中的那条（右侧正文滚到它）。</summary>
    private ReleaseRow? _selected;

    /// <summary>程序化同步 VersionList.SelectedItem 时抑制 SelectionChanged（防循环 + 防多余滚动）。</summary>
    private bool _syncingSelection;

    public ChangelogPage()
    {
        InitializeComponent();

        // 两侧留白 / 标题行上的滚轮也转给滚动区（见 Core/WheelRelay.cs）：
        // 指针压在哪一栏就滚哪一栏，压在别处一律给右侧的版本说明。
        Core.WheelRelay.Attach(this, ReleaseScroll, VersionList);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var channel = UpdateChannels.ToDisplay(UpdateChannels.Parse(App.Settings.Current.UpdateChannel));
        var repo = $"{ShellConfig.UpdateRepoOwner}/{ShellConfig.UpdateRepoName}";

        // 原来这里是一张占掉 15% 屏高的大卡，只写版本号和两句说明 —— 压成一行副标题
        HeaderMeta.Text = $"当前版本 {ShellConfig.VersionPrefix}{ShellConfig.ShellVersion}　·　{channel}"
                          + $"　·　发布说明读取自 {repo}"
                          + (ShellConfig.IsInsider ? "。当前为内测构建，可能尚未发布至仓库。" : "。");

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_busy) return;
        _busy = true;

        LoadingPanel.Visibility = Visibility.Visible;
        FallbackPanel.Visibility = Visibility.Collapsed;
        ReleaseList.ItemsSource = null;
        VersionList.ItemsSource = null;
        StateText.Text = "";
        _selected = null;
        _syncingSelection = true;
        try { VersionList.SelectedItem = null; }
        finally { _syncingSelection = false; }

        try
        {
            if (!_updater.Source.IsConfigured)
            {
                ShowFallback("尚未配置更新仓库地址，无法显示发布说明。");
                return;
            }

            // ⚠️ 故意用 Insider 通道去拉（它是"两种都收"的 permissive 那一档），
            //    这样「全部版本」在正式版通道的用户那儿也看得到完整历史 ——
            //    筛选交给下拉，不再跟着用户的更新通道走（Nick 2026-09-26）。
            var releases = await _updater.GetHistoryAsync(UpdateChannel.Insider, 30);

            if (releases.Count == 0)
            {
                ShowFallback("更新仓库尚未发布任何版本。发布后，各版本的更新说明将显示在此处。");
                return;
            }

            _all = releases.Select(r => new ReleaseRow(r)).ToList();

            var hasCurrent = releases.Any(r =>
                string.Equals(r.Version, ShellConfig.ShellVersion, StringComparison.OrdinalIgnoreCase));
            _currentPublishedNote = hasCurrent
                ? ""
                : $" · 当前版本 {ShellConfig.VersionPrefix}{ShellConfig.ShellVersion} 尚未发布至仓库";

            ApplyFilter(scrollToTop: false);
        }
        catch (Exception ex)
        {
            ShowFallback($"无法读取发布说明（{ex.GetType().Name}）。可能为无网络连接，或 GitHub 暂时不可访问：请单击「刷新」重试。");
        }
        finally
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            _busy = false;
        }
    }

    private string _currentPublishedNote = "";

    /// <summary>按顶部下拉的「全部版本 / 仅正式版本」筛一遍，并把左侧版本清单一起刷新。</summary>
    private void ApplyFilter(bool scrollToTop)
    {
        var stableOnly = FilterBox.SelectedIndex == 1;
        _rows = stableOnly ? _all.Where(r => !r.IsPrerelease).ToList() : new List<ReleaseRow>(_all);

        ReleaseList.ItemsSource = _rows;
        VersionList.ItemsSource = _rows;

        if (_rows.Count == 0)
        {
            ShowFallback(stableOnly
                ? "仓库中尚无正式版发布。切换至「全部版本」可查看内测（Insider）的发布记录。"
                : "更新仓库尚未发布任何版本。发布后，各版本的更新说明将显示在此处。");
            return;
        }

        FallbackPanel.Visibility = Visibility.Collapsed;

        var stableCount = _all.Count(r => !r.IsPrerelease);
        StateText.Text = stableOnly
            ? $"共 {_rows.Count} 条正式版{_currentPublishedNote}"
            : $"共 {_rows.Count} 条 · {stableCount} 条正式版{_currentPublishedNote}";

        SelectRow(_rows[0], scroll: false);

        if (scrollToTop) ReleaseScroll.ChangeView(null, 0, null);
    }

    /// <summary>选中某一条：同步原生列表高亮 + 右侧滚到它。</summary>
    private void SelectRow(ReleaseRow row, bool scroll)
    {
        _selected = row;

        // 原生 ListView 的高亮由它自己管；这里只负责把选中项对齐（程序化选中时不重复触发滚动）
        if (!ReferenceEquals(VersionList.SelectedItem, row))
        {
            _syncingSelection = true;
            try { VersionList.SelectedItem = row; }
            finally { _syncingSelection = false; }
        }

        if (scroll) ScrollToRow(row);
    }

    private void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        if (VersionList.SelectedItem is not ReleaseRow row) return;

        SelectRow(row, scroll: true);
    }

    /// <summary>
    /// 把右侧正文滚到某一版。ItemsControl 用 StackPanel（不虚拟化），卡片容器都在，
    /// 所以直接量「这一版在滚动内容里的 Y」就是目标偏移量。
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
                var y = el.TransformToVisual(content).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                ReleaseScroll.ChangeView(null, Math.Max(0, y - 4), null);
            }
        }
        catch (Exception ex)
        {
            // 量不到位置就只是不滚，别把页面带崩
            AppLog.Exception("changelog", ex, "滚动到指定版本");
        }
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        // 数据还没回来时（XAML 初始选中项也会触发一次）什么都不做
        if (_all.Count == 0) return;
        ApplyFilter(scrollToTop: true);
    }

    private void ShowFallback(string message)
    {
        FallbackText.Text = message + "\n本机历史版本记录位于「设置 → 关于」中。";
        FallbackPanel.Visibility = Visibility.Visible;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private void OpenWeb_Click(object sender, RoutedEventArgs e)
        => App.MainWindow?.OpenExternal($"https://github.com/{ShellConfig.UpdateRepoOwner}/{ShellConfig.UpdateRepoName}/releases");

    // ══════════════════════════════════════════════════════════════
    //  发布说明是 Markdown。这里只做**轻量**处理，不做完整渲染：
    //  GitHub Release 正文基本就是「若干标题 + 一串 * 列表」，把标题当分组边界、
    //  认出列表项、去掉行内标记，读起来就顺了。真上 Markdown 渲染器不划算。
    // ══════════════════════════════════════════════════════════════

    internal static IReadOnlyList<NoteBlock> ParseBlocks(string? markdown)
    {
        var blocks = new List<NoteBlock>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;

        var lines = new List<NoteLine>();
        var title = "";

        void Flush()
        {
            if (lines.Count == 0 && title.Length == 0) return;

            // 组间距：第一组贴顶，后面每组留 16
            blocks.Add(new NoteBlock(title, lines.ToList(), blocks.Count == 0
                ? new Thickness(0)
                : new Thickness(0, 16, 0, 0)));

            lines.Clear();
            title = "";
        }

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
                var heading = Inline(line.TrimStart('#').Trim());
                if (heading.Length == 0) continue;

                Flush();          // 上一组到此为止，新的标题开启下一组
                title = heading;
                continue;
            }

            if (line.StartsWith("* ") || line.StartsWith("- ") || line.StartsWith("+ "))
            {
                var text = Inline(line[2..].Trim());
                if (text.Length > 0) lines.Add(new NoteLine(text, isBullet: true));
                continue;
            }

            lines.Add(new NoteLine(Inline(line), isBullet: false));
        }

        Flush();
        return blocks;
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
