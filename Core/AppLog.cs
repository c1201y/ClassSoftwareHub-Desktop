using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ClassSoftwareHub.Desktop.Core;


public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}


























public static class AppLog
{
    private static readonly object Gate = new();

    
    private const string CrashFile = "crash.log";

    
    private const string AppFile = "app.log";

    
    public static string Root => Services.LogHome.Root;

    
    public static string Dir => Services.LogHome.SessionDir;

    public static void Debug(string module, string message) => Write(module, LogLevel.Debug, message);
    public static void Info(string module, string message) => Write(module, LogLevel.Info, message);
    public static void Warning(string module, string message) => Write(module, LogLevel.Warning, message);
    public static void Error(string module, string message) => Write(module, LogLevel.Error, message);

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    

    
    
    
    
    public static Func<string>? ContextProvider { get; set; }

    
    
    
    
    
    
    
    
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
        catch {  }
    }

    
    public static void Begin(string module, string what) => Info(module, "▶ " + what);

    
    public static void Step(string module, string what) => Info(module, "  · " + what);

    
    public static void Done(string module, string what, TimeSpan? elapsed = null)
        => Info(module, "✔ " + what + (elapsed is { } t ? " · 耗时 " + Secs(t) : ""));

    
    public static void Fail(string module, Exception ex, string what) => Exception(module, ex, "✘ " + what);

    
    public static void Fail(string module, string what)
        => Write(module, LogLevel.Error, "✘ " + what);

    
    
    
    
    public static void Warn(string module, string what) => Warning(module, "⚠ " + what);

    
    public static string Secs(TimeSpan t) =>
        t.TotalSeconds < 1 ? $"{t.TotalMilliseconds:0}ms" : $"{t.TotalSeconds:0.##}s";

    
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

    
    private static string Indent(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join('\n', lines.Select(l => "  " + l.TrimEnd()));
    }

    
    
    
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
