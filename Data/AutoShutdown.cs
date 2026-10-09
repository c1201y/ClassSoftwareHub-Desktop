using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace ClassSoftwareHub.Desktop.Data;

/// <summary>关机方式。</summary>
public static class ShutdownMethods
{
    /// <summary>调 Windows 自带的 <c>shutdown.exe</c> 直接关机，**可以配延迟**。</summary>
    public const string Shutdown = "shutdown";

    /// <summary>
    /// 调系统的 <c>SlideToShutDown.exe</c>，弹出那个"下滑关机"界面。
    /// ⚠️ 它**只负责弹界面**：用户把滑块拉到底才真关，不拉就一直停在那儿 —— 这是刻意选的"高级感"，
    ///    也意味着这种方式**没有"取消"的概念**（滑块自己就能取消），所以它不需要延迟。
    /// </summary>
    public const string Slide = "slide";

    /// <summary>界面上显示的名字。</summary>
    public static string DisplayName(string? method) => method switch
    {
        Slide => "滑动关机",
        _ => "直接关机",
    };
}

/// <summary>
/// 一个「关机点」：到点做什么、怎么做。
///
/// ⚠️ 与「程序专杀」的规则一样，**每个点各自带自己的配置**（时间 / 方式 / 延迟），
///    别把这些提到 <see cref="AutoShutdownConfig"/> 上做成全局的。
/// </summary>
public sealed class ShutdownPoint
{
    /// <summary>时间点，格式 <c>"HH:mm"</c>，每天到达该时刻触发一次。</summary>
    public string Time { get; set; } = "17:30";

    /// <summary><see cref="ShutdownMethods.Shutdown"/> 或 <see cref="ShutdownMethods.Slide"/>。</summary>
    public string Method { get; set; } = ShutdownMethods.Shutdown;

    /// <summary>延迟秒数。**只有「直接关机」用它**（滑动关机忽略）。0 = 立刻关机。</summary>
    public int DelaySeconds { get; set; } = 60;
}

/// <summary>
/// 「自动关机」的本地存档（%LOCALAPPDATA%\ClassSoftwareHub\autoshutdown.json）。
///
/// ⚠️ 跟两个专杀（<c>procguard.json</c> / <c>easinote-guard.json</c>）**各存各的** ——
///    这个文件将来整个不要了也能直接删干净，不影响那两条线。
/// </summary>
public sealed class AutoShutdownConfig
{
    public static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassSoftwareHub", "autoshutdown.json");

    /// <summary>
    /// 总开关。默认**关** —— 这是实验性功能，而且是**破坏性操作**（真会关机），
    /// 一律由用户自己在页面上打开（同两个专杀）。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 周一~周日各一组关机点。<c>Days[0]</c> = 周一 …… <c>Days[6]</c> = 周日。
    ///
    /// ⚠️ 用 `(int)DayOfWeek` 取星期会得到"周日=0"，**不能直接拿去索引**，
    ///    一律走 <see cref="DayIndexOf"/> 换算。
    /// </summary>
    public List<List<ShutdownPoint>> Days { get; set; } = NewDays();

    /// <summary>七天各自一个空清单。</summary>
    public static List<List<ShutdownPoint>> NewDays() =>
        Enumerable.Range(0, 7).Select(_ => new List<ShutdownPoint>()).ToList();

    /// <summary>今天该看第几组（周一=0 …… 周日=6）。</summary>
    public static int DayIndexOf(DateTime time) => ((int)time.DayOfWeek + 6) % 7;

    /// <summary>星期几的显示名（按上面那套索引）。</summary>
    public static readonly string[] DayNames = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };

    public static AutoShutdownConfig Load()
    {
        var cfg = new AutoShutdownConfig();
        try
        {
            if (File.Exists(StorePath))
                cfg = JsonSerializer.Deserialize<AutoShutdownConfig>(File.ReadAllText(StorePath))
                      ?? new AutoShutdownConfig();
        }
        catch { /* 存档损坏则退回默认值 */ }

        // 手改过的 json 里可能有缺天数、空元素、非法时间点 —— 顺手规整，免得巡检时白比对
        cfg.Days ??= NewDays();
        while (cfg.Days.Count < 7) cfg.Days.Add(new List<ShutdownPoint>());
        if (cfg.Days.Count > 7) cfg.Days = cfg.Days.Take(7).ToList();

        for (var i = 0; i < cfg.Days.Count; i++)
        {
            cfg.Days[i] ??= new List<ShutdownPoint>();
            cfg.Days[i].RemoveAll(p => p is null || !IsValidTime(p.Time));
            foreach (var p in cfg.Days[i]) p.Method = NormalizeMethod(p.Method);
        }
        return cfg;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(this));
        }
        catch { /* 存不上不影响使用 */ }
    }

    /// <summary>"HH:mm" 且真的解析得出来（挡掉 "25:99" 这种）。</summary>
    public static bool IsValidTime(string? s) =>
        !string.IsNullOrWhiteSpace(s)
        && TimeSpan.TryParseExact(s.Trim(), @"hh\:mm", null, out _);

    /// <summary>认不出的方式一律当「直接关机」（手改 json 写错时的兜底）。</summary>
    public static string NormalizeMethod(string? method) =>
        string.Equals(method, ShutdownMethods.Slide, StringComparison.OrdinalIgnoreCase)
            ? ShutdownMethods.Slide
            : ShutdownMethods.Shutdown;
}

/// <summary>
/// 「自动关机」的执行引擎（实验性功能，2026-10-10 Nick 提）。
///
/// 与两个专杀（<see cref="ProcessGuard"/> / <see cref="EasiNoteGuard"/>）同一套范式：
/// 靠本进程内的 <see cref="DispatcherQueueTimer"/> 巡检 —— **本程序没在跑就不会关**，
/// 没有注册 Windows 计划任务。（Nick 2026-09-28 定：不往系统里装计划任务。）
///
/// ⚠️ 与专杀的关键差别：这里**不用"当前 HH:mm 撞字符串"**判到点，而是算「今天的目标时刻」，
///    只要 <c>now</c> 落在目标时刻之后的 <see cref="LateWindow"/> 内就算命中。
///    原因：撞字符串时若那一分钟正好在睡眠/休眠就**永远错过**（17:30 睡着、17:31 醒来不会补），
///    而"放学自动关机"一旦漏掉 = 电脑整晚开着，正是这个功能要防的事。
///    容忍窗口又不能太长 —— 睡了两小时醒来才补关，反而会把正在用电脑的人坑进去。
///
/// ⚠️ 排下去的 <c>shutdown /s /t</c> **由 Windows 自己数秒**：这中间就算本程序被退掉，
///    到点仍然会关机（这是有意的 —— 用户要的就是"到点就关"）。
///    反悔的通道是侧边栏那颗「取消关机」按钮（走 <c>shutdown /a</c>）。
/// </summary>
public static class AutoShutdown
{
    /// <summary>巡检间隔（秒）。</summary>
    private const int TickSeconds = 20;

    /// <summary>
    /// 到点后还算数的容忍窗口。缩短它 = 更容易漏关；放大它 = 睡醒后补关更容易误伤正在用电脑的人。
    /// 3 分钟是"跨过一分钟的睡眠也能兜住，睡了一觉则不关"的折中。
    /// </summary>
    private static readonly TimeSpan LateWindow = TimeSpan.FromMinutes(3);

    /// <summary>延迟秒数的上限（一天）。防止手滑填个天文数字把机器永久排上关机。</summary>
    private const int MaxDelaySeconds = 86400;

    private static DispatcherQueueTimer? _timer;

    /// <summary>「日期 + 星期 + 第几个点」记账，同一次只跑一遍（20 秒一发，同一分钟会 tick 好几次）。</summary>
    private static readonly HashSet<string> _fired = new();

    /// <summary>启动定时巡检。应用启动时调一次，**必须在 UI 线程**。</summary>
    public static void Start()
    {
        if (_timer is not null) return;
        try
        {
            var dq = DispatcherQueue.GetForCurrentThread();
            if (dq is null) { Log("拿不到 DispatcherQueue，自动关机未启用"); return; }

            _timer = dq.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(TickSeconds);
            _timer.IsRepeating = true;
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
            Log($"自动关机巡检已启动（每 {TickSeconds} 秒看一次表）");
        }
        catch (Exception ex)
        {
            Log("启动自动关机巡检失败: " + ex.Message);
        }
    }

    private static void Tick()
    {
        try
        {
            var cfg = AutoShutdownConfig.Load();
            if (!cfg.Enabled) return;

            var now = DateTime.Now;
            var dayIndex = AutoShutdownConfig.DayIndexOf(now);
            var points = cfg.Days[dayIndex];
            if (points is null || points.Count == 0) return;

            // 每天清一次记账，免得集合无限长
            if (_fired.Count > 512) _fired.Clear();

            for (var i = 0; i < points.Count; i++)
            {
                var point = points[i];
                if (point is null) continue;
                if (!TimeSpan.TryParseExact(point.Time.Trim(), @"hh\:mm", null, out var span)) continue;

                var target = now.Date.Add(span);
                var late = now - target;
                if (late < TimeSpan.Zero || late > LateWindow) continue;   // 还没到 / 已过容忍窗口

                if (!_fired.Add($"{now:yyyy-MM-dd} {dayIndex} {i}")) continue;   // 这一分钟已经跑过
                Execute(point, target);
            }
        }
        catch (Exception ex)
        {
            Log("巡检出错: " + ex.Message);
        }
    }

    /// <summary>真的动手。</summary>
    private static void Execute(ShutdownPoint point, DateTime target)
    {
        try
        {
            if (AutoShutdownConfig.NormalizeMethod(point.Method) == ShutdownMethods.Slide)
            {
                var exe = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "SlideToShutDown.exe");
                if (!File.Exists(exe))
                {
                    Log($"[{target:HH:mm}] 找不到 {exe}，滑动关机没执行");
                    return;
                }

                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                Log($"[{target:HH:mm}] 已调起滑动关机");
                return;
            }

            var delay = Math.Clamp(point.DelaySeconds, 0, MaxDelaySeconds);
            var code = RunShutdown(
                $"/s /t {delay} /c \"ClassSoftwareHub：已到设定的关机时间\"", out var error);
            Log($"[{target:HH:mm}] 直接关机 delay={delay}s exit={code}"
                + (error.Length > 0 ? " " + error : ""));
        }
        catch (Exception ex)
        {
            Log("执行关机失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 取消待执行的关机（侧边栏那颗「取消关机」按钮调它）。
    /// </summary>
    /// <returns>要显示给用户的一句话 —— 侧边栏会拿它弹原生提示浮层，见 <c>TeachingActions.Begin</c>。</returns>
    public static string CancelPending()
    {
        var code = RunShutdown("/a", out var error);

        // 0 = 取消成功；1116 = 没有待中止的关机（点了但压根没排过关机时最常见的就是它）
        if (code == 0)
        {
            Log("已取消本次自动关机");
            return "已取消本次自动关机";
        }

        Log($"取消关机未成功 exit={code}" + (error.Length > 0 ? " " + error : ""));
        return "当前没有待执行的关机";
    }

    /// <summary>跑一次 shutdown.exe，返回进程退出码（起不来 / 超时返回 -1）。</summary>
    private static int RunShutdown(string arguments, out string error)
    {
        error = "";
        try
        {
            var exe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
            var psi = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null) { error = "起不来"; return -1; }

            // ⚠️ 这两个命令都是"登记一下就返回"（带上 /t 也不会等倒计时），实测毫秒级。
            //    超时给短一点：取消关机是侧边栏动作、跑在 UI 线程上，卡住就是整条边条僵住。
            if (!process.WaitForExit(2000))
            {
                error = "超时";
                return -1;
            }
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return -1;
        }
    }

    // ── 日志 ─────────────────────────────────────────────────

    public static void Log(string msg)
    {
        try
        {
            Core.AppLog.Info("autoshutdown", msg);
        }
        catch { /* 记不上不影响功能 */ }
    }
}
