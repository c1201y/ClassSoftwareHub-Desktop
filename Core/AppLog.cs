using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>日志级别。中文标签直接用于日志行与查看页的筛选。</summary>
public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// 统一日志入口。
///
/// 历史（Nick 2026-10-02）：旧做法是十几个模块各自 <c>File.AppendAllText</c>，
/// 格式只有时间没有级别。当时把入口收敛到这里，但**出口仍是「一个模块一个 .log」**。
///
/// 现状（Nick 2026-10-11 重构）：出口也统一了 ——
///   · 目录：<c>%LOCALAPPDATA%\ClassSoftwareHub\logs\{本次启动时间}\</c>（见 <see cref="Services.LogHome"/>），
///     一次运行一个目录，目录名就是启动时间；
///   · 文件：**只有两个** ——
///       <c>app.log</c>    日常全部日志（所有模块、所有级别都进这里）；
///       <c>crash.log</c>  崩溃与页面加载失败（模块名传 <c>crash</c> 的走这里）。
///     为什么崩溃单独一个文件：它要在「应用还没启动完」和「崩溃那一瞬间」都写得进去，
///     跟主日志合流反而更脆；而且用户出问题时只被要求发这一个文件。
///   · 行格式：<c>[时间] [级别] [模块] 消息</c>。模块名（tray / sidebar / ui …）从"文件名"
///     挪到了"行内标签"，信息一点没丢，但二十几个文件塌缩成两个。
///
/// 迁移路径：调用点**零改动** —— <c>AppLog.Info("tray", "…")</c> 照旧写。
///
/// ⚠️ 模块名要实事求是：缓存自己（<see cref="Services.CacheHome"/>）、日志自己
/// （<see cref="Services.LogHome"/>）、旧结构清理（<see cref="Services.LegacyCleanup"/>）
/// 都直接写 <c>AppLog.Info("cache"/"logs"/"legacy", …)</c>，
/// ⛔ 不要图省事走 <c>ScreenCapture.Log</c> —— 那个会把模块名标成 <c>snip</c>（截屏模块），
/// 于是在日志页的「模块」列里，一条缓存清理记录看起来像是截屏写的。
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();

    /// <summary>崩溃专用日志文件名（模块名传 <c>crash</c> 时进这个）。</summary>
    private const string CrashFile = "crash.log";

    /// <summary>日常日志文件名（其余所有模块都进这个）。</summary>
    private const string AppFile = "app.log";

    /// <summary>日志根目录：<c>%LOCALAPPDATA%\ClassSoftwareHub\logs</c>。下面按「启动一次一个目录」分层。</summary>
    public static string Root => Services.LogHome.Root;

    /// <summary>本次运行的会话目录（日志就写在它里面）。⚠️ 不再是「一堆模块文件的平铺目录」。</summary>
    public static string Dir => Services.LogHome.SessionDir;

    public static void Debug(string module, string message) => Write(module, LogLevel.Debug, message);
    public static void Info(string module, string message) => Write(module, LogLevel.Info, message);
    public static void Warning(string module, string message) => Write(module, LogLevel.Warning, message);
    public static void Error(string module, string message) => Write(module, LogLevel.Error, message);

    // ══════════════════ 异常与「操作链路」（2026-10-11 补）══════════════════
    //
    // 起因：Nick 问「这些日志够吗」—— 审计结果是 **17 个会话里警告 0 条、错误 0 条**。
    // 全仓库 724 个 catch、269 个是空的 `catch {}`，而记日志的调用点只有 15 个。
    // 也就是**约 98% 的异常路径是静默的**：某个功能坏了，日志里一个字都没有。
    //
    // 所以补两样东西：
    //   ① <see cref="Exception(string, Exception, string?)"/> —— 所有 catch 块的**统一出口**，
    //      一次调用带全「类型 / HResult / 消息 / 堆栈 / 内部异常 / 当时页面」；
    //   ② <see cref="Begin"/> / <see cref="Step"/> / <see cref="Done"/> / <see cref="Fail"/> ——
    //      「操作链路」记账（下载、更新、同步、提交走的就是这套），让日志从
    //      "发生了某件事" 变成 "某件事走到了哪一步、结果如何、花了多久"。
    //
    // ⚠️ 级别分工（这是"既要全、又别吵"的折中，别随手改）：
    //    · 主干步骤 → <c>信息</c>（默认就看得见）
    //    · 高频读数（内存回收之类）→ <c>调试</c>（日志页点掉「调试」就没它）
    //    · 真的出岔子 → <c>警告</c> / <c>错误</c>（**全量记，不节流**）

    /// <summary>
    /// 由 <c>App</c> 在启动时注入：返回"当时在哪一页、窗口多大"这类现场描述。
    /// ⛔ 做成委托而不是直接调 <c>Pages.ShellPage</c> —— Core 层不该反向依赖 Pages 层。
    /// </summary>
    public static Func<string>? ContextProvider { get; set; }

    /// <summary>
    /// 记一条异常。**所有 catch 块的统一出口**（别再写裸的 <c>AppLog.Error(mod, ex.Message)</c>：
    /// 那样没有类型、没有 HResult、没有堆栈，等于没记）。
    ///
    /// <paramref name="what"/> 写"当时在干什么"，人话，例如 <c>下载「微信」</c>、<c>读取设置</c>。
    /// 整个异常（含堆栈与内部异常）会**缩进后拼在同一条日志里** ——
    /// 日志页的解析规则是"缩进行归并到上一条"，所以它在列表里是一行、在详细视图里是全貌。
    /// </summary>
    public static void Exception(string module, Exception ex, string? what = null)
    {
        try
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(what)) sb.Append(what!.Trim()).Append('｜');
            sb.Append(ex.GetType().Name)
              .Append(" 0x").Append(ex.HResult.ToString("X8"))
              .Append(": ").Append(ex.Message);

            var where = SafeContext();
            if (where.Length > 0) sb.Append("\n  · 当时：").Append(where);

            sb.Append('\n').Append(Indent(ex.ToString()));
            Write(module, LogLevel.Error, sb.ToString());
        }
        catch { /* 记日志这件事本身绝不能把调用方搞挂 */ }
    }

    /// <summary>链路开始（<c>▶</c>）。<paramref name="what"/> 里请带上关键入参（链接、版本号、文件名…）。</summary>
    public static void Begin(string module, string what) => Info(module, "▶ " + what);

    /// <summary>链路中间步骤（<c>·</c>）。一步一步写清楚，出问题时才知道断在哪。</summary>
    public static void Step(string module, string what) => Info(module, "  · " + what);

    /// <summary>链路成功收尾（<c>✔</c>），带耗时。</summary>
    public static void Done(string module, string what, TimeSpan? elapsed = null)
        => Info(module, "✔ " + what + (elapsed is { } t ? " · 耗时 " + Secs(t) : ""));

    /// <summary>链路失败收尾（<c>✘</c>，带完整异常）。</summary>
    public static void Fail(string module, Exception ex, string what) => Exception(module, ex, "✘ " + what);

    /// <summary>链路失败收尾（没有异常对象时用这个，例如服务端返回了一个失败结果）。</summary>
    public static void Fail(string module, string what)
        => Write(module, LogLevel.Error, "✘ " + what);

    /// <summary>
    /// 告警：**能用但已经不对了**（降级、回退、重试、跳过校验…）。
    /// 与 <see cref="Error"/> 的分界 = 功能还能不能正常用。
    /// </summary>
    public static void Warn(string module, string what) => Warning(module, "⚠ " + what);

    /// <summary>耗时可读化：不到 1 秒给毫秒，超过给秒。</summary>
    public static string Secs(TimeSpan t) =>
        t.TotalSeconds < 1 ? $"{t.TotalMilliseconds:0}ms" : $"{t.TotalSeconds:0.##}s";

    /// <summary>字节可读化（日志里写"12.3 MB"比写 12897484 好读）。</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 0 => "未知",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.##} GB",
    };

    private static string SafeContext()
    {
        try { return ContextProvider?.Invoke() ?? ""; }
        catch { return ""; }
    }

    /// <summary>整个异常文本每行都加两个空格 —— 这样日志页会把它归并成**同一条**（见类注释）。</summary>
    private static string Indent(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join('\n', lines.Select(l => "  " + l.TrimEnd()));
    }

    /// <summary>
    /// 写一条日志。写不进去就静默放弃 —— 日志自己绝不能把功能搞挂。
    /// </summary>
    public static void Write(string module, LogLevel level, string message)
    {
        try
        {
            var target = string.Equals(module, "crash", StringComparison.OrdinalIgnoreCase) ? CrashFile : AppFile;

            lock (Gate)
            {
                var dir = Dir;
                Directory.CreateDirectory(dir);
                var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                File.AppendAllText(
                    Path.Combine(dir, target),
                    $"[{stamp}] [{TagOf(level)}] [{module}] {message}\r\n",
                    new UTF8Encoding(false));
            }
        }
        catch { }
    }

    private static string TagOf(LogLevel level) => level switch
    {
        LogLevel.Debug => "调试",
        LogLevel.Warning => "警告",
        LogLevel.Error => "错误",
        _ => "信息",
    };
}
