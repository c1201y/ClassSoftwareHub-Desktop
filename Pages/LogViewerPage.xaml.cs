using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 表格里的一行日志。
/// ⚠️ <see cref="LevelGlyph"/> / <see cref="LevelText"/> 只管"是什么"，颜色由
/// <see cref="LogViewerPage.LogList_ContainerContentChanging"/> 按级别查主题画刷赋值 ——
/// SystemFillColor* 那批画刷 XAML 里取不到（§D-3），所以颜色不走绑定。
/// </summary>
public sealed class LogEntry
{
    /// <summary>原始时间戳文本（保持日志原样，不重排版）。</summary>
    public string Time { get; init; } = "";
    public LogLevel Level { get; init; } = LogLevel.Info;
    public string Message { get; init; } = "";

    public string LevelText => Level switch
    {
        LogLevel.Debug => "调试",
        LogLevel.Warning => "警告",
        LogLevel.Error => "错误",
        _ => "信息",
    };

    public string LevelGlyph => Level switch
    {
        LogLevel.Debug => "\uE721",   // 放大镜（诊断）
        LogLevel.Warning => "\uE7BA", // 警告三角
        LogLevel.Error => "\uE783",   // 错误圆标
        _ => "\uE946",                // 信息圆标
    };
}

/// <summary>
/// 日志查看（实验 → 2026-10-02 起入口改到 设置 → 诊断，Nick：这不是实验功能）。
///
/// 数据目录已分区（见 <see cref="AppLog"/>）：日志在 <c>%LOCALAPPDATA%\ClassSoftwareHub\logs\</c>。
///
/// 四条设计约束：
///   ① **文件不写死**：进页面扫目录里的 <c>*.log</c>，新加的日志自动出现；
///      下拉项直接带用途说明（Nick：用户不知道一堆 log 是干嘛的）。
///   ② **只读**：绝不写、不删、不清空 —— 排障工具自己把现场改了就没法查了。
///      （所以没有 ClassIsland 那个「清空日志」按钮，是有意的。）
///   ③ **大文件也不能卡**：只读末尾 512 KB / 2000 行；要完整内容走「导出全部日志」。
///   ④ **新格式 + 旧格式都能解析**：新格式 <c>[时间] [级别] 消息</c>（<see cref="AppLog"/>）；
///      旧格式 <c>[时间] 消息</c> 按关键词推断级别；堆栈等缩进行归并进上一条。
///
/// 版面骨架（2026-10-02 四改）：工具条（页面级）→ 内容卡片（区块级）。
/// 文件用途说明收进「文件说明」按钮的弹窗（Nick：单独占一行太浪费空间）；
/// 级别筛选用系统原生 <see cref="SelectorBar"/>（**单选**：全部 / 调试 / 信息 / 警告 / 错误）——
/// 段控是原生控件里唯一"看着像筛选器、又不像一排按钮"的东西；代价是失去了多选组合。
/// </summary>
public sealed partial class LogViewerPage : Page
{
    /// <summary>单个文件最多显示多少行 / 读多少字节。</summary>
    private const int MaxLines = 2000;
    private const int MaxBytes = 512 * 1024;

    /// <summary>自动刷新间隔。日志是本地文件，读一次很便宜，但没必要更密。</summary>
    private const int AutoRefreshMs = 2000;

    /// <summary>距底部这个距离以内就认为"用户看着末尾"，刷新后自动跟着滚。</summary>
    private const double NearBottomPx = 40;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _autoTimer;
    private bool _suppress;
    private List<string> _names = new();
    private List<LogEntry> _all = new();
    private List<LogEntry> _filtered = new();
    private ScrollViewer? _listScroll;
    private bool _atBottom = true;

    /// <summary>
    /// 文件用途说明。键是文件名（忽略大小写），值是「短名 + 整句」：
    ///   · 短名进下拉（<c>tray.log　托盘图标</c>），够短才扫得快；
    ///   · 整句进「文件说明」弹窗，讲清"记什么、什么时候该看它"。
    /// ⛔ 别再让短名和整句写成同一个词 —— 那样说明就只是在重复文件名（2026-10-02 改）。
    /// 没登记的文件不会被隐藏，只标成「应用日志」。
    /// </summary>
    private static readonly Dictionary<string, (string Short, string Long)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["crash.log"] = ("崩溃", "程序异常退出、页面加载失败 —— 出问题时第一个该看它，里面有完整堆栈"),
        ["snip.log"] = ("截屏与启动", "截屏贴图、启动流程、单实例唤醒（点桌面图标把窗口叫回来那条链路）"),
        ["perf.log"] = ("切页耗时", "切页与首帧耗时探针。只在 Debug 编译、或设了环境变量 CSH_PERF=1 时才写入"),
        ["memory.log"] = ("内存回收", "每次回收前后的工作集与托管堆，以及「为什么这一轮没有回收」"),
        ["settings.log"] = ("设置读写", "配置被判定为损坏、或某项值被重置到默认时看它"),
        ["sidebar.log"] = ("工具侧边栏", "侧边栏的显示、收起、贴边、换边与重建"),
        ["layout.log"] = ("侧边栏版式", "拖拽落位、尺寸、停靠与自由模式的几何记录"),
        ["palette.log"] = ("工具浮窗", "常用工具浮窗的显示、收起与落位"),
        ["tray.log"] = ("托盘图标", "托盘图标的创建、气泡通知与退出请求"),
        ["chrome.log"] = ("窗口外观", "窗口圆角、边框、标题栏区域的诊断信息"),
        ["theme.log"] = ("主题配色", "明暗主题切换时实际取到的主题资源快照"),
        ["easiguard.log"] = ("白板专杀", "定时结束白板5（EasiNote 开头）驻留进程的执行记录"),
        ["procguard.log"] = ("程序专杀", "定时结束所设程序驻留进程的执行记录"),
        ["teaching.log"] = ("教学操作", "关前台应用、专注模式等教学动作的执行记录"),
        ["brightness.log"] = ("屏幕亮度", "亮度读取与设置的每一步 —— 读不到亮度时看它卡在哪一步"),
        ["clock-presets.log"] = ("全屏时钟预设", "全屏时钟预设的增删改"),

        // ── 以下为**历史遗留**：源码里已无任何写入点，只剩旧版本留下的文件 ──
        // 留着不删（用户可能还在用旧版本、或文件里有旧现场），但要如实标出来 ——
        // 否则「交给开发者」时会被当成真的活跃日志去分析。
        ["confirm.log"] = ("历史遗留", "已停用：早期 XAML 资源缺失的崩溃堆栈"),
        ["teachingbar.log"] = ("历史遗留", "已停用：旧版「手边条」的显示与收起记录"),
        ["touchkbd.log"] = ("历史遗留", "已停用：虚拟键盘更早的名字（touchkbd）留下的日志；该功能已在 2026-10-09 整体下线"),
        ["vkbd.log"] = ("历史遗留", "已停用：虚拟键盘已在 2026-10-09 整体下线，不再产生新日志；这里只有旧版本留下的现场"),
        ["vol-verify.log"] = ("历史遗留", "已停用：一次性验证音量浮窗的落位坐标"),
        ["wheel-diag.log"] = ("历史遗留", "已停用：一次性滚轮事件命中诊断"),
    };

    /// <summary>取用途说明；没登记的返回默认（宁可用户看到"应用日志"，也别显示空白）。</summary>
    private static (string Short, string Long) Describe(string name) =>
        Known.TryGetValue(name, out var d) ? d : ("应用日志", "应用日志。");

    public LogViewerPage()
    {
        InitializeComponent();

        // 段控默认落在「全部」。不写在 XAML 里是因为 SelectorBar 的选中态要以
        // SelectedItem 为准，XAML 的 IsSelected 在部分场景下不会回填到 SelectedItem。
        try { LevelBar.SelectedItem = LevelBar.Items[0]; } catch { }

        Loaded += (_, _) =>
        {
            if (_names.Count == 0) ReloadFiles(keepSelection: false);
            // 自动刷新的开关状态以 ToggleButton 为准（它在 XAML 里默认选中）。
            // ⚠️ 别再无条件 StartAuto()：那会让"开关显示关、定时器却在跑"。
            if (AutoToggle.IsChecked == true) StartAuto();
        };
    }

    // ── 数据 ────────────────────────────────────────────────

    private sealed class LogFile
    {
        public string Path { get; init; } = "";
        public string Name { get; init; } = "";
        public long Size { get; init; }
        public DateTime Modified { get; init; }

        /// <summary>
        /// 下拉里直接带用途短名（Nick：用户不知道一堆 log 是干嘛的）。
        /// 只放短名 —— 整句太长，二十个文件的下拉会撑得没法扫；
        /// 大小与时间也不放进下拉（它们随写入变化，摆快照值只会误导），实时值统一在卡片脚的状态行。
        /// </summary>
        public string Display => $"{Name}　{Describe(Name).Short}";
        public override string ToString() => Display;

        public static string SizeText(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
            _ => $"{bytes / 1048576.0:0.#} MB",
        };
    }

    private static List<LogFile> Scan()
    {
        var list = new List<LogFile>();
        try
        {
            var dir = AppLog.Dir;
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

        return list.OrderByDescending(f => f.Modified).ToList();
    }

    /// <summary>重新扫目录 + 重建下拉。keepSelection=true 时尽量留在原来那个文件上。</summary>
    private void ReloadFiles(bool keepSelection)
    {
        var wanted = keepSelection ? (FileBox.SelectedItem as LogFile)?.Name : null;
        var items = Scan();

        _suppress = true;
        FileBox.ItemsSource = items;
        FileBox.SelectedItem = (wanted is null ? null : items.FirstOrDefault(f => f.Name == wanted))
                               ?? items.FirstOrDefault();
        _suppress = false;

        _names = items.Select(f => f.Name).ToList();
        LoadCurrent();
    }

    private void LoadCurrent()
    {
        if (FileBox.SelectedItem is not LogFile f)
        {
            _all = new List<LogEntry>();
            ApplyFilter();
            StatusText.Text = "本机还没有产生任何日志文件。";
            return;
        }

        var keepBottom = _atBottom;
        var (text, truncated) = ReadTail(f.Path);

        long size = 0;
        var modified = DateTime.MinValue;
        try
        {
            var info = new FileInfo(f.Path);
            size = info.Length;
            modified = info.LastWriteTime;
        }
        catch { }

        _all = ParseLog(text);
        ApplyFilter();

        StatusText.Text = $"{f.Name} · {LogFile.SizeText(size)} · 最后写入 {modified:yyyy-MM-dd HH:mm:ss}"
                          + (truncated ? $" · 只显示末尾 {MaxLines} 行（要完整内容请导出）" : "");

        if (keepBottom) ScrollToEnd();
    }

    // ── 解析 ────────────────────────────────────────────────

    /// <summary>
    /// 把日志文本拆成条目。这些形态都能吃：
    ///   · 新格式：<c>[2026-10-02 14:00:00.123] [警告] 消息</c>（<see cref="AppLog"/>）；
    ///   · 旧格式：<c>[2026-10-02 14:00:00] 消息</c> —— 级别按关键词推断；
    ///   · 只有时间的方括号：<c>[15:07:30.950] 消息</c>（layout.log 等）；
    ///   · 裸前缀时间：<c>16:33:15.765 消息</c>（theme.log）；
    ///   · 缩进行（异常堆栈）→ 归并进上一条；其它无法识别的行 → 独立成条。
    /// ⚠️ 老日志首行可能带 **UTF-8 BOM**（早期用 Encoding.UTF8 写入留下的），
    ///    不剥掉的话 <c>\uFEFF[2026-...]</c> 匹配不上时间戳，首行会显示成一条"没有时间"的怪行。
    /// </summary>
    private static List<LogEntry> ParseLog(string text)
    {
        var list = new List<LogEntry>();
        if (text.Length == 0) return list;

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Length > 0 && line[0] == '\uFEFF') line = line[1..];
            if (line.Length == 0) continue;

            // 缩进行 = 上一条的延续（异常堆栈）
            if (char.IsWhiteSpace(line[0]))
            {
                if (list.Count > 0)
                {
                    var last = list[^1];
                    // ⚠️ 级别取"更强的那一个"：堆栈行里才会出现 Exception / 异常 字样，
                    //    标题行反而可能只是一句普通描述 —— 只按首行判的话，
                    //    crash.log 的崩溃条目会被标成"信息"（2026-10-02 实测踩到）。
                    var lv = GuessLevel(line);
                    var level = Rank(lv) > Rank(last.Level) ? lv : last.Level;
                    list[^1] = new LogEntry { Time = last.Time, Level = level, Message = last.Message + "\n" + line };
                }
                else
                {
                    list.Add(new LogEntry { Time = "", Level = GuessLevel(line), Message = line });
                }
                continue;
            }

            // ① 方括号时间戳
            if (line[0] == '[')
            {
                var close = line.IndexOf(']');
                if (close > 0 && TryParseStamp(line[1..close], out var time))
                {
                    var rest = line[(close + 1)..].TrimStart();

                    // 第二个方括号若是级别标签就是新格式；否则（如 crash 旧格式的 [where]）整段当消息
                    var level = LogLevel.Info;
                    if (rest.Length > 0 && rest[0] == '[')
                    {
                        var close2 = rest.IndexOf(']');
                        if (close2 > 0)
                        {
                            var tag = rest[1..close2];
                            var mapped = MapTag(tag);
                            if (mapped is { } lv)
                            {
                                level = lv;
                                rest = rest[(close2 + 1)..].TrimStart();
                            }
                        }
                    }

                    if (level == LogLevel.Info) level = GuessLevel(rest);
                    list.Add(new LogEntry { Time = time, Level = level, Message = rest });
                    continue;
                }
            }

            // ② 裸前缀时间戳（theme.log 这类）
            if (TryParseBareStamp(line, out var bareTime, out var bareRest))
            {
                list.Add(new LogEntry { Time = bareTime, Level = GuessLevel(bareRest), Message = bareRest });
                continue;
            }

            // perf 行等没有时间前缀的 —— 独立成条
            list.Add(new LogEntry { Time = "", Level = GuessLevel(line), Message = line });
        }

        return list;
    }

    /// <summary>把方括号里的时间戳原文转成显示文本（认不出就返回 false，整行当消息兜底）。</summary>
    private static bool TryParseStamp(string stamp, out string display)
    {
        // 2026-10-02 14:00:00 / 2026-10-02 14:00:00.123 / 2026-10-02T14:00:00 / 15:07:30.950
        display = stamp.Trim().Replace('T', ' ');
        var dot = display.IndexOf('.');
        if (dot > 0) display = display[..dot];

        if (display.Length >= 16 && char.IsDigit(display[0]) && display[4] == '-' && display[7] == '-')
            return true;

        // 只有时间、没有日期 —— 不少模块的日志是这个形态
        if (IsClock(display)) return true;

        display = "";
        return false;
    }

    /// <summary>裸前缀时间：<c>16:33:15.765 正文</c>（theme.log 用的就是这个格式）。</summary>
    private static bool TryParseBareStamp(string line, out string time, out string rest)
    {
        time = "";
        rest = line;

        // 必须 HH:mm:ss.fff 紧跟空白或行尾 —— 收紧了才不会把普通正文误判成时间
        if (line.Length < 12) return false;
        if (!IsClock(line[..8])) return false;
        if (line[8] != '.') return false;
        if (!(char.IsDigit(line[9]) && char.IsDigit(line[10]) && char.IsDigit(line[11]))) return false;
        if (line.Length > 12 && !char.IsWhiteSpace(line[12])) return false;

        time = line[..8];
        rest = line.Length > 12 ? line[13..].TrimStart() : "";
        return true;
    }

    /// <summary>是不是 <c>HH:mm:ss</c> 这 8 个字符。</summary>
    private static bool IsClock(string s) =>
        s.Length == 8 && s[2] == ':' && s[5] == ':'
        && char.IsAsciiDigit(s[0]) && char.IsAsciiDigit(s[1])
        && char.IsAsciiDigit(s[3]) && char.IsAsciiDigit(s[4])
        && char.IsAsciiDigit(s[6]) && char.IsAsciiDigit(s[7]);

    private static LogLevel? MapTag(string tag) => tag switch
    {
        "调试" => LogLevel.Debug,
        "信息" => LogLevel.Info,
        "警告" => LogLevel.Warning,
        "错误" => LogLevel.Error,
        _ => null,
    };

    /// <summary>
    /// 旧格式行没有级别 —— 按关键词推断。关键词收得保守：
    /// 宁可漏报成"信息"也别把正常行报成"错误"吓人；新格式写入后不再走这里。
    /// </summary>
    private static LogLevel GuessLevel(string message)
    {
        if (message.Contains("Exception", StringComparison.OrdinalIgnoreCase)
            || message.Contains("异常") || message.Contains("崩溃") || message.Contains("堆栈"))
        {
            return LogLevel.Error;
        }
        if (ContainsAny(message, "失败", "错误", "无法", "超时", "警告", "error", "warn"))
        {
            return LogLevel.Warning;
        }
        return LogLevel.Info;
    }

    /// <summary>级别强弱排序（归并堆栈行时用来取更强的那个）。</summary>
    private static int Rank(LogLevel level) => level switch
    {
        LogLevel.Error => 3,
        LogLevel.Warning => 2,
        LogLevel.Info => 1,
        _ => 0,
    };

    private static bool ContainsAny(string text, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (text.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ── 筛选 ────────────────────────────────────────────────

    /// <summary>段控当前选中的级别；null = 全部（不按级别筛）。</summary>
    private LogLevel? SelectedLevel()
    {
        var tag = (LevelBar.SelectedItem as SelectorBarItem)?.Tag as string;
        return tag switch
        {
            "debug" => LogLevel.Debug,
            "info" => LogLevel.Info,
            "warning" => LogLevel.Warning,
            "error" => LogLevel.Error,
            _ => null,
        };
    }

    private void ApplyFilter()
    {
        var level = SelectedLevel();
        var keyword = (FilterBox.Text ?? "").Trim();
        var keywordEmpty = keyword.Length == 0;

        _filtered = _all.Where(e => (level is null || e.Level == level)
                                    && (keywordEmpty || e.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                        .ToList();

        LogList.ItemsSource = _filtered;
    }

    private void ScrollToEnd()
    {
        if (_filtered.Count == 0) return;
        try
        {
            LogList.UpdateLayout();
            LogList.ScrollIntoView(_filtered[^1]);
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
                //    中途 offset 还没到底，会误判成"用户翻上去了"，把自动跟随掐断。
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
            var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i);
                if (child is ScrollViewer sv) return sv;
                var deep = FindDescendantScrollViewer(child);
                if (deep is not null) return deep;
            }
            return null;
        }
        return null;
    }

    // ── 读取 ────────────────────────────────────────────────

    /// <summary>
    /// 读文件末尾。⚠️ 一定要带 <c>FileShare.ReadWrite | FileShare.Delete</c> ——
    /// 这个文件此刻很可能正被本进程的其他线程写着，用默认共享模式打开会自己把自己锁住。
    /// </summary>
    private static (string Text, bool Truncated) ReadTail(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);

            var length = fs.Length;
            var take = (int)Math.Min(length, MaxBytes);
            fs.Seek(-take, SeekOrigin.End);

            var buffer = new byte[take];
            var read = 0;
            while (read < take)
            {
                var n = fs.Read(buffer, read, take - read);
                if (n <= 0) break;
                read += n;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, read);

            // 从中间切进来的第一行多半是半截 → 丢掉（只有确实截断过才需要）
            if (take < length)
            {
                var nl = text.IndexOf('\n');
                if (nl >= 0) text = text[(nl + 1)..];
            }

            var lines = text.Replace("\r\n", "\n").Split('\n');
            var truncated = take < length || lines.Length > MaxLines;
            if (lines.Length > MaxLines) lines = lines[^MaxLines..];

            return (string.Join("\n", lines).TrimEnd(), truncated);
        }
        catch (Exception ex)
        {
            return ($"（读取失败：{ex.Message}）", false);
        }
    }

    // ── 主题画刷查表（SystemFillColor* XAML 取不到，§D-3） ─────

    private Brush? _debugBrush, _infoBrush, _warningBrush, _errorBrush;

    private Brush BrushOf(LogLevel level) => level switch
    {
        LogLevel.Debug => _debugBrush ??= Look("TextFillColorTertiaryBrush"),
        LogLevel.Warning => _warningBrush ??= Look("SystemFillColorCautionBrush"),
        LogLevel.Error => _errorBrush ??= Look("SystemFillColorCriticalBrush"),
        _ => _infoBrush ??= Look("TextFillColorSecondaryBrush"),
    };

    private static Brush Look(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush b
            ? b
            : (Brush)Application.Current.Resources[key];

    private void LogList_ContainerContentChanging(object sender, ContainerContentChangingEventArgs args)
    {
        if (args.Phase != 0 || args.Item is not LogEntry entry) return;

        if (args.ItemContainer.ContentTemplateRoot is Grid root)
        {
            if (root.FindName("LevelIcon") is FontIcon icon) icon.Foreground = BrushOf(entry.Level);
            if (root.FindName("LevelLabel") is TextBlock text) text.Foreground = BrushOf(entry.Level);
        }
    }

    // ── 文件说明弹窗 ────────────────────────────────────────

    /// <summary>
    /// 「文件说明」弹窗：逐个讲本机现有的每个 log 是干嘛的。
    /// ⚠️ 只列磁盘上**真实存在**的文件 —— 讲一堆不存在的文件反而让人困惑；
    ///    没登记用途的标「应用日志」兜底。ContentDialog 至少给一个关闭钮（§D-⑫）。
    /// </summary>
    private void FilesHelp_Click(object sender, RoutedEventArgs e)
    {
        var host = new StackPanel { Spacing = 12 };

        var items = (FileBox.ItemsSource as IEnumerable<LogFile>)?.ToList() ?? new List<LogFile>();
        if (items.Count == 0)
        {
            host.Children.Add(new TextBlock
            {
                Text = "本机还没有产生任何日志文件。",
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            foreach (var f in items)
            {
                var d = Describe(f.Name);
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
                            Foreground = Look("TextFillColorSecondaryBrush"),
                        },
                    },
                });
            }
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "每个日志文件是干什么的",
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

    private void FileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        LoadCurrent();
    }

    private void LevelBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        => ApplyFilter();

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
        ReloadFiles(keepSelection: true);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = AppLog.Dir;
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

    private void CopySelected_Click(object sender, RoutedEventArgs e)
    {
        var picked = LogList.SelectedItems.OfType<LogEntry>().ToList();
        if (picked.Count == 0) { Say("先在表格里选中要复制的行。"); return; }
        CopyEntries(picked, $"已复制选中的 {picked.Count} 行到剪贴板。");
    }

    private void CopyFiltered_Click(object sender, RoutedEventArgs e)
    {
        if (_filtered.Count == 0) { Say("当前筛选下没有内容可复制。"); return; }
        CopyEntries(_filtered, $"已复制筛选结果 {_filtered.Count} 行到剪贴板。");
    }

    private void CopyEntries(List<LogEntry> entries, string done)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var en in entries)
            {
                var time = en.Time.Length == 0 ? "" : en.Time + "  ";
                sb.AppendLine($"{time}[{en.LevelText}] {en.Message}");
            }

            var dp = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            dp.SetText(sb.ToString());
            Clipboard.SetContent(dp);
            Clipboard.Flush();      // 不加这句，应用一退出剪贴板里就空了
            Say(done);
        }
        catch (Exception ex)
        {
            Say("复制失败：" + ex.Message);
        }
    }

    /// <summary>把目录下所有 .log 打成一个 zip 放到桌面 —— 这是"交给开发者"最省事的一步。</summary>
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = AppLog.Dir;
            var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.log") : Array.Empty<string>();
            if (files.Length == 0) { Say("没有可导出的日志。"); return; }

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop)) desktop = dir;

            var zip = Path.Combine(desktop, $"ClassSoftwareHub-日志-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            if (File.Exists(zip)) File.Delete(zip);

            var count = 0;
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    try
                    {
                        using var src = new FileStream(file, FileMode.Open, FileAccess.Read,
                                                       FileShare.ReadWrite | FileShare.Delete);
                        var entry = archive.CreateEntry(Path.GetFileName(file), CompressionLevel.Optimal);
                        using var dst = entry.Open();
                        src.CopyTo(dst);
                        count++;
                    }
                    catch { /* 个别文件被独占就跳过，别让整次导出失败 */ }
                }
            }

            Say($"已导出 {count} 个日志文件到：{zip}");
        }
        catch (Exception ex)
        {
            Say("导出失败：" + ex.Message);
        }
    }

    private void AutoToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (AutoToggle.IsChecked == true) StartAuto(); else StopAuto();
    }

    private void StartAuto()
    {
        StopAuto();

        var queue = DispatcherQueue;
        if (queue is null) return;

        _autoTimer = queue.CreateTimer();
        _autoTimer.Interval = TimeSpan.FromMilliseconds(AutoRefreshMs);
        _autoTimer.Tick += (_, _) => AutoTick();
        _autoTimer.Start();
    }

    private void StopAuto()
    {
        try { _autoTimer?.Stop(); } catch { }
        _autoTimer = null;
    }

    /// <summary>
    /// 自动刷新：文件名集合变了就重建下拉（新日志出现 / 旧的被删），
    /// 否则只在当前文件**真的长了**才重读 —— 每次无条件重设会打断用户正在做的文本选中与滚动。
    /// </summary>
    private void AutoTick()
    {
        try
        {
            if (_suppress) return;

            var currentName = (FileBox.SelectedItem as LogFile)?.Name;
            var items = Scan();

            if (!items.Select(f => f.Name).SequenceEqual(_names))
            {
                _suppress = true;
                FileBox.ItemsSource = items;
                FileBox.SelectedItem = (currentName is null ? null : items.FirstOrDefault(f => f.Name == currentName))
                                       ?? items.FirstOrDefault();
                _suppress = false;

                _names = items.Select(f => f.Name).ToList();
                LoadCurrent();
                return;
            }

            if (FileBox.SelectedItem is not LogFile cur) return;

            long size = 0;
            try { size = new FileInfo(cur.Path).Length; } catch { }
            if (size != cur.Size) LoadCurrent();
        }
        catch { /* 自动刷新出问题就安静跳过，等下一个 tick */ }
    }

    /// <summary>页面被缓存（NavigationCacheMode=Enabled），切走时必须停掉定时器。</summary>
    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e) => StopAuto();
}
