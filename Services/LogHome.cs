using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>一次运行留下的会话目录（给日志页/设置页列表用的只读视图）。</summary>
public sealed record LogSessionInfo(
    string Name,
    string FullPath,
    DateTime StartedLocal,
    long Bytes,
    int FileCount,
    bool IsCurrent,
    bool IsLegacy);

/// <summary>
/// 会话化日志目录（Nick 2026-10-11 定案）：
/// <c>%LOCALAPPDATA%\ClassSoftwareHub\logs\{yyyy-MM-dd_HH-mm-ss}\</c>，例如 <c>logs\2026-10-11_00-13-41\</c>。
///
/// 为什么要这么切：以前是「一个模块一个 .log、每份文件存一辈子」——
/// 二十几个文件（tray / sidebar / theme / perf / memory…），而且越写越大（memory.log 单独就 2.6 MB），
/// 想查「昨天那次启动出了什么事」得在二十几个文件里各翻一遍。
///
/// 改成「**启动一次 = 一个目录**」之后：
///   · 一次运行的全部现场在同一个目录里，翻旧问题只要挑一个目录；
///   · 目录名就是启动时间，天然有序、天然唯一；
///   · 回收策略可以简单成「留最近 N 天」，跟缓存那套完全同构。
///
/// ⚠️ 与 <see cref="CacheHome"/> 的关键差别：**日志不复用目录**。
/// 缓存是「同一版本反复启动复用一个目录」（那是在清垃圾），而日志每次启动**必须是新目录** ——
/// 复用了就等于两次运行混在一起，「找出昨天那次」这件事就没了。所以这里只新建、不查找复用。
///
/// ⛔ 安全边界（这条绝不松）：
///  - 只认名字匹配会话格式或 <c>_legacy-{8位日期}</c> 的**目录**，其余一律不碰；
///  - 只在自家 <c>logs\</c> 里动手，删除前再校验一次「路径确实在 logs\ 之下」；
///  - **当前正在用的会话目录，任何情况下都不删**；
///  - 每一步异常都落日志，禁止空 catch。
/// </summary>
public static class LogHome
{
    /// <summary>日志根目录的文件夹名（数据根下）。</summary>
    public const string FolderName = "logs";

    /// <summary>会话目录名：<c>2026-10-11_00-13-41</c>（同一秒连开会缀 <c>-N</c>）。</summary>
    private static readonly Regex SessionPattern =
        new(@"^(\d{4})-(\d{2})-(\d{2})_(\d{2})-(\d{2})-(\d{2})(?:-(\d+))?$", RegexOptions.Compiled);

    /// <summary>升级前归档目录：<c>_legacy-20261011</c>。</summary>
    private const string LegacyPrefix = "_legacy-";
    private static readonly Regex LegacyPattern = new(@"^_legacy-(\d{8})$", RegexOptions.Compiled);
    private const string LegacyStampFormat = "yyyyMMdd";

    private const string StampFormat = "yyyy-MM-dd_HH-mm-ss";

    private static readonly object Gate = new();
    private static string? _sessionName;

    /// <summary>日志根目录：<c>%LOCALAPPDATA%\ClassSoftwareHub\logs</c>。所有会话目录都在这下面。</summary>
    public static string Root => Path.Combine(SettingsStore.Dir, FolderName);

    /// <summary>本次运行的会话目录名（如 <c>2026-10-11_00-13-41</c>）。取用即确保已初始化。</summary>
    public static string SessionName => Ensure();

    /// <summary>
    /// 本次运行的会话目录**是否已经落定**（不触发初始化）。
    ///
    /// ⚠️ 用途：区分「真的启动过」和「第二个实例敲门后自退」。
    ///    后者走 <c>Environment.Exit(0)</c>，同样会触发 <c>ProcessExit</c> ——
    ///    不加这道闸的话，用户每点一次图标就会多出一个只写了一行的空会话目录。
    /// </summary>
    public static bool Established => _sessionName is { Length: > 0 };

    /// <summary>本次运行的会话目录完整路径。初始化失败时退回 <c>logs\</c> 根（不至于让日志写不出去）。</summary>
    public static string SessionDir
    {
        get
        {
            var name = Ensure();
            return name.Length == 0 ? Root : Path.Combine(Root, name);
        }
    }

    /// <summary>
    /// 幂等：定下本次运行的会话目录（**只新建，绝不复用**）。返回目录名，初始化失败返回空串。
    /// 建完顺手写一条「启动横幅」当作本次日志的第一行。
    /// </summary>
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

                // 同一秒内连开两次（脚本连点）也别互相覆盖；正常情况下永远走不到这个循环
                while (Directory.Exists(Path.Combine(Root, name)) && n < 100)
                    name = baseName + "-" + n++;

                var dir = Path.Combine(Root, name);
                Directory.CreateDirectory(dir);

                _sessionName = name;
                WriteBanner(dir, name);
            }
            catch (Exception ex)
            {
                // ⚠️⚠️ 必须先落定再记日志：下面这句 AppLog.Info 会走 SessionDir → Ensure，
                //     如果这时 _sessionName 还是 null 就会递归回这里，写日志失败变成无限递归。
                _sessionName = "";
                AppLog.Info("logs", "初始化会话日志目录失败，退回 logs\\ 根：" + ex.Message);
            }

            return _sessionName;
        }
    }

    /// <summary>
    /// 本次运行的「启动横幅」，直接落盘（不走 <see cref="AppLog"/>，避免"写横幅又去要目录"的递归）。
    /// 排障第一眼要的东西全在这一段里：哪个版本、什么时候、什么系统、数据在哪。
    ///
    /// ⚠️ 每一行都要带**标准行格式**（<c>[时间] [级别] [模块] 内容</c>）—— 裸文本行在日志页里
    ///    会因为没有时间戳而被当成"独立条目"，模块列还会退化成文件名（2026-10-11 实测过）。
    /// </summary>
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
        catch { /* 横幅写不进去不影响日志本身 */ }
    }

    /// <summary>
    /// 启动时的**用户配置快照**（2026-10-11 加，Nick 问"日志够吗"之后补的）。
    ///
    /// 为什么要有：很多问题只在**特定设置组合**下出现（关闭到托盘 / 分体主题 / 走自建节点 /
    /// 跳过过某个版本 / 侧边栏常驻…），而事后问用户"你当时是怎么设的"十次有九次问不明白。
    /// 与其问，不如每次启动自己记一份。
    ///
    /// ⚠️ 整段包 try/catch：读设置失败只是少几行，**横幅本身不能跟着丢**。
    /// ⚠️ 取值一律"人话 + 原值"（<c>开/关</c>、模式名），别直接 ToString 布尔值 ——
    ///    这份快照是排障时给人看的，不是给程序读的。
    /// </summary>
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

    /// <summary>列出 <c>logs\</c> 下所有会话目录（含升级前归档），新的在前。当前会话恒排第一。</summary>
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
                // 目录名里六段就是本地时间，直接拼（别用 Parse，会受当前区域性影响）
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

        // 当前会话永远排最前（其余按时间倒序）
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
                catch { /* 单个文件读不到就跳过，不影响整目录统计 */ }
            }
        }
        catch (Exception ex)
        {
            AppLog.Info("logs", $"统计会话目录失败（{name}）：{ex.Message}");
        }
        return (bytes, count);
    }

    /// <summary>
    /// 按「保留天数」回收：删掉启动时间早于 <c>今天 - keepDays</c> 的会话目录。
    /// <paramref name="keepDays"/> &lt;= 0 表示**永不删除**（用户选的那一项）。
    /// 当前正在用的会话**永不删**。返回删掉的目录个数。
    /// </summary>
    public static int Clean(int keepDays)
    {
        if (keepDays <= 0) return 0;      // 永不删除

        var current = Ensure();
        if (current.Length == 0) return 0;    // 目录都没建起来 ⇒ 不动手（安全第一）

        var deadline = DateTime.Now.AddDays(-keepDays);
        var removed = 0;

        foreach (var s in Scan())
        {
            if (s.IsCurrent) continue;                       // 当前会话永不删
            if (s.StartedLocal >= deadline) continue;         // 还没到期
            if (!IsInsideRoot(s.FullPath)) continue;          // 安全边界：必须在 logs\ 之下

            try
            {
                Directory.Delete(s.FullPath, recursive: true);
                removed++;
            }
            catch (Exception ex)
            {
                // 被占用（日志页正读着 / 杀软扫着）属正常：跳过，下次启动再试
                AppLog.Info("logs", $"删除会话日志目录失败（{s.Name}）：{ex.Message}");
            }
        }

        return removed;
    }

    /// <summary>删掉指定的一個会话目录（设置页「删除」用）。当前会话、非法名字、越界路径一律拒绝。</summary>
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

    /// <summary>全部会话合计占用（字节数与文件数）。</summary>
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

    /// <summary>
    /// 应用启动时挂后台清一轮（不阻塞首帧）：按「日志保留天数」回收旧会话目录。
    /// </summary>
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

    /// <summary>
    /// 向下兼容：上一版的日志是「按模块平铺在 <c>logs\</c> 根下」的（tray.log / theme.log …）。
    /// 这里把它们整体挪进 <c>logs\_legacy-{yyyyMMdd}\</c>，让 <c>logs\</c> 根下只剩会话目录。
    /// 幂等：根下没有 <c>*.log</c> 就什么都不做（跑过一次之后永远为空）。
    /// </summary>
    public static void MigrateLegacy()
    {
        try
        {
            var root = Root;
            if (!Directory.Exists(root)) return;

            var strays = Directory.GetFiles(root, "*.log");     // 只扫根，不递归
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

    /// <summary>目标路径确实落在 <c>logs\</c> 之下（删除前的最后一道闸）。</summary>
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
