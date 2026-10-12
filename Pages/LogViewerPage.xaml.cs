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

/// <summary>
/// 日志查看（入口：设置 → 诊断）。
///
/// ⚠️ 2026-10-11 起日志是「**启动一次 = 一个目录**」（见 <see cref="LogHome"/>）：
///   日志在 <c>%LOCALAPPDATA%\ClassSoftwareHub\logs\{启动时间}\</c>，
///   每个会话目录里正常只有 <c>app.log</c>（日常全部）与 <c>crash.log</c>（崩溃堆栈）。
///   所以控制行有两级下拉：**会话 → 文件**，默认落在「本次运行」。
///
/// 本页的定位是「**概览**」：够用的上下文 + 筛选 + 统计，一眼看清这次运行发生了什么。
/// 要逐行啃、看毫秒时间与完整堆栈、对照原始行号 ⇒ 交给 <c>LogDetailWindow</c>（「详细视图」）。
/// 解析/统计全走 <see cref="LogReader"/>，两个视图共用同一份，结果不会漂移。
///
/// 四条设计约束：
///   ① **只读**：绝不写、不删、不清空 —— 排障工具自己把现场改了就没法查了。
///      （真正的清理在设置 → 存储，按「日志保留天数」整目录回收。）
///   ② **大文件也不能卡**：只读末尾 512 KB / 2000 行；要完整内容走「详细视图」或「导出」。
///   ③ **新格式 + 旧格式都能解析**：交给 <see cref="LogReader.Parse"/>。
///   ④ **没有"自动刷新"开关**（Nick 2026-10-11 定）：只在用户**停在末尾**时静默跟随，
///      一旦往上翻就完全不动 —— 既看得见新日志，又不会在读的时候被顶走。
///   ⑤ **级别筛选是多选**（Nick 2026-10-11 定）：芯片各自独立开合，全勾 = 全部，
///      所以没有「全部」那颗；一个都不勾就是"什么都别看"，页面用空表提示引导勾回来。
/// </summary>
public sealed partial class LogViewerPage : Page
{
    /// <summary>静默跟随的间隔。本地文件读一次很便宜，但没必要更密。</summary>
    private const int FollowMs = 2000;

    /// <summary>距底部这个距离以内就认为"用户看着末尾"，刷新后自动跟着滚。</summary>
    private const double NearBottomPx = 40;

    private readonly LevelChipBar _chips;
    private DispatcherQueueTimer? _followTimer;
    private bool _suppress;
    private List<LogEntry> _all = new();

    /// <summary>
    /// 绑给 <see cref="LogList"/> 的筛选结果。⚠️ 用可观察集合 + **增量追加**：
    /// 静默跟随时只往里加新行，绝不整体重建 —— 重建会清空再填满，每 2 秒一次就是闪烁。
    /// </summary>
    private readonly System.Collections.ObjectModel.ObservableCollection<LogEntry> _view = new();

    /// <summary>
    /// 已经读进来的文件长度（字节）。⛔⛔ **必须自己记**：
    /// <see cref="LogFile.Size"/> 是扫描那一刻的快照，永远不会自己更新，
    /// 拿它当"有没有新内容"的基准 ⇒ 条件恒为真 ⇒ 每 2 秒重读一次 ⇒ 画面一直抖。
    /// 这就是 Nick 2026-10-11 报的"一直刷新、又没有新内容"。
    /// </summary>
    private long _loadedSize = -1;

    private ScrollViewer? _listScroll;
    /// <summary>是否停在末尾（跟随的闸门）。初始 false = 打开时**从第 1 行看起**，
    /// 第一轮 FollowTick 会按真实滚动位置重算，之后才可能开始跟随。</summary>
    private bool _atBottom;

    public LogViewerPage()
    {
        InitializeComponent();

        // 两侧留白 / 标题行上的滚轮也转给表格（见 Core/WheelRelay.cs）
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

    // ── 数据 ────────────────────────────────────────────────

    /// <summary>一次运行（一个会话目录）。</summary>
    private sealed class LogSession
    {
        public string Name { get; init; } = "";
        public string FullPath { get; init; } = "";
        public DateTime Started { get; init; }
        public long Bytes { get; init; }
        public int Files { get; init; }
        public bool IsCurrent { get; init; }
        public bool IsLegacy { get; init; }

        /// <summary>
        /// 下拉里显示什么：当前那次标「本次运行」，升级前归档单独标一句，
        /// 其余显示「月-日 时:分:秒 · N 个文件」。
        /// ⚠️ 不显示完整目录名（<c>2026-10-11_00-13-41</c>）：太长会把控制行撑爆。
        /// </summary>
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

        /// <summary>
        /// 下拉里直接带用途短名（用户不知道一堆 log 是干嘛的）。只放短名 —— 整句太长；
        /// 大小与时间也不放进下拉（它们随写入变化，摆快照值只会误导）。
        /// </summary>
        public string Display => $"{Name}　{LogReader.Describe(Name).Short}";
        public override string ToString() => Display;
    }

    /// <summary>会话目录下的 <c>*.log</c>（只扫顶层；会话目录里也放不下别的东西）。</summary>
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
                catch { /* 单个文件读不到就跳过，别拖垮整张列表 */ }
            }
        }
        catch { }

        // app.log 恒排第一（它是主文件），其余按最后写入倒序
        return list
            .OrderByDescending(f => f.Name.Equals("app.log", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(f => f.Modified)
            .ToList();
    }

    /// <summary>重建会话下拉，并尽量留在原来那个会话上（默认「本次运行」）。</summary>
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

    /// <summary>重建文件下拉（限定在当前选中的会话里）。keepSelection=true 时尽量留在原来那个文件上。</summary>
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

        // 下拉宽度有限，用途整句放不下 —— 挂在 ToolTip 上，鼠标一悬停就看得全
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

        // ⚠️ 先量长度、再读内容：万一读的这一刻文件还在长，长度会大于实际解析到的内容，
        //    下一轮就会因为"又长了"再跟一次；反过来（先读后量）会把这批新增**永久漏掉**。
        long size = 0;
        try { size = new FileInfo(f.Path).Length; } catch { }

        var (text, _) = LogReader.ReadTail(f.Path, LogReader.ListMaxBytes, LogReader.ListMaxLines);
        _loadedSize = size;

        // 该会话里逐行没带模块名的（旧格式、或堆栈行）用文件名兜底
        _all = LogReader.Parse(text, Path.GetFileNameWithoutExtension(f.Name));
        ApplyFilter();

        // ⚠️ 必须碰一下 ListScroll：它是"用户有没有停在末尾"的唯一数据源，属性里才挂 ViewChanged。
        _ = ListScroll;

        // 统计行 / 芯片计数 / ToolTip 由 ApplyFilter → RefreshStats 统一刷新，这里不再重复设。
        // ⚠️ 刻意**不**在这里滚到底：打开 / 刷新 / 换文件时应该从第 1 行看起。
        //    只有静默跟随（用户本来就停在末尾）才滚到底，见 FollowTick。
    }

    /// <summary>
    /// 统计行：把「这次运行到底有多少东西」讲清楚 ——
    /// 条数 / 模块分布 / 时间跨度 / 文件大小 / 有没有被截断。
    /// ⛔ 不再是原来那句"共 N 行"了（Nick：太笼统）。
    /// </summary>
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

    /// <summary>统计行的 ToolTip：把模块分布全量摊开 —— 排查时"哪个模块在狂写"是第一条线索。</summary>
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

    // ── 筛选 ────────────────────────────────────────────────

    private void ApplyFilter()
    {
        // 芯片是**多选**：AllOn 时 FilterLevels 返回 null（= 不按级别筛）
        var list = LogReader.Filter(_all, _chips.FilterLevels, FilterBox.Text ?? "");

        // ⚠️ 整表重建只发生在"用户主动改筛选 / 换文件 / 手动刷新"这三种时刻。
        //    静默跟随走 AppendNew()，只追加新行，绝不重建（否则画面一抖一抖）。
        _view.Clear();
        foreach (var e in list) _view.Add(e);

        RefreshStats(FileBox.SelectedItem as LogFile);
    }

    /// <summary>
    /// 统计行 + 芯片计数 + 空表提示 —— 数据变了就调一次。**不碰列表内容**，
    /// 所以静默跟随可以放心调它（改文本不会有"重建"那种闪烁）。
    /// </summary>
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

    /// <summary>
    /// 空表提示。**三种空法必须分开说**，否则一张白表读不出该改哪儿：
    ///   ① 文件本来就没内容；② 一个级别都没勾（芯片被点空了）；③ 被关键字筛没了。
    /// </summary>
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

    /// <summary>ListView 模板里的 ScrollViewer，拿它判断"用户是否停在底部"。</summary>
    private ScrollViewer? ListScroll
    {
        get
        {
            if (_listScroll is not null) return _listScroll;
            _listScroll = FindDescendantScrollViewer(LogList);
            if (_listScroll is not null)
            {
                // ⚠️ WinUI 的 ViewChanged 参数上没有偏移属性，要从 ScrollViewer 本身读。
                //    只在滚动稳定（!IsIntermediate）时判定 —— ScrollIntoView 的滚动动画
                //    中途 offset 还没到底，会误判成"用户翻上去了"，把跟随掐断。
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

    // ── 主题画刷查表（SystemFillColor* 按项取不到，§D-3） ─────

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

    // ── 文件说明弹窗 ────────────────────────────────────────

    /// <summary>
    /// 「说明」弹窗：讲清当前会话里的每个文件是干嘛的。
    /// ⚠️ 只列磁盘上**真实存在**的文件 —— 讲一堆不存在的文件反而让人困惑。
    /// ContentDialog 至少给一个关闭钮（§D-⑫）。
    /// </summary>
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

    // ── 事件 ────────────────────────────────────────────────

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

    /// <summary>
    /// ⚠️ 只在 <see cref="AutoSuggestionBoxTextChangeReason.UserInput"/> 时重筛：
    /// 程序自己改写文本（或清空）也走这个事件，无条件重筛会打断用户正在做的选中。
    /// </summary>
    private void FilterBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) ApplyFilter();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        // 连会话列表一起重建（清理器可能在上一轮删掉了旧目录）
        ReloadSessions();
    }

    /// <summary>开独立窗的「详细视图」，落在与主页面相同的会话 / 文件上。</summary>
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
            // 开**日志根目录**：每个子文件夹就是一次运行，便于自己翻或清理
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

    /// <summary>
    /// Ctrl+C 复制选中行 —— 控制行上不再有"复制"按钮了（Nick 2026-10-11 要压按钮），
    /// 所以快捷键得自己顶上（右键菜单里也有同样两项）。
    /// </summary>
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
            Clipboard.Flush();      // 不加这句，应用一退出剪贴板里就空了
            Say(done);
        }
        catch (Exception ex)
        {
            Say("复制失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 把**全部会话**打成一个 zip 放到桌面 —— 这是"交给开发者"最省事的一步。
    /// zip 里保留会话目录这一层（<c>2026-10-11_00-13-41/app.log</c>），
    /// 这样"哪天出的问题"一眼可见，不会几份 app.log 混成一锅。
    /// </summary>
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
                        catch { /* 个别文件被独占就跳过，别让整次导出失败 */ }
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

    // ── 静默跟随（取代原来的"自动刷新"开关） ────────────────

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
    /// 静默跟随：**只跟"本次运行"那个会话**，且**只在你停在末尾时**才动手。
    ///   · 历史会话的文件已经不会再长了，不跟；
    ///   · 你正往上翻 = 在读，**一个字都不动**（原来那个自动刷新开关最烦人的就是这个）。
    /// ⚠️ 刻意**不重扫会话列表**：会话目录在本次运行期间是固定的（只有清理器会删旧的），
    ///    每 2 秒全盘统计一遍纯属浪费。
    ///
    /// ⛔⛔ 两条铁律（2026-10-11 Nick 报"一直抖、又没有新内容"，就是这两条的锅）：
    ///   ① **基准长度必须是自己记的 <see cref="_loadedSize"/>**。用 <c>LogFile.Size</c>
    ///      （扫描时的快照、永不更新）当基准，条件恒为真 ⇒ 每 2 秒重读一次；
    ///   ② **有新增时只追加，不重建**（<see cref="AppendNew"/>）。整表重建 = 清空再填满，
    ///      每 2 秒来一次就是肉眼可见的闪烁。
    /// </summary>
    private void FollowTick()
    {
        try
        {
            // ⚠️ "是否停在末尾"每轮**重新算一遍**，别只依赖 ViewChanged：
            //    内容短于视口时根本不会触发滚动事件，那样就永远跟不上了；
            //    反过来 ScrollToEnd 之后布局还没跟上，读到的旧 offset 会误判成"翻上去了"，
            //    所以下一轮再算一次就能自愈。
            // ⛔ ViewportHeight==0 = 还没布局完，此时 ExtentHeight 也是 0，
            //    "装得下"那条会**误判为真** ⇒ 打开页面就自己滚到末尾（2026-10-11 实测踩到）。
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

            if (size == _loadedSize) return;   // 没长 ⇒ 什么都不做（绝大多数 tick 都走这条）

            // 变短（被清空 / 换了文件）或续接不上（读取上限截断、文件被重写）⇒ 只能整套重来
            if (size < _loadedSize || !AppendNew(cur, size)) LoadCurrent();

            ScrollToEnd();
        }
        catch { /* 跟随出问题就安静跳过，等下一个 tick */ }
    }

    /// <summary>
    /// 增量续读：只把文件里**新多出来的那几行**解析出来追加进列表。
    /// 返回 false = 新内容和旧的对不上，调用方要整套重读。
    /// </summary>
    private bool AppendNew(LogFile f, long size)
    {
        var (text, truncated) = LogReader.ReadTail(f.Path, LogReader.ListMaxBytes, LogReader.ListMaxLines);
        if (truncated) return false;

        // 序号接着老条目往下编：续读出来那截前缀的序号是重编的，我们只用它的 Raw 比对，不看序号
        var fresh = LogReader.Parse(text, Path.GetFileNameWithoutExtension(f.Name), _all.Count + 1);
        if (!LogReader.IsExtensionOf(_all, fresh)) return false;

        var levels = _chips.FilterLevels;
        var kw = FilterBox.Text ?? "";

        for (var i = _all.Count; i < fresh.Count; i++)
        {
            var e = fresh[i];
            _all.Add(e);
            // 新增行单独过一遍筛选 —— 和整表用的是同一个 Filter，口径不会漂
            if (LogReader.Filter(new[] { e }, levels, kw).Count > 0) _view.Add(e);
        }

        _loadedSize = size;
        RefreshStats(f);
        return true;
    }

    /// <summary>页面被缓存（NavigationCacheMode=Enabled），切走时必须停掉定时器。</summary>
    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e) => StopFollow();
}
