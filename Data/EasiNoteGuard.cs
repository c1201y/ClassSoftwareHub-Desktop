using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace ClassSoftwareHub.Desktop.Data;

/// <summary>
/// 希沃白板5「后台空跑」进程的到点查杀（实验性功能，2026-09-28 Nick 提）。
///
/// 背景：白板5 点了「退出」有时进程并不真退，留在任务管理器的「后台进程」里
///       （它本意是让下次启动更快）。残留进程占着单实例互斥量，
///       再点桌面图标就「打不开」—— 得手动去任务管理器结束掉才行。
///
/// 做法：到用户设定的时间点扫一遍，**只杀「没有任何可见窗口」的那些**；
///       还有窗口的（= 老师正在用）一律放过。
///
/// ⛔ 两条硬边界，改之前先想清楚：
///   ① **只匹配白板5 家族**（进程名去扩展名后以 <see cref="NamePrefix"/> 开头）。
///      不碰 `EasiAgent`（学校集控端）和 `EasiUpdate*`（更新组件）—— 杀它们会影响集控管理。
///   ② **有可见窗口就不杀**。这是整个功能唯一的安全闸：判定「在不在用」只看有没有可见顶层窗口。
///
/// ⚠️ 定时靠本进程内的 <see cref="DispatcherQueueTimer"/> —— **本程序没在跑就不会查杀**
///    （2026-09-28 Nick 确认「跟随本程序」即可）。**没有**注册 Windows 计划任务，不去动系统里任何东西。
/// </summary>
public static class EasiNoteGuard
{
    /// <summary>进程名（去扩展名）以它开头的都算白板5 家族：EasiNote / EasiNote.Mirror / EasiNote5 …</summary>
    public const string NamePrefix = "EasiNote";

    /// <summary>巡检间隔（秒）。到点靠它命中「当前 HH:mm」，所以别设得比 60 秒还大。</summary>
    private const int TickSeconds = 20;

    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassSoftwareHub", "easiguard.log");

    // ── 扫描 ─────────────────────────────────────────────────

    /// <summary>一个白板5 进程的现状。</summary>
    public sealed class GuardProc
    {
        public int Pid { get; init; }
        public string Name { get; init; } = "";
        public string Path { get; init; } = "";

        /// <summary>有可见的顶层窗口（= 老师正在用，不许杀）。</summary>
        public bool HasWindow { get; set; }

        public override string ToString() => $"{Name}({Pid})";
    }

    /// <summary>扫一遍当前的白板5 进程。**只看不动**。</summary>
    public static List<GuardProc> Scan()
    {
        var list = new List<GuardProc>();
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (Exception ex) { Log("枚举进程失败: " + ex.Message); return list; }

        foreach (var p in all)
        {
            try
            {
                if (!IsTarget(p.ProcessName)) continue;

                var path = "";
                try { path = p.MainModule?.FileName ?? ""; } catch { /* 权限不够就是空 */ }

                list.Add(new GuardProc
                {
                    Pid = p.Id,
                    Name = p.ProcessName,
                    Path = path,
                    HasWindow = HasVisibleWindow(p.Id)
                });
            }
            catch { /* 单个进程读不到就跳过，别让整轮扫描失败 */ }
            finally { p.Dispose(); }
        }
        return list;
    }

    /// <summary>是不是白板5 家族的进程。</summary>
    private static bool IsTarget(string processName) =>
        !string.IsNullOrEmpty(processName)
        && processName.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 这个进程有没有**可见的顶层窗口**。
    ///
    /// 判定口径（别改成 Process.MainWindowHandle != 0）：白板5 是多进程的，
    /// 主界面窗口未必挂在被扫到的那个 pid 上；而 MainWindowHandle 对很多后台进程
    /// 会返回 0 或过期值。直接枚举所有顶层窗口、按 pid 归属、看 IsWindowVisible 最实在 ——
    /// 隐藏的消息窗口 IsWindowVisible 本身就是 false，天然被排除。
    /// </summary>
    private static bool HasVisibleWindow(int pid)
    {
        var found = false;
        try
        {
            _ = EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var wpid);
                if (wpid != pid) return true;
                if (IsWindowVisible(hwnd)) { found = true; return false; }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return found;
    }

    // ── 执行一次 ─────────────────────────────────────────────

    /// <summary>一次查杀的结果。</summary>
    public sealed class GuardResult
    {
        public bool DryRun { get; init; }
        public int Killed { get; set; }
        public int Skipped { get; set; }
        public List<string> KilledNames { get; } = new();
        public List<string> SkippedNames { get; } = new();
        public List<string> Errors { get; } = new();

        /// <summary>给界面 / 日志用的一句话。</summary>
        public string Summary
        {
            get
            {
                if (Killed == 0 && Skipped == 0 && Errors.Count == 0)
                    return "没找到希沃白板5 的进程。";

                var parts = new List<string>();
                if (Killed > 0) parts.Add($"{(DryRun ? "会清掉" : "已清掉")} {Killed} 个");
                if (Skipped > 0) parts.Add($"放过 {Skipped} 个（有窗口，正在用）");
                if (Errors.Count > 0) parts.Add($"{Errors.Count} 个没能结束");
                return string.Join("；", parts) + "。";
            }
        }
    }

    /// <param name="dryRun">true = 只报告要动谁，**真的一个都不杀**（页面上的「只检查」走这条）。</param>
    public static GuardResult RunOnce(bool dryRun)
    {
        var r = new GuardResult { DryRun = dryRun };

        foreach (var p in Scan())
        {
            if (p.HasWindow)                       // 安全闸：有窗口 = 在用的，放过
            {
                r.Skipped++;
                r.SkippedNames.Add($"{p.Name}（PID {p.Pid}）");
                continue;
            }

            if (dryRun)
            {
                r.Killed++;
                r.KilledNames.Add($"{p.Name}（PID {p.Pid}）");
                continue;
            }

            try
            {
                using var proc = Process.GetProcessById(p.Pid);
                proc.Kill();
                r.Killed++;
                r.KilledNames.Add($"{p.Name}（PID {p.Pid}）");
            }
            catch (Exception ex)
            {
                // 最常见的就是权限不够（白板5 以管理员身份跑，我们不是）
                r.Errors.Add($"{p.Name}（PID {p.Pid}）：{ex.Message}");
            }
        }
        return r;
    }

    // ── 定时巡检 ─────────────────────────────────────────────

    private static DispatcherQueueTimer? _timer;

    /// <summary>「日期 + 时刻」记账，同一次只跑一遍（20 秒一发，同一分钟会 tick 好几次）。</summary>
    private static string _lastFiredKey = "";

    /// <summary>启动定时巡检。应用启动时调一次，**必须在 UI 线程**。</summary>
    public static void Start()
    {
        if (_timer is not null) return;
        try
        {
            var dq = DispatcherQueue.GetForCurrentThread();
            if (dq is null) { Log("拿不到 DispatcherQueue，定时查杀未启用"); return; }

            _timer = dq.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(TickSeconds);
            _timer.IsRepeating = true;
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
            Log($"定时巡检已启动（每 {TickSeconds} 秒看一次表）");
        }
        catch (Exception ex)
        {
            Log("启动定时巡检失败: " + ex.Message);
        }
    }

    private static void Tick()
    {
        try
        {
            var cfg = EasiNoteGuardConfig.Load();
            if (!cfg.Enabled || cfg.Times.Count == 0) return;

            var now = DateTime.Now;
            var hhmm = now.ToString("HH:mm");
            if (!cfg.Times.Contains(hhmm)) return;

            var key = now.ToString("yyyy-MM-dd ") + hhmm;
            if (_lastFiredKey == key) return;
            _lastFiredKey = key;

            var r = RunOnce(dryRun: false);
            Log($"[{hhmm}] 到点查杀：{r.Summary}"
                + (r.KilledNames.Count > 0 ? " 清掉=" + string.Join("、", r.KilledNames) : "")
                + (r.SkippedNames.Count > 0 ? " 放过=" + string.Join("、", r.SkippedNames) : "")
                + (r.Errors.Count > 0 ? " 失败=" + string.Join("；", r.Errors) : ""));
        }
        catch (Exception ex)
        {
            Log("巡检出错: " + ex.Message);
        }
    }

    // ── 日志 ─────────────────────────────────────────────────

    public static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n");
        }
        catch { /* 记不上不影响功能 */ }
    }

    // ── P/Invoke ─────────────────────────────────────────────

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}

/// <summary>
/// 白板查杀的本地存档（%LOCALAPPDATA%\ClassSoftwareHub\easinote-guard.json）。
///
/// ⚠️ 实验性功能单独用一个文件，就是图将来真不要了可以直接删干净 ——
///    千万别把字段塞进 AppSettings（那条线是要长期背着的）。
/// </summary>
public sealed class EasiNoteGuardConfig
{
    public static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassSoftwareHub", "easinote-guard.json");

    /// <summary>总开关。默认**关** —— 实验性功能，让用户自己决定开不开。</summary>
    public bool Enabled { get; set; }

    /// <summary>时间点，格式 "HH:mm"，每天到点检查一次。</summary>
    public List<string> Times { get; set; } = new();

    public static EasiNoteGuardConfig Load()
    {
        var cfg = new EasiNoteGuardConfig();
        try
        {
            if (File.Exists(StorePath))
                cfg = JsonSerializer.Deserialize<EasiNoteGuardConfig>(File.ReadAllText(StorePath))
                      ?? new EasiNoteGuardConfig();
        }
        catch { /* 存档坏了就用默认值 */ }

        cfg.Times ??= new List<string>();
        // 手改过的 json 里可能有空串 / 乱格式 —— 过滤掉，免得 Tick 里白比对
        cfg.Times.RemoveAll(t => !IsValidTime(t));
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
}
