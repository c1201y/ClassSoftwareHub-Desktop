using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 表格里的一行日志。日志页与详细视图共用（2026-10-11 从 LogViewerPage 里抽出来的）。
///
/// ⚠️ <see cref="LevelGlyph"/> / <see cref="LevelText"/> 只管"是什么"，**颜色不走绑定** ——
/// SystemFillColor* 那批画刷在 DataTemplate 里按项取不到，统一由
/// <see cref="LogReader.BrushOf"/> 在代码里查表赋值（§D-3）。
/// </summary>
public sealed class LogEntry
{
    /// <summary>在文件里的行号（1 起）。详细视图的 <c>#</c> 列用它，便于对照原文。</summary>
    public int Index { get; init; }

    /// <summary>原始时间戳（可能带毫秒）；认不出就是空串。详细视图显示它。</summary>
    public string Stamp { get; init; } = "";

    /// <summary>表格里显示的时间（去掉毫秒，等宽对齐用）。</summary>
    public string Time { get; init; } = "";

    public LogLevel Level { get; init; } = LogLevel.Info;

    /// <summary>模块名（tray / sidebar / ui …）。是**行内标签**，不是文件名。</summary>
    public string Module { get; init; } = "";

    /// <summary>正文（多行时含堆栈续行）。</summary>
    public string Message { get; init; } = "";

    /// <summary>本条在文件里的完整原文（含续行），详情面板用它 —— 保证"看到的就是原文"。</summary>
    public string Raw { get; init; } = "";

    public string IndexText => Index.ToString(CultureInfo.InvariantCulture);

    public string ModuleText => Module.Length == 0 ? "—" : Module;

    /// <summary>堆栈等多行条目：表格里给个标记，提示"点开看详情"。</summary>
    public bool IsMultiLine => Message.IndexOf('\n') >= 0;

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

/// <summary>一次载入的日志的汇总（详细统计用，不再只报一个"共 N 行"）。</summary>
public sealed class LogStats
{
    public int Total { get; init; }
    public int Filtered { get; init; }
    public int Debug { get; init; }
    public int Info { get; init; }
    public int Warning { get; init; }
    public int Error { get; init; }

    /// <summary>出现过的模块数（不含空模块）。</summary>
    public int ModuleCount { get; init; }

    /// <summary>写日志最多的那个模块 + 它的条数。</summary>
    public string TopModule { get; init; } = "";
    public int TopModuleCount { get; init; }

    /// <summary>模块 → 条数，按条数倒序。ToolTip 里展开给开发者看。</summary>
    public IReadOnlyList<(string Module, int Count)> Modules { get; init; } = Array.Empty<(string, int)>();

    /// <summary>首/末条的时间戳原文（空串 = 这批日志没有可识别的时间）。</summary>
    public string FirstStamp { get; init; } = "";
    public string LastStamp { get; init; } = "";

    /// <summary>带堆栈的多行条目数 —— 排障时最该先扫的就是它们。</summary>
    public int MultiLineCount { get; init; }
}

/// <summary>
/// 日志文件的读取 / 解析 / 统计。日志页（<c>LogViewerPage</c>）与详细视图
/// （<c>LogDetailWindow</c>）**共用这一份** —— 两个视图对同一条日志的解析结果必须一模一样，
/// 各写一份迟早会漂移（这正是 2026-10-11 抽出它的原因）。
///
/// 解析要能吃下这些形态：
///   · 新格式：<c>[2026-10-11 00:13:41.123] [警告] [tray] 消息</c>（<see cref="AppLog"/>）；
///   · 旧格式：<c>[2026-10-02 14:00:00] 消息</c> —— 级别按关键词推断；
///   · 只有时间的方括号：<c>[15:07:30.950] 消息</c>；
///   · 裸前缀时间：<c>16:33:15.765 消息</c>（theme.log）；
///   · 缩进行（异常堆栈）→ 归并进上一条；其它无法识别的行 → 独立成条。
/// </summary>
public static class LogReader
{
    /// <summary>列表视图的读取上限（够看、够快）。</summary>
    public const int ListMaxBytes = 512 * 1024;
    public const int ListMaxLines = 2000;

    /// <summary>详细视图的读取上限 —— 它是给开发者定位问题用的，宁可多读。</summary>
    public const int DetailMaxBytes = 8 * 1024 * 1024;
    public const int DetailMaxLines = 40000;

    // ── 文件用途说明 ────────────────────────────────────────

    /// <summary>
    /// 文件用途说明。键是文件名（忽略大小写），值是「短名 + 整句」：
    ///   · 短名进下拉（<c>crash.log　崩溃</c>），够短才扫得快；
    ///   · 整句进「说明」弹窗，讲清"记什么、什么时候该看它"。
    /// ⛔ 别再让短名和整句写成同一个词 —— 那样说明就只是在重复文件名。
    /// 没登记的文件不会被隐藏，只标成「应用日志」。
    /// </summary>
    private static readonly Dictionary<string, (string Short, string Long)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── 现行结构：每个会话目录里就这两个 ──
        // ⚠️ 短名要**短**：下拉的可用宽度只有一百多 dip，长了就被截成"app.log　日常E…"。
        //    整句走 ToolTip / 「说明」弹窗，那儿不限长度。
        ["app.log"] = ("日常", "本次运行的全部日志。所有模块、所有级别都写在这一个文件里，行首的 [模块名] 说明是谁写的"),
        ["crash.log"] = ("崩溃", "崩溃与页面加载失败的完整堆栈 —— 出问题时第一个该看它"),

        // ── 以下只在「升级前的历史日志」那个归档会话里出现（源码里已无写入点）──
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
        ["ui.log"] = ("界面交互", "文本框右键菜单等界面交互的挂载与弹出"),
        ["exit.log"] = ("退出流程", "主窗关闭、收进托盘、真的退出的判定过程"),
        ["download-route.log"] = ("下载线路", "自建加速节点 / 公益镜像的选用、失败与冷却"),
        ["autoshutdown.log"] = ("自动关机", "自动关机的到点巡检与执行记录"),
        ["nav.log"] = ("历史遗留", "已停用：早期的页面跳转记录"),
        ["confirm.log"] = ("历史遗留", "已停用：早期 XAML 资源缺失的崩溃堆栈"),
        ["teachingbar.log"] = ("历史遗留", "已停用：旧版「手边条」的显示与收起记录"),
        ["touchkbd.log"] = ("历史遗留", "已停用：虚拟键盘更早的名字（touchkbd）留下的日志；该功能已在 2026-10-09 整体下线"),
        ["vkbd.log"] = ("历史遗留", "已停用：虚拟键盘已在 2026-10-09 整体下线，不再产生新日志；这里只有旧版本留下的现场"),
        ["vol-verify.log"] = ("历史遗留", "已停用：一次性验证音量浮窗的落位坐标"),
        ["wheel-diag.log"] = ("历史遗留", "已停用：一次性滚轮事件命中诊断"),
    };

    /// <summary>取用途说明；没登记的返回默认（宁可看到"应用日志"，也别显示空白）。</summary>
    public static (string Short, string Long) Describe(string name) =>
        Known.TryGetValue(name, out var d) ? d : ("应用日志", "应用日志。");

    public static string SizeText(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1048576.0:0.#} MB",
    };

    // ── 级别配色（SystemFillColor* 只能代码查表，§D-3） ──────

    private static Brush? _debugBrush, _infoBrush, _warningBrush, _errorBrush, _neutralBrush;

    public static Brush BrushOf(LogLevel level) => level switch
    {
        LogLevel.Debug => _debugBrush ??= Look("TextFillColorTertiaryBrush"),
        LogLevel.Warning => _warningBrush ??= Look("SystemFillColorCautionBrush"),
        LogLevel.Error => _errorBrush ??= Look("SystemFillColorCriticalBrush"),
        _ => _infoBrush ??= Look("TextFillColorSecondaryBrush"),
    };

    /// <summary>「全部」这类不带级别的强调色。</summary>
    public static Brush NeutralBrush => _neutralBrush ??= Look("TextFillColorPrimaryBrush");

    /// <summary>
    /// 按 key 取主题画刷。⚠️ 这是取 <c>SystemFillColor*</c> / <c>ControlFillColor*</c>
    /// 这类"主题字典里的画刷"的**唯一**门路 —— 它们不能在 XAML 里按项取（§D-3），
    /// 也不能在代码里用 <c>new SolidColorBrush(写死 RGB)</c> 顶（§2）。
    /// </summary>
    public static Brush Look(string key)
    {
        try
        {
            if (Application.Current?.Resources is { } res)
            {
                if (res.TryGetValue(key, out var value) && value is Brush b) return b;
                if (res[key] is Brush fallback) return fallback;
            }
        }
        catch { }
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128));
    }

    // ── 读取 ────────────────────────────────────────────────

    /// <summary>
    /// 读文件末尾 <paramref name="maxBytes"/> 字节 / <paramref name="maxLines"/> 行。
    /// ⚠️ 一定要带 <c>FileShare.ReadWrite | FileShare.Delete</c> ——
    /// 这个文件此刻很可能正被本进程的其他线程写着，用默认共享模式打开会自己把自己锁住。
    /// </summary>
    public static (string Text, bool Truncated) ReadTail(string path, int maxBytes, int maxLines)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);

            var length = fs.Length;
            var take = (int)Math.Min(length, maxBytes);
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
            var truncated = take < length || lines.Length > maxLines;
            if (lines.Length > maxLines) lines = lines[^maxLines..];

            return (string.Join("\n", lines).TrimEnd(), truncated);
        }
        catch (Exception ex)
        {
            return ($"（读取失败：{ex.Message}）", false);
        }
    }

    /// <summary>
    /// 把日志文本拆成条目。
    /// ⚠️ 老日志首行可能带 **UTF-8 BOM**（早期用 Encoding.UTF8 写入留下的），
    /// 不剥掉的话 <c>\uFEFF[2026-...]</c> 匹配不上时间戳，首行会显示成一条"没有时间"的怪行。
    /// </summary>
    /// <param name="defaultModule">这一行认不出模块名时用什么兜底（传文件名，旧日志靠它区分来源）。</param>
    /// <param name="startIndex">第一行的序号（截断读取时传"这段之前还有多少行 + 1"更直观，一般传 1）。</param>
    public static List<LogEntry> Parse(string text, string defaultModule, int startIndex = 1)
    {
        var list = new List<LogEntry>();
        if (text.Length == 0) return list;

        var index = startIndex;

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
                    list[^1] = new LogEntry
                    {
                        Index = last.Index,
                        Stamp = last.Stamp,
                        Time = last.Time,
                        Level = level,
                        Module = last.Module,
                        Message = last.Message + "\n" + line,
                        Raw = last.Raw + "\n" + line,
                    };
                }
                else
                {
                    list.Add(new LogEntry
                    {
                        Index = index++,
                        Level = GuessLevel(line),
                        Module = defaultModule,
                        Message = line,
                        Raw = line,
                    });
                }
                continue;
            }

            // ① 方括号时间戳
            if (line[0] == '[')
            {
                var close = line.IndexOf(']');
                if (close > 0 && TryParseStamp(line[1..close], out var stamp, out var time))
                {
                    var rest = line[(close + 1)..].TrimStart();

                    // 第二个方括号若是级别标签就是新格式；否则（如 crash 旧格式的 [where]）留着当内容
                    var level = LogLevel.Info;
                    if (rest.Length > 0 && rest[0] == '[')
                    {
                        var close2 = rest.IndexOf(']');
                        if (close2 > 0)
                        {
                            var tag = rest[1..close2];
                            if (MapTag(tag) is { } lv)
                            {
                                level = lv;
                                rest = rest[(close2 + 1)..].TrimStart();
                            }
                        }
                    }

                    // 再往后若是 [模块名] 就摘出来 —— 2026-10-11 起模块名挪到了行内这一格
                    var module = "";
                    if (rest.Length > 0 && rest[0] == '[')
                    {
                        var close3 = rest.IndexOf(']');
                        if (close3 > 1 && close3 <= 25)
                        {
                            var tag = rest[1..close3];
                            if (IsModuleTag(tag))
                            {
                                module = tag;
                                rest = rest[(close3 + 1)..].TrimStart();
                            }
                        }
                    }

                    if (level == LogLevel.Info) level = GuessLevel(rest);
                    list.Add(new LogEntry
                    {
                        Index = index++,
                        Stamp = stamp,
                        Time = time,
                        Level = level,
                        Module = module.Length > 0 ? module : defaultModule,
                        Message = rest,
                        Raw = line,
                    });
                    continue;
                }
            }

            // ② 裸前缀时间戳（theme.log 这类）
            if (TryParseBareStamp(line, out var bareTime, out var bareRest))
            {
                list.Add(new LogEntry
                {
                    Index = index++,
                    Stamp = bareTime,
                    Time = bareTime,
                    Level = GuessLevel(bareRest),
                    Module = defaultModule,
                    Message = bareRest,
                    Raw = line,
                });
                continue;
            }

            // perf 行等没有时间前缀的 —— 独立成条
            list.Add(new LogEntry
            {
                Index = index++,
                Level = GuessLevel(line),
                Module = defaultModule,
                Message = line,
                Raw = line,
            });
        }

        return list;
    }

    /// <summary>
    /// 还原成可粘贴的纯文本。用**原文** <see cref="LogEntry.Raw"/> 而不是重新拼字段 ——
    /// 复制出去的东西必须跟文件里的一模一样，堆栈的缩进、列宽都不能被我们改掉。
    /// </summary>
    public static string ToText(IEnumerable<LogEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries) sb.AppendLine(e.Raw.Length > 0 ? e.Raw : e.Message);
        return sb.ToString();
    }

    /// <summary>
    /// 按级别集合 / 关键字筛。关键字同时匹配「模块」与「内容」。
    /// <paramref name="levels"/> 为 <c>null</c> = 不按级别筛；**空集合 = 一条都不显示**
    /// （两个视图的芯片都是多选，全不勾就是"什么都别看"，与字面语义一致）。
    /// </summary>
    public static List<LogEntry> Filter(IEnumerable<LogEntry> source,
                                        IReadOnlyCollection<LogLevel>? levels, string keyword)
    {
        var kw = (keyword ?? "").Trim();
        var empty = kw.Length == 0;
        var anyLevel = levels is null;

        return source
            .Where(e => (anyLevel || levels!.Contains(e.Level))
                        && (empty
                            || e.Message.Contains(kw, StringComparison.OrdinalIgnoreCase)
                            || e.Module.Contains(kw, StringComparison.OrdinalIgnoreCase)
                            || e.Stamp.Contains(kw, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>
    /// <paramref name="fresh"/> 是不是只是 <paramref name="old"/> 的**续接**（旧的是新的前缀）。
    ///
    /// 用途：静默跟随时**只追加新行**，不把整张表重建 —— 整表重建会瞬间清空再填满，
    /// 每 2 秒来一次就是"画面一抖一抖"（Nick 2026-10-11 报的正是这个）。
    /// 比的是 <see cref="LogEntry.Raw"/>（文件原文）而不是 <see cref="LogEntry.Index"/>：
    /// 续读时前端那段的序号是重新编的，不代表内容变了。
    /// </summary>
    public static bool IsExtensionOf(IReadOnlyList<LogEntry> old, IReadOnlyList<LogEntry> fresh)
    {
        if (fresh.Count <= old.Count) return false;

        for (var i = 0; i < old.Count; i++)
            if (!string.Equals(old[i].Raw, fresh[i].Raw, StringComparison.Ordinal)) return false;

        return true;
    }

    /// <summary>汇总统计。开发者一眼要看的都在这里（级别分布、模块分布、时间跨度、堆栈条数）。</summary>
    public static LogStats Compute(IReadOnlyList<LogEntry> all, int filteredCount)
    {
        var modules = all
            .Where(e => e.Module.Length > 0)
            .GroupBy(e => e.Module, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Module: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Module, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var withTime = all.Where(e => e.Stamp.Length > 0).ToList();

        return new LogStats
        {
            Total = all.Count,
            Filtered = filteredCount,
            Debug = all.Count(e => e.Level == LogLevel.Debug),
            Info = all.Count(e => e.Level == LogLevel.Info),
            Warning = all.Count(e => e.Level == LogLevel.Warning),
            Error = all.Count(e => e.Level == LogLevel.Error),
            ModuleCount = modules.Count,
            TopModule = modules.Count > 0 ? modules[0].Module : "",
            TopModuleCount = modules.Count > 0 ? modules[0].Count : 0,
            Modules = modules,
            FirstStamp = withTime.Count > 0 ? withTime[0].Stamp : "",
            LastStamp = withTime.Count > 0 ? withTime[^1].Stamp : "",
            MultiLineCount = all.Count(e => e.IsMultiLine),
        };
    }

    /// <summary>是不是一个「模块标签」。收得比较保守（纯 ASCII、≤24 字、不能带空格），
    /// 免得把旧日志里 <c>[设置页]</c>、<c>[System.Xaml 加载失败]</c> 这种正文当成模块名摘走。</summary>
    public static bool IsModuleTag(string tag)
    {
        if (tag.Length is 0 or > 24) return false;
        if (!char.IsAsciiLetter(tag[0])) return false;
        foreach (var c in tag)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) return false;
        }
        return true;
    }

    /// <summary>把方括号里的时间戳原文转成「原文 + 显示文本」。</summary>
    private static bool TryParseStamp(string stamp, out string full, out string display)
    {
        full = stamp.Trim().Replace('T', ' ');
        display = full;
        var dot = display.IndexOf('.');
        if (dot > 0) display = display[..dot];

        if (display.Length >= 16 && char.IsDigit(display[0]) && display[4] == '-' && display[7] == '-')
            return true;

        // 只有时间、没有日期 —— 不少模块的日志是这个形态
        if (IsClock(display)) return true;

        full = "";
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

        time = line[..12];
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
}
