using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;


public sealed record LogSessionInfo(
    string Name,
    string FullPath,
    DateTime StartedLocal,
    long Bytes,
    int FileCount,
    bool IsCurrent,
    bool IsLegacy);
























public static class LogHome
{
    
    public const string FolderName = "logs";

    
    private static readonly Regex SessionPattern =
        new(@"^(\d{4})-(\d{2})-(\d{2})_(\d{2})-(\d{2})-(\d{2})(?:-(\d+))?$", RegexOptions.Compiled);

    
    private const string LegacyPrefix = "_legacy-";
    private static readonly Regex LegacyPattern = new(@"^_legacy-(\d{8})$", RegexOptions.Compiled);
    private const string LegacyStampFormat = "yyyyMMdd";

    private const string StampFormat = "yyyy-MM-dd_HH-mm-ss";

    private static readonly object Gate = new();
    private static string? _sessionName;

    
    public static string Root => Path.Combine(SettingsStore.Dir, FolderName);

    
    public static string SessionName => Ensure();

    
    
    
    
    
    
    
    public static bool Established => _sessionName is { Length: > 0 };

    
    public static string SessionDir
    {
        get
        {
            var name = Ensure();
            return name.Length == 0 ? Root : Path.Combine(Root, name);
        }
    }

    
    
    
    
    public static string Ensure()
    {
        if (_sessionName is not null) return _sessionName;

        lock (Gate)
        {
            if (_sessionName is not null) return _sessionName;

            try
            {
                Directory.CreateDirectory(Root);

                var baseName = DateTime.Now.ToString(StampFormat, CultureInfo.InvariantCulture);
                var name = baseName;
                var n = 1;

                
                while (Directory.Exists(Path.Combine(Root, name)) && n < 100)
                    name = baseName + "-" + n++;

                var dir = Path.Combine(Root, name);
                Directory.CreateDirectory(dir);

                _sessionName = name;
                WriteBanner(dir, name);
            }
            catch (Exception ex)
            {
                
                
                _sessionName = "";
                AppLog.Info("logs", "初始化会话日志目录失败，退回 logs\\ 根：" + ex.Message);
            }

            return _sessionName;
        }
    }

    
    
    
    
    
    
    
    private static void WriteBanner(string dir, string sessionName)
    {
        try
        {
            var now = DateTime.Now;
            var stamp = now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

            var lines = new List<string>
            {
                "════════════════ 应用启动 ════════════════",
                "版本      : " + ShellConfig.ShellVersion,
                "启动时间  : " + now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                "系统      : " + Environment.OSVersion.VersionString
                                + (Environment.Is64BitOperatingSystem ? " 64 位" : " 32 位")
                                + (Environment.Is64BitProcess ? "（进程 64 位）" : "（进程 32 位）"),
                "显示      : " + SystemInfo.DisplaySummary(),
                "数据目录  : " + SettingsStore.Dir,
                "会话目录  : " + sessionName,
            };

            lines.AddRange(SettingsSnapshot());
            lines.Add("═════════════════════════════════════════");

            var sb = new StringBuilder();
            foreach (var line in lines)
                sb.Append('[').Append(stamp).Append("] [信息] [startup] ").Append(line).Append("\r\n");

            File.WriteAllText(Path.Combine(dir, "app.log"), sb.ToString(), new UTF8Encoding(false));
        }
        catch {  }
    }

    
    
    
    
    
    
    
    
    
    
    
    private static IEnumerable<string> SettingsSnapshot()
    {
        try
        {
            var s = App.Settings.Current;

            Func<bool, string> on = v => v ? "开" : "关";
            Func<string?, string> or = v => string.IsNullOrWhiteSpace(v) ? "—" : v!;

            return new[]
            {
                "外观      : 主题=" + or(s.Theme) + "／背景=" + or(s.Backdrop)
                              + "／分体主题=" + on(s.SplitTheme) + "／外部分体=" + or(s.ExternalTheme)
                              + "／置顶=" + on(s.AlwaysOnTop),
                "行为      : 关闭到托盘=" + on(s.CloseToTray) + "／开机自启=" + on(s.AutoStart)
                              + "／最小化启动=" + on(s.MinimizeOnStart) + "／遥测=" + on(s.TelemetryEnabled),
                "侧边栏    : 启用=" + on(s.SidebarEnabled) + "／贴边=" + or(s.SidebarEdge)
                              + "／模式=" + or(s.SidebarMode) + "／常驻=" + on(s.SidebarPinned)
                              + "／模块=" + (s.SidebarModuleIds is { Length: > 0 } ids ? string.Join(",", ids) : "—"),
                "网络      : 下载路径=" + GithubRoute.DisplayName(s.GithubDownloadRoute)
                              + "／更新通道=" + or(s.UpdateChannel)
                              + "／自动检查更新=" + on(s.AutoCheckUpdate)
                              + "／跳过版本=" + or(s.SkipUpdateVersion),
                "保留策略  : 日志=" + KeepDays(s.LogKeepDays) + "／缓存=" + s.CacheKeepCount
                              + "／安装包=" + s.InstallerKeepCount,
                "窗口      : " + s.WindowWidth + "x" + s.WindowHeight + "／卡片=" + or(s.AppCardView)
                              + "／网页缩放=" + s.WebZoom.ToString("0.##", CultureInfo.InvariantCulture),
            };
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string KeepDays(int d) => d <= 0 ? "永不删除" : d + " 天";

    
    public static IReadOnlyList<LogSessionInfo> Scan()
    {
        var root = Root;
        if (!Directory.Exists(root)) return Array.Empty<LogSessionInfo>();

        var current = Ensure();
        var list = new List<LogSessionInfo>();

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            var legacy = false;
            DateTime started;

            var m = SessionPattern.Match(name);
            if (m.Success)
            {
                
                if (!DateTime.TryParseExact(
                        $"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value} "
                        + $"{m.Groups[4].Value}:{m.Groups[5].Value}:{m.Groups[6].Value}",
                        "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out started))
                    continue;
            }
            else
            {
                var lm = LegacyPattern.Match(name);
                if (!lm.Success) continue;
                legacy = true;
                if (!DateTime.TryParseExact(lm.Groups[1].Value, LegacyStampFormat,
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out started))
                    continue;
            }

            var (bytes, count) = MeasureDir(dir, name);

            list.Add(new LogSessionInfo(
                name, dir, started, bytes, count,
                !legacy && string.Equals(name, current, StringComparison.OrdinalIgnoreCase),
                legacy));
        }

        
        return list
            .OrderByDescending(x => x.IsCurrent)
            .ThenByDescending(x => x.StartedLocal)
            .ToList();
    }

    private static (long Bytes, int Files) MeasureDir(string dir, string name)
    {
        long bytes = 0;
        var count = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { bytes += new FileInfo(file).Length; count++; }
                catch {  }
            }
        }
        catch (Exception ex)
        {
            AppLog.Info("logs", $"统计会话目录失败（{name}）：{ex.Message}");
        }
        return (bytes, count);
    }

    
    
    
    
    
    public static int Clean(int keepDays)
    {
        if (keepDays <= 0) return 0;      

        var current = Ensure();
        if (current.Length == 0) return 0;    

        var deadline = DateTime.Now.AddDays(-keepDays);
        var removed = 0;

        foreach (var s in Scan())
        {
            if (s.IsCurrent) continue;                       
            if (s.StartedLocal >= deadline) continue;         
            if (!IsInsideRoot(s.FullPath)) continue;          

            try
            {
                Directory.Delete(s.FullPath, recursive: true);
                removed++;
            }
            catch (Exception ex)
            {
                
                AppLog.Info("logs", $"删除会话日志目录失败（{s.Name}）：{ex.Message}");
            }
        }

        return removed;
    }

    
    public static bool TryDelete(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (!SessionPattern.IsMatch(name) && !LegacyPattern.IsMatch(name)) return false;
        if (string.Equals(name, Ensure(), StringComparison.OrdinalIgnoreCase)) return false;

        var path = Path.Combine(Root, name);
        if (!IsInsideRoot(path)) return false;

        try
        {
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Info("logs", $"删除会话日志目录失败（{name}）：{ex.Message}");
            return false;
        }
    }

    
    public static (long Bytes, int Files) Measure()
    {
        long bytes = 0;
        var files = 0;
        foreach (var s in Scan())
        {
            bytes += s.Bytes;
            files += s.FileCount;
        }
        return (bytes, files);
    }

    
    
    
    public static void Start() => System.Threading.Tasks.Task.Run(() =>
    {
        try
        {
            var days = App.Settings.Current.LogKeepDays;
            var removed = Clean(days);
            if (removed > 0)
                AppLog.Info("logs", days <= 0
                    ? $"已清理 {removed} 个旧会话日志目录"
                    : $"已清理 {removed} 个旧会话日志目录（保留最近 {days} 天）");
        }
        catch (Exception ex)
        {
            AppLog.Info("logs", "日志清理失败: " + ex.Message);
        }
    });

    
    
    
    
    
    public static void MigrateLegacy()
    {
        try
        {
            var root = Root;
            if (!Directory.Exists(root)) return;

            var strays = Directory.GetFiles(root, "*.log");     
            if (strays.Length == 0) return;

            var dir = Path.Combine(root, LegacyPrefix + DateTime.Now.ToString(LegacyStampFormat, CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);

            var moved = 0;
            foreach (var file in strays)
            {
                try
                {
                    var target = Path.Combine(dir, Path.GetFileName(file));
                    if (File.Exists(target))
                        target = Path.Combine(dir, Path.GetFileNameWithoutExtension(file)
                                                   + "-" + Guid.NewGuid().ToString("N")[..6]
                                                   + Path.GetExtension(file));
                    File.Move(file, target);
                    moved++;
                }
                catch (Exception ex)
                {
                    AppLog.Info("logs", $"归档旧日志失败（{Path.GetFileName(file)}）：{ex.Message}");
                }
            }

            if (moved > 0)
                AppLog.Info("logs", $"已把 {moved} 个上一版的模块日志归档到 {Path.GetFileName(dir)}\\");
        }
        catch (Exception ex)
        {
            AppLog.Info("logs", "归档旧日志失败：" + ex.Message);
        }
    }

    
    private static bool IsInsideRoot(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
