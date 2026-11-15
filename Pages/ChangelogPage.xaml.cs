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


public sealed class NoteLine
{
    public NoteLine(string text, bool isBullet)
    {
        Text = text;
        IsBullet = isBullet;
    }

    public string Text { get; }

    public bool IsBullet { get; }

    
    public Visibility BulletVisibility => IsBullet ? Visibility.Visible : Visibility.Collapsed;

    
    public double Opacity => IsBullet ? 0.95 : 0.85;
}









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

    
    public Thickness Margin { get; }
}


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

        
        SubLine = DateShort.Length == 0 ? ChannelShort : $"{DateShort} · {ChannelShort}";
    }

    public string Tag { get; }

    public string ChannelText { get; }

    
    public string ChannelShort { get; }

    
    public string DateShort { get; }

    
    public string SubLine { get; }

    public string DateText { get; }

    public Brush? BadgeBrush { get; }

    
    public bool IsPrerelease { get; }

    public IReadOnlyList<NoteBlock> Blocks { get; }

    private static Brush? Lookup(string key)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b) return b;
            
            return Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var f) ? f as Brush : null;
        }
        catch
        {
            return null;
        }
    }
}








public sealed partial class ChangelogPage : Page
{
    private readonly UpdateService _updater = UpdateService.CreateDefault();
    private bool _busy;

    
    private List<ReleaseRow> _all = new();

    
    private List<ReleaseRow> _rows = new();

    
    private ReleaseRow? _selected;

    
    private bool _syncingSelection;

    public ChangelogPage()
    {
        InitializeComponent();

        
        
        Core.WheelRelay.Attach(this, ReleaseScroll, VersionList);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var channel = UpdateChannels.ToDisplay(UpdateChannels.Parse(App.Settings.Current.UpdateChannel));
        var repo = $"{ShellConfig.UpdateRepoOwner}/{ShellConfig.UpdateRepoName}";

        
        HeaderMeta.Text = $"当前版本 {ShellConfig.VersionPrefix}{ShellConfig.DisplayVersion}　·　{channel}"
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
                : $" · 当前版本 {ShellConfig.VersionPrefix}{ShellConfig.DisplayVersion} 尚未发布至仓库";

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

    
    private void SelectRow(ReleaseRow row, bool scroll)
    {
        _selected = row;

        
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
            
            AppLog.Exception("changelog", ex, "滚动到指定版本");
        }
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        
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

    
    
    
    
    

    internal static IReadOnlyList<NoteBlock> ParseBlocks(string? markdown)
    {
        var blocks = new List<NoteBlock>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;

        var lines = new List<NoteLine>();
        var title = "";

        void Flush()
        {
            if (lines.Count == 0 && title.Length == 0) return;

            
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

            
            if (line.All(c => c is '-' or '=')) continue;

            
            if (line.StartsWith("![")) continue;

            if (line.StartsWith('#'))
            {
                var heading = Inline(line.TrimStart('#').Trim());
                if (heading.Length == 0) continue;

                Flush();          
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

    
    private static string Inline(string s)
    {
        s = Regex.Replace(s, @"（作者\s*\[?@?[^）]*）", "");
        s = Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1");
        s = s.Replace("**", "").Replace("__", "").Replace("`", "");
        return s.Trim();
    }
}
