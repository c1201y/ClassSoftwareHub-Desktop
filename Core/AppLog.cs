using System;
using System.IO;
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
/// 统一日志入口（Nick 2026-10-02：日志页要能像 ClassIsland 那样按级别/时间分层筛选，
/// 而旧做法是十几个模块各自 <c>File.AppendAllText</c>，格式只有时间没有级别）。
///
/// 三条约定：
///   ① **目录**：日志一律写 <c>%LOCALAPPDATA%\ClassSoftwareHub\logs\</c> 子目录 ——
///      根目录留给你真正会手动翻的东西（settings.json、图片缓存另在 cache\），
///      不要再让日志跟截图、图标、配置混在一起（Nick：杂糅不优雅）。
///   ② **格式**：<c>[2026-10-02 14:00:00.123] [级别] 消息</c>。多行消息（如异常堆栈）
///      只有首行带前缀，后续行原样 —— 查看页把「不以 [ 开头的行」归并到上一条。
///   ③ **模块名**：就是文件名去掉 .log（tray / sidebar / vkbd …），查看页的用途字典按它对。
///
/// 迁移路径：各模块现有的 <c>Log(string)</c> 方法体改成转调这里，**调用点零改动**。
/// 老格式（无级别）的行由查看页兜底解析，所以新旧行可以共存于同一份历史文件里。
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static bool _migrated;

    /// <summary>日志目录。⚠️ 是根目录下的 <c>logs</c> 子目录，别再把文件写到根上了。</summary>
    public static string Dir => Path.Combine(Services.SettingsStore.Dir, "logs");

    public static void Debug(string module, string message) => Write(module, LogLevel.Debug, message);
    public static void Info(string module, string message) => Write(module, LogLevel.Info, message);
    public static void Warning(string module, string message) => Write(module, LogLevel.Warning, message);
    public static void Error(string module, string message) => Write(module, LogLevel.Error, message);

    /// <summary>写一条日志。写不进去就静默放弃 —— 日志自己绝不能把功能搞挂。</summary>
    public static void Write(string module, LogLevel level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                File.AppendAllText(
                    Path.Combine(Dir, module + ".log"),
                    $"[{stamp}] [{TagOf(level)}] {message}\r\n",
                    Encoding.UTF8);
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

    /// <summary>
    /// 旧版把所有 .log 直接堆在根目录。搬进 <c>logs\</c> —— 只在**目标不存在**时移动，
    /// 绝不覆盖；个别文件被占用就留在原地（不影响任何功能，就是没那么整洁）。
    /// 应用启动早期调一次即可，内部保证只跑一遍。
    /// </summary>
    public static void MigrateLegacyFiles()
    {
        if (_migrated) return;
        _migrated = true;

        try
        {
            var root = Services.SettingsStore.Dir;
            if (!Directory.Exists(root)) return;

            foreach (var file in Directory.GetFiles(root, "*.log"))
            {
                try
                {
                    var target = Path.Combine(Dir, Path.GetFileName(file));
                    if (File.Exists(target)) continue;
                    Directory.CreateDirectory(Dir);
                    File.Move(file, target);
                }
                catch { /* 被占用 / 权限 —— 留在原地 */ }
            }
        }
        catch { }
    }
}
