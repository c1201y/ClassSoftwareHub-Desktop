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

/// <summary>
/// 日志「详细视图」—— 独立 AppWindow，**给开发者定位问题用**（Nick 2026-10-11 要求）。
///
/// 与主界面日志页（<c>LogViewerPage</c>）的分工：
///   · 主页面 = 概览：够用的上下文 + 级别/关键字筛选 + 一行统计；
///   · 本窗口 = 逐行啃：**序号**、**毫秒时间**、**更大读取上限**（8 MB / 40000 行）、
///     右侧常驻**原文面板**（文件里那一行的原始内容，含缩进堆栈）、**全量统计条**。
///
/// ⛔ 这里刻意不追求观感（Nick 原话：可以牺牲部分视觉效果），但要保住**排版正确**：
///    列宽严格对齐、内容可选中可复制、能滚、深浅色都读得清。
///
/// = 标准窗口 + 自绘标题栏（跟 <see cref="QqFeedbackGuideWindow"/> 一个套路）：
///   拖动 / 阴影 / 圆角交给系统，我们只管标题栏内容；**故意留在任务栏和 Alt+Tab 里**
///   （开发者要一边看它一边操作应用复现问题）。
///
/// ⚠️ 关掉 = **真的销毁**（不像工具浮窗那种"收起来"）：排障窗没有"留着复用"的价值，
///    真关掉最省事，也不会在退出链路上留下没关掉的窗口把进程吊住（§V）。
/// </summary>
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
    /// <summary>是否停在末尾（跟随的闸门）。初始 false = 打开时**从第 1 行看起**。</summary>
    private bool _atBottom;
    private List<LogEntry> _all = new();

    /// <summary>绑给表格的筛选结果。可观察集合 + **增量追加**：跟随时只加新行，不整体重建
    /// （重建会清空再填满，每 2 秒一次就是闪烁，跟主页面同一套规矩）。</summary>
    private readonly System.Collections.ObjectModel.ObservableCollection<LogEntry> _view = new();

    /// <summary>已读进来的文件长度。⛔ 必须自己记：<c>LogFile.Size</c> 是扫描时的快照、
    /// 永不更新，拿它当基准 ⇒ 条件恒为真 ⇒ 每 2 秒重读一次 ⇒ 画面一直抖。</summary>
    private long _loadedSize = -1;

    /// <summary>「Show 时指定要看哪个会话 / 文件」，用完即清（用户随后自己改不该被覆盖）。</summary>
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
            // Show() 里已经先刷过一次（那样窗口一出来就有内容）；这里只在还没数据时兜底
            if (SessionBox.ItemsSource is null) ReloadSessions();
            StartFollow();
        };
    }

    // ── 对外 ─────────────────────────────────────────────────────────────

    /// <summary>摊开这扇窗。已经开着就切到指定的会话 / 文件，再把它调到前面。</summary>
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

    /// <summary>
    /// 退出应用时真正销毁（见 <see cref="MainWindow"/> 的 FinishExit）。
    /// ⛔ Application.Exit() 会漏窗口，没关掉的窗口把消息循环撑住 ⇒ 进程残留（§V）。
    /// </summary>
    public static void CloseForExit()
    {
        var w = _instance;
        _instance = null;
        try { w?.Close(); } catch { }
        AppLog.Info("exit", "日志详细视图实例已请求 Close");
    }

    // ── 窗口本身 ─────────────────────────────────────────────────────────

    private void Configure()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "日志 · 详细视图";

            if (_appWindow.Presenter is OverlappedPresenter p)
            {
                p.SetBorderAndTitleBar(true, true);   // 框架留着（拖动 / 阴影 / 圆角靠系统）
                p.IsResizable = true;                 // 排障窗就是要能拉大
                p.IsMaximizable = true;
                p.IsMinimizable = true;
            }

            ConfigureTitleBar();
            BackdropHost.Apply(this, Root, App.Settings.Current.Backdrop);
            ThemeHost.Apply(Root);

            ApplySize();
            CenterOnScreen();

            Root.ActualThemeChanged += (_, _) => UpdateCaptionButtonColors();

            // Esc 关窗。⚠️ 走 handledEventsToo：焦点多半在里层 ListView / 搜索框上
            Root.AddHandler(UIElement.KeyDownEvent,
                new KeyEventHandler((_, e) =>
                {
                    if (e.Key == Windows.System.VirtualKey.Escape) Close();
                }), true);

            // 真关掉 ⇒ 清掉单例，下次 Show 重新建
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

        // ⚠️ 上一句 ExtendsContentIntoTitleBar 会把窗口圆角偏好按回 Default —— 实测渲染出来是直角。
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

    /// <summary>按逻辑像素（dip）定客户区大小，并夹进当前屏幕（小屏上别长出屏幕外）。</summary>
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

    // ── 数据 ─────────────────────────────────────────────────────────────

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

        // ⚠️ 先量长度、再读内容（理由同主页面：顺序反了会把"读的这一刻新增的行"永久漏掉）
        long size = 0;
        try { size = new FileInfo(f.Path).Length; } catch { }

        var (text, truncated) = LogReader.ReadTail(f.Path, LogReader.DetailMaxBytes, LogReader.DetailMaxLines);
        _loadedSize = size;

        _all = LogReader.Parse(text, Path.GetFileNameWithoutExtension(f.Name));
        ApplyFilter();

        // ⚠️ 必须碰一下 ListScroll：它是"用户有没有停在末尾"的唯一数据源，属性里才挂 ViewChanged。
        _ = ListScroll;

        StatusText.Text = $"{f.Path}\n{LogReader.SizeText(size)} · " +
                          (truncated
                              ? $"已截断：只读了末尾 {LogReader.DetailMaxLines:N0} 行"
                              : "已完整载入")
                          + " · 关闭窗口即可，不会修改任何文件";
        // ⚠️ 刻意**不**在这里滚到底：打开 / 刷新时应该从第 1 行看起（看到一半开始会让人
        //    以为前面的丢了）。只有静默跟随（用户在末尾）才滚到底，见 FollowTick。
    }

    /// <summary>
    /// 统计条：把开发者真正要看的数字一次摆齐（条数 / 级别分布 / 模块数 / 堆栈数 / 时间跨度）。
    /// 主页面那行是"一句话讲清"，这里是"分组数字"，扫一眼就知道该不该细看。
    /// ⚠️ 组与组之间必须有点分隔符：十几个小字挤在一条线上，没有分隔就是一团糊
    ///    （2026-10-11 实拍发现「2 调试 27 信息」这种连读）。文件大小与"是否载入完整"
    ///    不放进来 —— 右下角状态行已经写了，重复占宽度。
    /// ⚠️ 用**一个 TextBlock 的 Inlines**而不是一排小 StackPanel：
    ///    这样字号不同的"数字 / 标签"自动共基线（原来靠 VerticalAlignment=Bottom 凑），
    ///    而且能整条**换行**，窄窗口下不会被顶出可视区。
    /// </summary>
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
            // 同一天就只留时间，日期重复一遍纯占宽度（跨度那一组本来就最长）
            if (a.Length >= 19 && b.Length >= 19 && a[..10] == b[..10]) { a = a[11..]; b = b[11..]; }
            Add("跨度", $"{a} → {b}", labelFirst: true);
        }
    }

    // ── 筛选 ─────────────────────────────────────────────────────────────

    private void ApplyFilter()
    {
        var list = LogReader.Filter(_all, _chips.FilterLevels, FilterBox.Text ?? "");

        // ⚠️ 整表重建只在"用户主动改筛选 / 换文件 / 手动刷新"时发生；
        //    静默跟随走 AppendNew()，只追加新行（否则画面一抖一抖）。
        _view.Clear();
        foreach (var e in list) _view.Add(e);

        RefreshStats();

        // 详情面板跟着筛选走：选中的那条被筛掉了就把面板复位
        if (LogList.SelectedItem is LogEntry sel && !_view.Contains(sel)) ShowRaw(null);
    }

    /// <summary>统计条 + 芯片计数 —— 数据变了就调一次，**不碰列表内容**。</summary>
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

    // ── 原文面板 ─────────────────────────────────────────────────────────

    private void LogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ShowRaw(LogList.SelectedItem as LogEntry);

    /// <summary>把选中那条的**文件原文**摆到右侧面板里（多选时显示最上面那条，其余用复制按钮）。</summary>
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

    // ── 动作 ─────────────────────────────────────────────────────────────

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
            Clipboard.Flush();   // 不加这句，应用一退出剪贴板里就空了
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

    /// <summary>把全部会话打成一个 zip 放到桌面（跟主页面那个导出同一套做法）。</summary>
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

    // ── 静默跟随 ─────────────────────────────────────────────────────────

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

    /// <summary>
    /// 只在「本次运行」且「停在末尾」时静默跟随（跟主页面同一套规矩）。
    /// ⛔⛔ 基准长度用自己记的 <see cref="_loadedSize"/>，别用 <c>LogFile.Size</c>
    /// （那是扫描时的快照、永不更新 ⇒ 条件恒真 ⇒ 每 2 秒重读一次 ⇒ 画面一直抖）；
    /// 有新增时只**追加**，不重建整表。这两条就是"一直抖却没新内容"的解药。
    /// </summary>
    private void FollowTick()
    {
        try
        {
            // ⚠️ "是否停在末尾"每轮重新算（理由同主页面：短内容不触发滚动事件、
            //    ScrollToEnd 之后布局没跟上会拿旧 offset 误判）。
            // ⛔ ViewportHeight==0 = 还没布局完 ⇒ "装得下"那条会误判为真，窗口一开就滚到末尾。
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

            if (size == _loadedSize) return;   // 没长 ⇒ 什么都不做

            // 变短（被清空 / 换了文件）或续接不上（读取上限截断、文件被重写）⇒ 整套重来
            if (size < _loadedSize || !AppendNew(cur, size)) LoadCurrent();

            ScrollToEnd();
        }
        catch { }
    }

    /// <summary>增量续读：只把新多出来的那几行解析出来追加进列表。
    /// 返回 false = 新内容和旧的对不上，调用方要整套重读。</summary>
    private bool AppendNew(LogFile f, long size)
    {
        var (text, truncated) = LogReader.ReadTail(f.Path, LogReader.DetailMaxBytes, LogReader.DetailMaxLines);
        if (truncated) return false;

        // 序号接着老条目往下编（前缀那段的序号是重编的，比对只看 Raw）
        var fresh = LogReader.Parse(text, Path.GetFileNameWithoutExtension(f.Name), _all.Count + 1);
        if (!LogReader.IsExtensionOf(_all, fresh)) return false;

        var levels = _chips.FilterLevels;
        var kw = FilterBox.Text ?? "";

        for (var i = _all.Count; i < fresh.Count; i++)
        {
            var e = fresh[i];
            _all.Add(e);
            // 新增行单独过一遍筛选 —— 与整表用的是同一个 Filter，口径不会漂
            if (LogReader.Filter(new[] { e }, levels, kw).Count > 0) _view.Add(e);
        }

        _loadedSize = size;
        RefreshStats();
        return true;
    }

    // ── Win32 ────────────────────────────────────────────────────────────

    private const int SW_SHOW = 5;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
