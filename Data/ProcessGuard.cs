using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace ClassSoftwareHub.Desktop.Data;

/// <summary>
/// 一条「程序专杀」规则：盯着某一个进程，到达设定时间点就结束它（2026-09-28 Nick 提）。
///
/// ⚠️ 每条规则**各自带一份时间点** —— 这是 Nick 明确要求的「可以为每个程序单独配置杀除的时间」，
///    所以别把时间点提到 <see cref="ProcessGuardConfig"/> 上做成全局的。
/// </summary>
public sealed class ProcessGuardRule
{
    /// <summary>要盯的进程名（不含扩展名，例如 <c>chrome</c>）。写入时统一走 <see cref="ProcessGuardConfig.NormalizeName"/>。</summary>
    public string ProcessName { get; set; } = "";

    /// <summary>这条规则要不要参与巡检。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 该程序**正在使用时是否跳过**：true = 存在可见窗口就不结束（默认，防误杀）。
    /// 关掉它就变成"无条件结束"—— 只在确实希望强杀某个程序时才关。
    /// </summary>
    public bool SkipWhenRunning { get; set; } = true;

    /// <summary>时间点，格式 "HH:mm"，每天到达该时间检查一次。</summary>
    public List<string> Times { get; set; } = new();
}

/// <summary>
/// 「程序专杀」的本地存档（%LOCALAPPDATA%\ClassSoftwareHub\procguard.json）。
///
/// ⚠️ 跟白板专杀（<c>easinote-guard.json</c>）**各存各的**：Nick 明确要求白板专杀保持独立、不与通用专杀合并。
///    这里的文件将来整个不要了也能直接删干净，不影响白板专杀那条线。
/// </summary>
public sealed class ProcessGuardConfig
{
    public static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassSoftwareHub", "procguard.json");

    /// <summary>总开关。默认**关** —— 实验性功能，由用户自己决定开不开。</summary>
    public bool Enabled { get; set; }

    /// <summary>要盯的程序清单。</summary>
    public List<ProcessGuardRule> Rules { get; set; } = new();

    public static ProcessGuardConfig Load()
    {
        var cfg = new ProcessGuardConfig();
        try
        {
            if (File.Exists(StorePath))
                cfg = JsonSerializer.Deserialize<ProcessGuardConfig>(File.ReadAllText(StorePath))
                      ?? new ProcessGuardConfig();
        }
        catch { /* 存档损坏则退回默认值 */ }

        cfg.Rules ??= new List<ProcessGuardRule>();

        // 手改过的 json 里可能有没名字的规则、或非法时间点 —— 顺手清掉，免得巡检时白比对
        cfg.Rules.RemoveAll(r => NormalizeName(r.ProcessName).Length == 0);
        foreach (var r in cfg.Rules)
        {
            r.Times ??= new List<string>();
            r.Times.RemoveAll(t => !IsValidTime(t));
            r.ProcessName = NormalizeName(r.ProcessName);
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

    /// <summary>
    /// 进程名统一化：去首尾空白、去掉 <c>.exe</c> 后缀。
    /// <c>Process.ProcessName</c> 本来就不带扩展名，用户却习惯连扩展名一起输 —— 这里抹平差异。
    /// </summary>
    public static string NormalizeName(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s.Trim();
    }

    /// <summary>"HH:mm" 且真的解析得出来（挡掉 "25:99" 这种）。</summary>
    public static bool IsValidTime(string? s) =>
        !string.IsNullOrWhiteSpace(s)
        && TimeSpan.TryParseExact(s.Trim(), @"hh\:mm", null, out _);
}

/// <summary>
/// 「程序专杀」的执行引擎（实验性功能，2026-09-28 Nick 提）。
///
/// 与白板专杀（<see cref="EasiNoteGuard"/>）的差别只有"盯谁"：
///   · 白板专杀：写死只认白板5 家族（<c>EasiNote*</c>），不碰集控端与更新组件；
///   · 程序专杀：盯用户在页面上指定的任意进程名，每条规则各自带时间点。
/// 判定"在不在用"的安全闸是**同一套**：只看该进程有没有可见的顶层窗口。
///
/// ⚠️ 定时同样靠本进程内的 <see cref="DispatcherQueueTimer"/> —— **本程序没在跑就不会查杀**，
///    没有注册 Windows 计划任务，不去动系统里任何东西。
/// </summary>
public static class ProcessGuard
{
    /// <summary>巡检间隔（秒）。到点靠它命中「当前 HH:mm」，所以别设得比 60 秒还大。</summary>
    private const int TickSeconds = 20;

    // ── 扫描 ─────────────────────────────────────────────────

    /// <summary>一个命中规则的进程的现状。</summary>
    public sealed class Match
    {
        public int Pid { get; init; }
        public string Name { get; init; } = "";
        public string Path { get; init; } = "";

        /// <summary>命中它的那条规则里的进程名（用于回显"这条规则现在盯着谁"）。</summary>
        public string RuleName { get; init; } = "";

        /// <summary>有可见的顶层窗口（= 正在使用）。</summary>
        public bool HasWindow { get; set; }

        public override string ToString() => $"{Name}({Pid})";
    }

    /// <summary>扫一遍**某一条规则**命中的进程。**只看不动**。</summary>
    public static List<Match> Scan(ProcessGuardRule rule)
    {
        var list = new List<Match>();
        var want = ProcessGuardConfig.NormalizeName(rule.ProcessName);
        if (want.Length == 0) return list;

        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (Exception ex) { Log("枚举进程失败: " + ex.Message); return list; }

        foreach (var p in all)
        {
            try
            {
                if (!string.Equals(p.ProcessName, want, StringComparison.OrdinalIgnoreCase)) continue;

                var path = "";
                try { path = p.MainModule?.FileName ?? ""; } catch { /* 权限不够就是空 */ }

                list.Add(new Match
                {
                    Pid = p.Id,
                    Name = p.ProcessName,
                    Path = path,
                    RuleName = want,
                    HasWindow = HasVisibleWindow(p.Id)
                });
            }
            catch { /* 单个进程读不到就跳过，别让整轮扫描失败 */ }
            finally { p.Dispose(); }
        }
        return list;
    }

    /// <summary>扫一遍**所有启用的规则**命中的进程（同一个进程只会出现一次）。</summary>
    public static List<Match> ScanAll(ProcessGuardConfig cfg)
    {
        var seen = new HashSet<int>();
        var list = new List<Match>();

        foreach (var rule in cfg.Rules.Where(r => r.Enabled))
            foreach (var m in Scan(rule))
                if (seen.Add(m.Pid)) list.Add(m);

        return list;
    }

    /// <summary>
    /// 这个进程有没有**可见的顶层窗口**。
    ///
    /// 口径与白板专杀一致（别改成 Process.MainWindowHandle != 0）：多进程程序的主界面窗口
    /// 未必挂在被扫到的那个 pid 上，而 MainWindowHandle 对很多后台进程会返回 0 或过期值。
    /// 直接枚举所有顶层窗口、按 pid 归属、看 IsWindowVisible 最实在。
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
                    return "未命中列表中的任何进程。";

                var parts = new List<string>();
                if (Killed > 0) parts.Add($"{(DryRun ? "可结束" : "已结束")} {Killed} 个");
                if (Skipped > 0) parts.Add($"跳过 {Skipped} 个（存在可见窗口，正在使用）");
                if (Errors.Count > 0) parts.Add($"{Errors.Count} 个结束失败");
                return string.Join("；", parts) + "。";
            }
        }
    }

    /// <param name="dryRun">true = 只报告会动谁，**真的一个都不结束**（页面上的「仅检测」走这条）。</param>
    public static GuardResult RunOnce(ProcessGuardConfig cfg, bool dryRun)
    {
        var r = new GuardResult { DryRun = dryRun };

        foreach (var rule in cfg.Rules.Where(x => x.Enabled))
        {
            foreach (var m in Scan(rule))
            {
                if (rule.SkipWhenRunning && m.HasWindow)      // 安全闸：正在使用则跳过
                {
                    r.Skipped++;
                    r.SkippedNames.Add($"{m.Name}（PID {m.Pid}）");
                    continue;
                }

                if (dryRun)
                {
                    r.Killed++;
                    r.KilledNames.Add($"{m.Name}（PID {m.Pid}）");
                    continue;
                }

                try
                {
                    using var proc = Process.GetProcessById(m.Pid);
                    proc.Kill();
                    r.Killed++;
                    r.KilledNames.Add($"{m.Name}（PID {m.Pid}）");
                }
                catch (Exception ex)
                {
                    // 最常见的是权限不够（目标进程以管理员身份运行）
                    r.Errors.Add($"{m.Name}（PID {m.Pid}）：{ex.Message}");
                }
            }
        }
        return r;
    }

    // ── 定时巡检 ─────────────────────────────────────────────

    private static DispatcherQueueTimer? _timer;

    /// <summary>「日期 + 时刻 + 规则序号」记账，同一次只跑一遍（20 秒一发，同一分钟会 tick 好几次）。</summary>
    private static readonly HashSet<string> _fired = new();

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
            var cfg = ProcessGuardConfig.Load();
            if (!cfg.Enabled || cfg.Rules.Count == 0) return;

            var now = DateTime.Now;
            var hhmm = now.ToString("HH:mm");
            var day = now.ToString("yyyy-MM-dd");

            // 每天清一次记账，免得集合无限长
            if (_fired.Count > 512) _fired.Clear();

            // 每轮**按规则**执行：只有"这条规则的时间点命中"的规则才动手
            var due = cfg.Rules
                .Where(r => r.Enabled && r.Times.Contains(hhmm))
                .ToList();
            if (due.Count == 0) return;

            var pending = due.Where(r =>
            {
                var key = $"{day} {hhmm} {ProcessGuardConfig.NormalizeName(r.ProcessName)}";
                return _fired.Add(key);        // false = 这一分钟已经跑过
            }).ToList();
            if (pending.Count == 0) return;

            var r2 = RunOnce(new ProcessGuardConfig { Enabled = true, Rules = pending }, dryRun: false);
            Log($"[{hhmm}] 到点查杀：{r2.Summary}"
                + (r2.KilledNames.Count > 0 ? " 结束=" + string.Join("、", r2.KilledNames) : "")
                + (r2.SkippedNames.Count > 0 ? " 跳过=" + string.Join("、", r2.SkippedNames) : "")
                + (r2.Errors.Count > 0 ? " 失败=" + string.Join("；", r2.Errors) : ""));
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
            Core.AppLog.Info("procguard", msg);
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
