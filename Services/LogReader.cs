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








public sealed class LogEntry
{
    
    public int Index { get; init; }

    
    public string Stamp { get; init; } = "";

    
    public string Time { get; init; } = "";

    public LogLevel Level { get; init; } = LogLevel.Info;

    
    public string Module { get; init; } = "";

    
    public string Message { get; init; } = "";

    
    public string Raw { get; init; } = "";

    public string IndexText => Index.ToString(CultureInfo.InvariantCulture);

    public string ModuleText => Module.Length == 0 ? "—" : Module;

    
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
        LogLevel.Debug => "\uE721",   
        LogLevel.Warning => "\uE7BA", 
        LogLevel.Error => "\uE783",   
        _ => "\uE946",                
    };
}


public sealed class LogStats
{
    public int Total { get; init; }
    public int Filtered { get; init; }
    public int Debug { get; init; }
    public int Info { get; init; }
    public int Warning { get; init; }
    public int Error { get; init; }

    
    public int ModuleCount { get; init; }

    
    public string TopModule { get; init; } = "";
    public int TopModuleCount { get; init; }

    
    public IReadOnlyList<(string Module, int Count)> Modules { get; init; } = Array.Empty<(string, int)>();

    
    public string FirstStamp { get; init; } = "";
    public string LastStamp { get; init; } = "";

    
    public int MultiLineCount { get; init; }
}













public static class LogReader
{
    
    public const int ListMaxBytes = 512 * 1024;
    public const int ListMaxLines = 2000;

    
    public const int DetailMaxBytes = 8 * 1024 * 1024;
    public const int DetailMaxLines = 40000;

    

    
    
    
    
    
    
    
    private static readonly Dictionary<string, (string Short, string Long)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        
        
        
        ["app.log"] = ("日常", "本次运行的全部日志。所有模块、所有级别都写在这一个文件里，行首的 [模块名] 说明是谁写的"),
        ["crash.log"] = ("崩溃", "崩溃与页面加载失败的完整堆栈 —— 出问题时第一个该看它"),

        
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

    
    public static (string Short, string Long) Describe(string name) =>
        Known.TryGetValue(name, out var d) ? d : ("应用日志", "应用日志。");

    public static string SizeText(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1048576.0:0.#} MB",
    };

    

    private static Brush? _debugBrush, _infoBrush, _warningBrush, _errorBrush, _neutralBrush;

    public static Brush BrushOf(LogLevel level) => level switch
    {
        LogLevel.Debug => _debugBrush ??= Look("TextFillColorTertiaryBrush"),
        LogLevel.Warning => _warningBrush ??= Look("SystemFillColorCautionBrush"),
        LogLevel.Error => _errorBrush ??= Look("SystemFillColorCriticalBrush"),
        _ => _infoBrush ??= Look("TextFillColorSecondaryBrush"),
    };

    
    public static Brush NeutralBrush => _neutralBrush ??= Look("TextFillColorPrimaryBrush");

    
    
    
    
    
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

            
            if (char.IsWhiteSpace(line[0]))
            {
                if (list.Count > 0)
                {
                    var last = list[^1];
                    
                    
                    
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

            
            if (line[0] == '[')
            {
                var close = line.IndexOf(']');
                if (close > 0 && TryParseStamp(line[1..close], out var stamp, out var time))
                {
                    var rest = line[(close + 1)..].TrimStart();

                    
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

    
    
    
    
    public static string ToText(IEnumerable<LogEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries) sb.AppendLine(e.Raw.Length > 0 ? e.Raw : e.Message);
        return sb.ToString();
    }

    
    
    
    
    
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

    
    
    
    
    
    
    
    
    public static bool IsExtensionOf(IReadOnlyList<LogEntry> old, IReadOnlyList<LogEntry> fresh)
    {
        if (fresh.Count <= old.Count) return false;

        for (var i = 0; i < old.Count; i++)
            if (!string.Equals(old[i].Raw, fresh[i].Raw, StringComparison.Ordinal)) return false;

        return true;
    }

    
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

    
    private static bool TryParseStamp(string stamp, out string full, out string display)
    {
        full = stamp.Trim().Replace('T', ' ');
        display = full;
        var dot = display.IndexOf('.');
        if (dot > 0) display = display[..dot];

        if (display.Length >= 16 && char.IsDigit(display[0]) && display[4] == '-' && display[7] == '-')
            return true;

        
        if (IsClock(display)) return true;

        full = "";
        display = "";
        return false;
    }

    
    private static bool TryParseBareStamp(string line, out string time, out string rest)
    {
        time = "";
        rest = line;

        
        if (line.Length < 12) return false;
        if (!IsClock(line[..8])) return false;
        if (line[8] != '.') return false;
        if (!(char.IsDigit(line[9]) && char.IsDigit(line[10]) && char.IsDigit(line[11]))) return false;
        if (line.Length > 12 && !char.IsWhiteSpace(line[12])) return false;

        time = line[..12];
        rest = line.Length > 12 ? line[13..].TrimStart() : "";
        return true;
    }

    
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
