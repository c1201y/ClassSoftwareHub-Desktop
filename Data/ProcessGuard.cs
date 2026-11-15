using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace ClassSoftwareHub.Desktop.Data;







public sealed class ProcessGuardRule
{
    
    public string ProcessName { get; set; } = "";

    
    public bool Enabled { get; set; } = true;

    
    
    
    
    public bool SkipWhenRunning { get; set; } = true;

    
    public List<string> Times { get; set; } = new();
}







public sealed class ProcessGuardConfig
{
    public static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassSoftwareHub", "procguard.json");

    
    public bool Enabled { get; set; }

    
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
        catch {  }

        cfg.Rules ??= new List<ProcessGuardRule>();

        
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
        catch {  }
    }

    
    
    
    
    public static string NormalizeName(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s.Trim();
    }

    
    public static bool IsValidTime(string? s) =>
        !string.IsNullOrWhiteSpace(s)
        && TimeSpan.TryParseExact(s.Trim(), @"hh\:mm", null, out _);
}












public static class ProcessGuard
{
    
    private const int TickSeconds = 20;

    

    
    public sealed class Match
    {
        public int Pid { get; init; }
        public string Name { get; init; } = "";
        public string Path { get; init; } = "";

        
        public string RuleName { get; init; } = "";

        
        public bool HasWindow { get; set; }

        public override string ToString() => $"{Name}({Pid})";
    }

    
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
                try { path = p.MainModule?.FileName ?? ""; } catch {  }

                list.Add(new Match
                {
                    Pid = p.Id,
                    Name = p.ProcessName,
                    Path = path,
                    RuleName = want,
                    HasWindow = HasVisibleWindow(p.Id)
                });
            }
            catch {  }
            finally { p.Dispose(); }
        }
        return list;
    }

    
    public static List<Match> ScanAll(ProcessGuardConfig cfg)
    {
        var seen = new HashSet<int>();
        var list = new List<Match>();

        foreach (var rule in cfg.Rules.Where(r => r.Enabled))
            foreach (var m in Scan(rule))
                if (seen.Add(m.Pid)) list.Add(m);

        return list;
    }

    
    
    
    
    
    
    
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

    

    
    public sealed class GuardResult
    {
        public bool DryRun { get; init; }
        public int Killed { get; set; }
        public int Skipped { get; set; }
        public List<string> KilledNames { get; } = new();
        public List<string> SkippedNames { get; } = new();
        public List<string> Errors { get; } = new();

        
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

    
    public static GuardResult RunOnce(ProcessGuardConfig cfg, bool dryRun)
    {
        var r = new GuardResult { DryRun = dryRun };

        foreach (var rule in cfg.Rules.Where(x => x.Enabled))
        {
            foreach (var m in Scan(rule))
            {
                if (rule.SkipWhenRunning && m.HasWindow)      
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
                    
                    r.Errors.Add($"{m.Name}（PID {m.Pid}）：{ex.Message}");
                }
            }
        }
        return r;
    }

    

    private static DispatcherQueueTimer? _timer;

    
    private static readonly HashSet<string> _fired = new();

    
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

            
            if (_fired.Count > 512) _fired.Clear();

            
            var due = cfg.Rules
                .Where(r => r.Enabled && r.Times.Contains(hhmm))
                .ToList();
            if (due.Count == 0) return;

            var pending = due.Where(r =>
            {
                var key = $"{day} {hhmm} {ProcessGuardConfig.NormalizeName(r.ProcessName)}";
                return _fired.Add(key);        
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

    

    public static void Log(string msg)
    {
        try
        {
            Core.AppLog.Info("procguard", msg);
        }
        catch {  }
    }

    

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
