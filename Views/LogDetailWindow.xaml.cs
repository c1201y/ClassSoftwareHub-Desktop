using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using ClassSoftwareHub.Desktop.Controls;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;



















public sealed partial class LogDetailWindow : Window
{
    private const int DefaultWidthDip = 1280;
    private const int DefaultHeightDip = 800;
    private const int FollowMs = 2000;
    private const double NearBottomPx = 40;

    private static LogDetailWindow? _instance;

    private readonly LevelChipBar _chips;

    private AppWindow? _appWindow;
    private DispatcherQueueTimer? _followTimer;
    private ScrollViewer? _listScroll;

    private bool _suppress;
    
    private bool _atBottom;
    private List<LogEntry> _all = new();

    
    
    private readonly System.Collections.ObjectModel.ObservableCollection<LogEntry> _view = new();

    
    
    private long _loadedSize = -1;

    
    private string? _wantSession;
    private string? _wantFile;

    private LogDetailWindow()
    {
        InitializeComponent();
        Configure();

        LogList.ItemsSource = _view;

        _chips = new LevelChipBar(ChipHost);
        _chips.SelectionChanged += ApplyFilter;

        Root.Loaded += (_, _) =>
        {
            
            if (SessionBox.ItemsSource is null) ReloadSessions();
            StartFollow();
        };
    }

    

    
    public static void Show(string? sessionName, string? fileName)
    {
        try
        {
            _instance ??= new LogDetailWindow();
            _instance._wantSession = sessionName;
            _instance._wantFile = fileName;
            _instance.ReloadSessions();
            _instance.BringUp();
        }
        catch (Exception ex)
        {
            AppLog.Warning("logs", "打开日志详细视图失败：" + ex.Message);
        }
    }

    
    
    
    
    public static void CloseForExit()
    {
        var w = _instance;
        _instance = null;
        try { w?.Close(); } catch { }
        AppLog.Info("exit", "日志详细视图实例已请求 Close");
    }

    

    private void Configure()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "日志 · 详细视图";

            if (_appWindow.Presenter is OverlappedPresenter p)
            {
                p.SetBorderAndTitleBar(true, true);   
                p.IsResizable = true;                 
                p.IsMaximizable = true;
                p.IsMinimizable = true;
            }

            ConfigureTitleBar();
            
            BackdropHost.Apply(this, Root, App.Settings.Current.Backdrop);
            ThemeHost.Apply(Root);

            ApplySize();
            CenterOnScreen();

            Root.ActualThemeChanged += (_, _) => UpdateCaptionButtonColors();

            
            Root.AddHandler(UIElement.KeyDownEvent,
                new KeyEventHandler((_, e) =>
                {
                    if (e.Key == Windows.System.VirtualKey.Escape) Close();
                }), true);

            
            Closed += (_, _) => { _instance = null; StopFollow(); };
        }
        catch (Exception ex)
        {
            AppLog.Warning("logs", "日志详细视图初始化失败：" + ex.Message);
        }
    }

    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        if (_appWindow is null) return;
        try
        {
            var bar = _appWindow.TitleBar;
            bar.ExtendsContentIntoTitleBar = true;
            bar.PreferredHeightOption = TitleBarHeightOption.Standard;
            bar.ButtonBackgroundColor = Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }
        catch (Exception ex)
        {
            AppLog.Warn("logs", "日志详细视图标题栏定制不可用：" + ex.Message);
        }

        UpdateCaptionButtonColors();

        
        WindowChrome.SetRounded(WindowNative.GetWindowHandle(this), rounded: true);
    }

    private void UpdateCaptionButtonColors()
    {
        if (_appWindow is null) return;
        var dark = Root.ActualTheme == ElementTheme.Dark;
        try
        {
            var bar = _appWindow.TitleBar;
            bar.ButtonForegroundColor = dark ? Colors.White : Windows.UI.Color.FromArgb(255, 30, 30, 30);
            bar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
            bar.ButtonHoverBackgroundColor = dark
                ? Windows.UI.Color.FromArgb(26, 255, 255, 255)
                : Windows.UI.Color.FromArgb(20, 0, 0, 0);
            bar.ButtonPressedForegroundColor = dark
                ? Windows.UI.Color.FromArgb(255, 200, 200, 200)
                : Windows.UI.Color.FromArgb(255, 90, 90, 90);
            bar.ButtonInactiveForegroundColor = dark
                ? Windows.UI.Color.FromArgb(160, 255, 255, 255)
                : Windows.UI.Color.FromArgb(140, 0, 0, 0);
            bar.ButtonPressedBackgroundColor = dark
                ? Windows.UI.Color.FromArgb(40, 255, 255, 255)
                : Windows.UI.Color.FromArgb(30, 0, 0, 0);
        }
        catch { }
    }

    
    private void ApplySize()
    {
        if (_appWindow is null) return;
        try
        {
            var scale = Scale();
            var work = DisplayArea.Primary.WorkArea;

            var maxW = work.Width / scale - 40;
            var maxH = work.Height / scale - 60;

            var w = (int)Math.Round(Math.Clamp(DefaultWidthDip, 760, Math.Max(760, maxW)));
            var h = (int)Math.Round(Math.Clamp(DefaultHeightDip, 460, Math.Max(460, maxH)));

            _appWindow.ResizeClient(new SizeInt32(
                (int)Math.Round(w * scale), (int)Math.Round(h * scale)));
        }
        catch (Exception ex)
        {
            AppLog.Warn("logs", "日志详细视图尺寸设置失败：" + ex.Message);
        }
    }

    private void CenterOnScreen()
    {
        if (_appWindow is null) return;
        try
        {
            var work = DisplayArea.Primary.WorkArea;
            var size = _appWindow.Size;
            _appWindow.Move(new PointInt32(
                work.X + Math.Max(0, (work.Width - size.Width) / 2),
                work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
        }
        catch (Exception ex)
        {
            AppLog.Warn("logs", "日志详细视图定位失败：" + ex.Message);
        }
    }

    private double Scale()
    {
        try
        {
            var dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch { return 1.0; }
    }

    private void BringUp()
    {
        try
        {
            Activate();
            var hwnd = WindowNative.GetWindowHandle(this);
            ShowWindow(hwnd, SW_SHOW);
            SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            AppLog.Warning("logs", "日志详细视图激活失败：" + ex.Message);
        }
    }

    

    private sealed class LogSession
    {
        public string Name { get; init; } = "";
        public string FullPath { get; init; } = "";
        public DateTime Started { get; init; }
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
                    list.Add(new LogFile { Path = path, Name = info.Name, Size = info.Length, Modified = info.LastWriteTime });
                }
                catch { }
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
        var wanted = _wantSession ?? (SessionBox.SelectedItem as LogSession)?.Name;
        _wantSession = null;

        List<LogSession> items;
        try
        {
            items = LogHome.Scan()
                .Select(s => new LogSession
                {
                    Name = s.Name,
                    FullPath = s.FullPath,
                    Started = s.StartedLocal,
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
        var wanted = _wantFile ?? (keepSelection ? (FileBox.SelectedItem as LogFile)?.Name : null);
        _wantFile = null;

        var items = SessionBox.SelectedItem is LogSession s
            ? ScanFiles(s.FullPath)
            : new List<LogFile>();

        _suppress = true;
        FileBox.ItemsSource = items;
        FileBox.SelectedItem = (wanted is null ? null : items.FirstOrDefault(f => f.Name == wanted))
                               ?? items.FirstOrDefault();
        _suppress = false;

        LoadCurrent();
    }

    private void LoadCurrent()
    {
        if (FileBox.SelectedItem is not LogFile f)
        {
            _all = new List<LogEntry>();
            _loadedSize = -1;
            ApplyFilter();
            StatusText.Text = "没有可读的日志文件。";
            ShowRaw(null);
            return;
        }

        
        long size = 0;
        try { size = new FileInfo(f.Path).Length; } catch { }

        var (text, truncated) = LogReader.ReadTail(f.Path, LogReader.DetailMaxBytes, LogReader.DetailMaxLines);
        _loadedSize = size;

        _all = LogReader.Parse(text, Path.GetFileNameWithoutExtension(f.Name));
        ApplyFilter();

        
        _ = ListScroll;

        StatusText.Text = $"{f.Path}\n{LogReader.SizeText(size)} · " +
                          (truncated
                              ? $"已截断：只读了末尾 {LogReader.DetailMaxLines:N0} 行"
                              : "已完整载入")
                          + " · 关闭窗口即可，不会修改任何文件";
        
        
    }

    
    
    
    
    
    
    
    
    
    
    private void BuildMetrics(LogStats stats)
    {
        MetricBar.Inlines.Clear();

        var dim = LogReader.Look("TextFillColorTertiaryBrush");

        void Add(string label, string value, LogLevel? level = null, bool labelFirst = false)
        {
            if (MetricBar.Inlines.Count > 0)
                MetricBar.Inlines.Add(new Run { Text = "   ·   ", FontSize = 11, Foreground = dim });

            var valueRun = new Run
            {
                Text = value,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontFamily = new FontFamily("Consolas"),
            };
            if (level is { } lv && value != "0") valueRun.Foreground = LogReader.BrushOf(lv);

            var labelRun = new Run { Text = label, FontSize = 10.5, Foreground = dim };
            var gap = new Run { Text = " " };

            if (labelFirst) { MetricBar.Inlines.Add(labelRun); MetricBar.Inlines.Add(gap); MetricBar.Inlines.Add(valueRun); }
            else { MetricBar.Inlines.Add(valueRun); MetricBar.Inlines.Add(gap); MetricBar.Inlines.Add(labelRun); }
        }

        Add("总行数", stats.Total.ToString("N0"));
        Add("调试", stats.Debug.ToString("N0"), LogLevel.Debug);
        Add("信息", stats.Info.ToString("N0"), LogLevel.Info);
        Add("警告", stats.Warning.ToString("N0"), LogLevel.Warning);
        Add("错误", stats.Error.ToString("N0"), LogLevel.Error);
        Add("模块", stats.ModuleCount.ToString("N0"));
        Add("含堆栈", stats.MultiLineCount.ToString("N0"));

        if (stats.FirstStamp.Length > 0 && stats.LastStamp.Length > 0)
        {
            var a = stats.FirstStamp;
            var b = stats.LastStamp;
            
            if (a.Length >= 19 && b.Length >= 19 && a[..10] == b[..10]) { a = a[11..]; b = b[11..]; }
            Add("跨度", $"{a} → {b}", labelFirst: true);
        }
    }

    

    private void ApplyFilter()
    {
        var list = LogReader.Filter(_all, _chips.FilterLevels, FilterBox.Text ?? "");

        
        
        _view.Clear();
        foreach (var e in list) _view.Add(e);

        RefreshStats();

        
        if (LogList.SelectedItem is LogEntry sel && !_view.Contains(sel)) ShowRaw(null);
    }

    
    private void RefreshStats()
    {
        var stats = LogReader.Compute(_all, _view.Count);
        _chips.SetCounts(stats);
        BuildMetrics(stats);
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

    

    private void LogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ShowRaw(LogList.SelectedItem as LogEntry);

    
    private void ShowRaw(LogEntry? entry)
    {
        if (entry is null)
        {
            RawTitle.Text = "原文";
            RawText.Text = "在上面的表格里选中一条日志，这里会显示它在文件里的原始内容。";
            RawText.Foreground = LogReader.BrushOf(LogLevel.Info);
            return;
        }

        RawTitle.Text = $"原文 · #{entry.Index} · {entry.LevelText}" +
                        (entry.Module.Length > 0 ? $" · {entry.Module}" : "") +
                        (entry.Stamp.Length > 0 ? $" · {entry.Stamp}" : "");
        RawText.Text = entry.Raw.Length > 0 ? entry.Raw : entry.Message;
        RawText.Foreground = LogReader.NeutralBrush;
    }

    

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

    private void Refresh_Click(object sender, RoutedEventArgs e) => ReloadSessions();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_view.Count == 0) { StatusText.Text = "当前筛选下没有内容可复制。"; return; }
        Copy(LogReader.ToText(_view), $"已复制筛选结果 {_view.Count} 行（原文）。");
    }

    private void CopySelected_Click(object sender, RoutedEventArgs e)
    {
        var picked = LogList.SelectedItems.OfType<LogEntry>().ToList();
        if (picked.Count == 0) { StatusText.Text = "先在表格里选中要复制的行。"; return; }
        Copy(LogReader.ToText(picked), $"已复制选中的 {picked.Count} 行（原文）。");
    }

    private void CopyRaw_Click(object sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItem is not LogEntry entry)
        {
            StatusText.Text = "先在表格里选中一条日志。";
            return;
        }
        Copy(entry.Raw.Length > 0 ? entry.Raw : entry.Message, "已复制这条日志的原文。");
    }

    private void Copy(string text, string done)
    {
        try
        {
            var dp = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            dp.SetText(text);
            Clipboard.SetContent(dp);
            Clipboard.Flush();   
            StatusText.Text = done;
        }
        catch (Exception ex)
        {
            StatusText.Text = "复制失败：" + ex.Message;
        }
    }

    private void LogList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.C) return;

        var ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        if ((ctrl & Windows.UI.Core.CoreVirtualKeyStates.Down) == 0) return;

        var picked = LogList.SelectedItems.OfType<LogEntry>().ToList();
        if (picked.Count == 0) return;

        Copy(LogReader.ToText(picked), $"已复制选中的 {picked.Count} 行（原文）。");
        e.Handled = true;
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

    
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sessions = LogHome.Scan();
            if (sessions.Count == 0) { StatusText.Text = "没有可导出的日志。"; return; }

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop)) desktop = AppLog.Root;

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
                        catch { }
                    }
                }
            }

            StatusText.Text = $"已导出 {sessions.Count} 次运行的 {count} 个日志文件到：{zip}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "导出失败：" + ex.Message;
        }
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape) Close();
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

            if (_suppress || !_atBottom) return;
            if (SessionBox.SelectedItem is not LogSession { IsCurrent: true }) return;
            if (FileBox.SelectedItem is not LogFile cur) return;

            long size = 0;
            try { size = new FileInfo(cur.Path).Length; } catch { return; }

            if (size == _loadedSize) return;   

            
            if (size < _loadedSize || !AppendNew(cur, size)) LoadCurrent();

            ScrollToEnd();
        }
        catch { }
    }

    
    
    private bool AppendNew(LogFile f, long size)
    {
        var (text, truncated) = LogReader.ReadTail(f.Path, LogReader.DetailMaxBytes, LogReader.DetailMaxLines);
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
        RefreshStats();
        return true;
    }

    

    private const int SW_SHOW = 5;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
