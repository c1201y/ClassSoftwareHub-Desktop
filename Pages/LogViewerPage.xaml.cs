using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using ClassSoftwareHub.Desktop.Controls;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace ClassSoftwareHub.Desktop.Pages;























public sealed partial class LogViewerPage : Page
{
    
    private const int FollowMs = 2000;

    
    private const double NearBottomPx = 40;

    private readonly LevelChipBar _chips;
    private DispatcherQueueTimer? _followTimer;
    private bool _suppress;
    private List<LogEntry> _all = new();

    
    
    
    
    private readonly System.Collections.ObjectModel.ObservableCollection<LogEntry> _view = new();

    
    
    
    
    
    
    private long _loadedSize = -1;

    private ScrollViewer? _listScroll;
    
    
    private bool _atBottom;

    public LogViewerPage()
    {
        InitializeComponent();

        
        Core.WheelRelay.Attach(this, LogList);

        LogList.ItemsSource = _view;

        _chips = new LevelChipBar(ChipHost);
        _chips.SelectionChanged += ApplyFilter;

        Loaded += (_, _) =>
        {
            if (SessionBox.ItemsSource is null) ReloadSessions();
            StartFollow();
        };
    }

    

    
    private sealed class LogSession
    {
        public string Name { get; init; } = "";
        public string FullPath { get; init; } = "";
        public DateTime Started { get; init; }
        public long Bytes { get; init; }
        public int Files { get; init; }
        public bool IsCurrent { get; init; }
        public bool IsLegacy { get; init; }

        
        
        
        
        
        public string Display =>
            IsCurrent ? $"本次运行 · {Started:HH:mm:ss}"
            : IsLegacy ? "升级前的历史日志"
            : $"{Started:MM-dd HH:mm:ss} · {Files} 个文件";

        public override string ToString() => Display;
    }

    private sealed class LogFile
    {
        public string Path { get; init; } = "";
        public string Name { get; init; } = "";
        public long Size { get; init; }
        public DateTime Modified { get; init; }

        
        
        
        
        public string Display => $"{Name}　{LogReader.Describe(Name).Short}";
        public override string ToString() => Display;
    }

    
    private static List<LogFile> ScanFiles(string dir)
    {
        var list = new List<LogFile>();
        try
        {
            if (!Directory.Exists(dir)) return list;

            foreach (var path in Directory.GetFiles(dir, "*.log"))
            {
                try
                {
                    var info = new FileInfo(path);
                    list.Add(new LogFile
                    {
                        Path = path,
                        Name = info.Name,
                        Size = info.Length,
                        Modified = info.LastWriteTime,
                    });
                }
                catch {  }
            }
        }
        catch { }

        
        return list
            .OrderByDescending(f => f.Name.Equals("app.log", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(f => f.Modified)
            .ToList();
    }

    
    private void ReloadSessions()
    {
        var wanted = (SessionBox.SelectedItem as LogSession)?.Name;

        List<LogSession> items;
        try
        {
            items = LogHome.Scan()
                .Select(s => new LogSession
                {
                    Name = s.Name,
                    FullPath = s.FullPath,
                    Started = s.StartedLocal,
                    Bytes = s.Bytes,
                    Files = s.FileCount,
                    IsCurrent = s.IsCurrent,
                    IsLegacy = s.IsLegacy,
                })
                .ToList();
        }
        catch
        {
            items = new List<LogSession>();
        }

        _suppress = true;
        SessionBox.ItemsSource = items;
        SessionBox.SelectedItem = (wanted is null ? null : items.FirstOrDefault(s => s.Name == wanted))
                                  ?? items.FirstOrDefault();
        _suppress = false;

        ReloadFiles(keepSelection: false);
    }

    
    private void ReloadFiles(bool keepSelection)
    {
        var wanted = keepSelection ? (FileBox.SelectedItem as LogFile)?.Name : null;

        var items = SessionBox.SelectedItem is LogSession s
            ? ScanFiles(s.FullPath)
            : new List<LogFile>();

        _suppress = true;
        FileBox.ItemsSource = items;
        FileBox.SelectedItem = (wanted is null ? null : items.FirstOrDefault(f => f.Name == wanted))
                               ?? items.FirstOrDefault();
        _suppress = false;

        
        ToolTipService.SetToolTip(FileBox, FileBox.SelectedItem is LogFile sel
            ? $"{sel.Name}：{LogReader.Describe(sel.Name).Long}"
            : "");

        LoadCurrent();
    }

    private void LoadCurrent()
    {
        if (FileBox.SelectedItem is not LogFile f)
        {
            _all = new List<LogEntry>();
            _loadedSize = -1;
            ApplyFilter();

            StatusText.Text = SessionBox.SelectedItem is null
                ? "本机还没有产生任何日志文件。"
                : "这个会话里没有可读的日志文件。";
            StatusText.ClearValue(ToolTipService.ToolTipProperty);
            return;
        }

        
        
        long size = 0;
        try { size = new FileInfo(f.Path).Length; } catch { }

        var (text, _) = LogReader.ReadTail(f.Path, LogReader.ListMaxBytes, LogReader.ListMaxLines);
        _loadedSize = size;

        
        _all = LogReader.Parse(text, Path.GetFileNameWithoutExtension(f.Name));
        ApplyFilter();

        
        _ = ListScroll;

        
        
        
    }

    
    
    
    
    
    private string BuildStatus(LogFile f, long size, DateTime modified, bool truncated)
    {
        var stats = LogReader.Compute(_all, _view.Count);
        var sb = new StringBuilder();

        var where = SessionBox.SelectedItem is LogSession s
            ? s.IsCurrent ? "本次运行" : s.IsLegacy ? "升级前的历史日志" : s.Name
            : "";
        if (where.Length > 0) sb.Append(where).Append(" · ");
        sb.Append(f.Name).Append(" · ").Append(LogReader.SizeText(size)).Append(" · ");

        sb.Append(stats.Total.ToString("N0")).Append(" 行");
        if (stats.Filtered != stats.Total) sb.Append("（当前显示 ").Append(stats.Filtered.ToString("N0")).Append(" 行）");
        if (_chips.AllOff) sb.Append("　未勾选任何级别");

        sb.Append(" · ").Append(stats.ModuleCount).Append(" 个模块");
        if (stats.TopModule.Length > 0)
            sb.Append("（最多 ").Append(stats.TopModule).Append(' ').Append(stats.TopModuleCount.ToString("N0")).Append(" 条）");

        var bad = new List<string>();
        if (stats.Error > 0) bad.Add($"错误 {stats.Error}");
        if (stats.Warning > 0) bad.Add($"警告 {stats.Warning}");
        if (bad.Count > 0) sb.Append(" · ").Append(string.Join("　", bad)).Append(" 条");

        if (stats.MultiLineCount > 0) sb.Append(" · 含堆栈 ").Append(stats.MultiLineCount).Append(" 条");

        if (stats.FirstStamp.Length > 0 && stats.LastStamp.Length > 0)
            sb.Append(" · 时间跨度 ").Append(stats.FirstStamp).Append(" → ").Append(stats.LastStamp);

        sb.Append(" · 最后写入 ").Append(modified.ToString("HH:mm:ss"));
        if (truncated)
            sb.Append(" · 已截断：只读末尾 ").Append(LogReader.ListMaxLines).Append(" 行，完整内容请用「详细视图」");

        return sb.ToString();
    }

    
    private string BuildStatusTooltip(LogFile f, long size, DateTime modified, bool truncated)
    {
        var stats = LogReader.Compute(_all, _view.Count);
        var sb = new StringBuilder();

        sb.AppendLine(f.Path);
        sb.AppendLine($"级别：调试 {stats.Debug:N0} · 信息 {stats.Info:N0} · 警告 {stats.Warning:N0} · 错误 {stats.Error:N0}");
        sb.AppendLine($"文件：{LogReader.SizeText(size)} · 最后写入 {modified:yyyy-MM-dd HH:mm:ss}");
        if (truncated) sb.AppendLine($"⚠ 只读了末尾 {LogReader.ListMaxLines:N0} 行（完整内容请在「详细视图」里看）");

        if (stats.Modules.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("模块分布：");
            foreach (var (module, count) in stats.Modules)
                sb.AppendLine($"    {module}　{count:N0}");
        }

        return sb.ToString().TrimEnd();
    }

    

    private void ApplyFilter()
    {
        
        var list = LogReader.Filter(_all, _chips.FilterLevels, FilterBox.Text ?? "");

        
        
        _view.Clear();
        foreach (var e in list) _view.Add(e);

        RefreshStats(FileBox.SelectedItem as LogFile);
    }

    
    
    
    
    private void RefreshStats(LogFile? f)
    {
        _chips.SetCounts(LogReader.Compute(_all, _view.Count));
        UpdateEmptyHint();

        if (f is null) return;

        long size = f.Size;
        var modified = f.Modified;
        try
        {
            var info = new FileInfo(f.Path);
            size = info.Length;
            modified = info.LastWriteTime;
        }
        catch { }

        var truncated = _all.Count >= LogReader.ListMaxLines;
        StatusText.Text = BuildStatus(f, size, modified, truncated);
        StatusText.SetValue(ToolTipService.ToolTipProperty, BuildStatusTooltip(f, size, modified, truncated));
    }

    
    
    
    
    private void UpdateEmptyHint()
    {
        if (EmptyHint is null) return;

        var kw = (FilterBox.Text ?? "").Trim();

        string? hint =
            _all.Count == 0 ? "这个文件里没有可显示的内容。"
            : _chips.AllOff ? "没有勾选任何级别 —— 点上面的「调试 / 信息 / 警告 / 错误」，把要看的勾回来。"
            : _view.Count == 0 ? $"没有匹配「{kw}」的日志。"
            : null;

        EmptyHint.Text = hint ?? "";
        EmptyHint.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ScrollToEnd()
    {
        if (_view.Count == 0) return;
        try
        {
            LogList.UpdateLayout();
            LogList.ScrollIntoView(_view[^1]);
        }
        catch { }
    }

    
    private ScrollViewer? ListScroll
    {
        get
        {
            if (_listScroll is not null) return _listScroll;
            _listScroll = FindDescendantScrollViewer(LogList);
            if (_listScroll is not null)
            {
                
                
                
                _listScroll.ViewChanged += (_, e) =>
                {
                    if (e.IsIntermediate) return;
                    _atBottom = _listScroll.ScrollableHeight - _listScroll.VerticalOffset < NearBottomPx;
                };
            }
            return _listScroll;
        }
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject? node)
    {
        while (node is not null)
        {
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is ScrollViewer sv) return sv;
                var deep = FindDescendantScrollViewer(child);
                if (deep is not null) return deep;
            }
            return null;
        }
        return null;
    }

    

    private void LogList_ContainerContentChanging(object sender, ContainerContentChangingEventArgs args)
    {
        if (args.Phase != 0 || args.Item is not LogEntry entry) return;

        if (args.ItemContainer.ContentTemplateRoot is Grid root)
        {
            var brush = LogReader.BrushOf(entry.Level);
            if (root.FindName("LevelIcon") is FontIcon icon) icon.Foreground = brush;
            if (root.FindName("LevelLabel") is TextBlock text) text.Foreground = brush;
        }
    }

    

    
    
    
    
    
    private void FilesHelp_Click(object sender, RoutedEventArgs e)
    {
        var host = new StackPanel { Spacing = 12 };
        var session = SessionBox.SelectedItem as LogSession;

        host.Children.Add(new TextBlock
        {
            Text = session switch
            {
                { IsCurrent: true } => "日志按「启动一次 = 一个文件夹」保存。下面这些是本次运行的文件。",
                { IsLegacy: true } => "这是升级之前那批「一个模块一个文件」的旧日志，原样归档在这儿。",
                null => "本机还没有产生任何日志文件。",
                _ => $"这是 {session.Started:yyyy-MM-dd HH:mm:ss} 那次运行留下的文件。",
            },
            TextWrapping = TextWrapping.Wrap,
            Foreground = LogReader.BrushOf(LogLevel.Info),
        });

        var items = (FileBox.ItemsSource as IEnumerable<LogFile>)?.ToList() ?? new List<LogFile>();
        foreach (var f in items)
        {
            var d = LogReader.Describe(f.Name);
            host.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{f.Name}　{d.Short}",
                        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                    },
                    new TextBlock
                    {
                        Text = d.Long,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = LogReader.BrushOf(LogLevel.Info),
                    },
                },
            });
        }

        host.Children.Add(new TextBlock
        {
            Text = "每个文件夹是一次运行，可在「设置 → 存储 → 本地日志」里按天保留或单独删除。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = LogReader.BrushOf(LogLevel.Info),
        });

        var dialog = new ContentDialog
        {
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
            XamlRoot = XamlRoot,
            Title = "这些日志文件是干什么的",
            Content = new ScrollViewer
            {
                MaxHeight = 460,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = host,
            },
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
        };

        _ = dialog.ShowAsync();
    }

    private void Say(string message) => StatusText.Text = message;

    

    private void SessionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        ReloadFiles(keepSelection: false);
    }

    private void FileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        LoadCurrent();
    }

    
    
    
    
    private void FilterBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) ApplyFilter();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        
        ReloadSessions();
    }

    
    private void Detail_Click(object sender, RoutedEventArgs e)
    {
        Views.LogDetailWindow.Show(
            (SessionBox.SelectedItem as LogSession)?.Name,
            (FileBox.SelectedItem as LogFile)?.Name);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            
            var dir = AppLog.Root;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true,
            });
        }
        catch { }
    }

    
    
    
    
    private void LogList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.C) return;

        var ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        if ((ctrl & Windows.UI.Core.CoreVirtualKeyStates.Down) == 0) return;

        var picked = LogList.SelectedItems.OfType<LogEntry>().ToList();
        if (picked.Count == 0) return;

        CopyEntries(picked, $"已复制选中的 {picked.Count} 行到剪贴板。");
        e.Handled = true;
    }

    private void CopySelected_Click(object sender, RoutedEventArgs e)
    {
        var picked = LogList.SelectedItems.OfType<LogEntry>().ToList();
        if (picked.Count == 0) { Say("先在表格里选中要复制的行。"); return; }
        CopyEntries(picked, $"已复制选中的 {picked.Count} 行到剪贴板。");
    }

    private void CopyFiltered_Click(object sender, RoutedEventArgs e)
    {
        if (_view.Count == 0) { Say("当前筛选下没有内容可复制。"); return; }
        CopyEntries(_view, $"已复制筛选结果 {_view.Count} 行到剪贴板。");
    }

    private void CopyEntries(IReadOnlyList<LogEntry> entries, string done)
    {
        try
        {
            var dp = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            dp.SetText(LogReader.ToText(entries));
            Clipboard.SetContent(dp);
            Clipboard.Flush();      
            Say(done);
        }
        catch (Exception ex)
        {
            Say("复制失败：" + ex.Message);
        }
    }

    
    
    
    
    
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var root = AppLog.Root;
            if (!Directory.Exists(root)) { Say("没有可导出的日志。"); return; }

            var sessions = LogHome.Scan();
            if (sessions.Count == 0) { Say("没有可导出的日志。"); return; }

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop)) desktop = root;

            var zip = Path.Combine(desktop, $"ClassSoftwareHub-日志-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            if (File.Exists(zip)) File.Delete(zip);

            var count = 0;
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                foreach (var s in sessions)
                {
                    foreach (var file in ScanFiles(s.FullPath))
                    {
                        try
                        {
                            using var src = new FileStream(file.Path, FileMode.Open, FileAccess.Read,
                                                           FileShare.ReadWrite | FileShare.Delete);
                            var entry = archive.CreateEntry($"{s.Name}/{file.Name}", CompressionLevel.Optimal);
                            using var dst = entry.Open();
                            src.CopyTo(dst);
                            count++;
                        }
                        catch {  }
                    }
                }
            }

            Say($"已导出 {sessions.Count} 次运行的 {count} 个日志文件到：{zip}");
        }
        catch (Exception ex)
        {
            Say("导出失败：" + ex.Message);
        }
    }

    

    private void StartFollow()
    {
        StopFollow();

        var queue = DispatcherQueue;
        if (queue is null) return;

        _followTimer = queue.CreateTimer();
        _followTimer.Interval = TimeSpan.FromMilliseconds(FollowMs);
        _followTimer.Tick += (_, _) => FollowTick();
        _followTimer.Start();
    }

    private void StopFollow()
    {
        try { _followTimer?.Stop(); } catch { }
        _followTimer = null;
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    private void FollowTick()
    {
        try
        {
            
            
            
            
            
            
            if (ListScroll is { ViewportHeight: > 0 } sv)
            {
                _atBottom = (sv.ExtentHeight > 0 && sv.ExtentHeight <= sv.ViewportHeight + 1)
                            || sv.ScrollableHeight - sv.VerticalOffset < NearBottomPx;
            }

            if (_suppress) return;
            if (!_atBottom) return;
            if (SessionBox.SelectedItem is not LogSession { IsCurrent: true }) return;
            if (FileBox.SelectedItem is not LogFile cur) return;

            long size = 0;
            try { size = new FileInfo(cur.Path).Length; } catch { return; }

            if (size == _loadedSize) return;   

            
            if (size < _loadedSize || !AppendNew(cur, size)) LoadCurrent();

            ScrollToEnd();
        }
        catch {  }
    }

    
    
    
    
    private bool AppendNew(LogFile f, long size)
    {
        var (text, truncated) = LogReader.ReadTail(f.Path, LogReader.ListMaxBytes, LogReader.ListMaxLines);
        if (truncated) return false;

        
        var fresh = LogReader.Parse(text, Path.GetFileNameWithoutExtension(f.Name), _all.Count + 1);
        if (!LogReader.IsExtensionOf(_all, fresh)) return false;

        var levels = _chips.FilterLevels;
        var kw = FilterBox.Text ?? "";

        for (var i = _all.Count; i < fresh.Count; i++)
        {
            var e = fresh[i];
            _all.Add(e);
            
            if (LogReader.Filter(new[] { e }, levels, kw).Count > 0) _view.Add(e);
        }

        _loadedSize = size;
        RefreshStats(f);
        return true;
    }

    
    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e) => StopFollow();
}
