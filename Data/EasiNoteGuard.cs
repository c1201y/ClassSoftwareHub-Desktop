using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace ClassSoftwareHub.Desktop.Data;



















public static class EasiNoteGuard
{
    
    public const string NamePrefix = "EasiNote";

    
    private const int TickSeconds = 20;

    

    
    public sealed class GuardProc
    {
        public int Pid { get; init; }
        public string Name { get; init; } = "";
        public string Path { get; init; } = "";

        
        public bool HasWindow { get; set; }

        public override string ToString() => $"{Name}({Pid})";
    }

    
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
                try { path = p.MainModule?.FileName ?? ""; } catch {  }

                list.Add(new GuardProc
                {
                    Pid = p.Id,
                    Name = p.ProcessName,
                    Path = path,
                    HasWindow = HasVisibleWindow(p.Id)
                });
            }
            catch {  }
            finally { p.Dispose(); }
        }
        return list;
    }

    
    private static bool IsTarget(string processName) =>
        !string.IsNullOrEmpty(processName)
        && processName.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase);

    
    
    
    
    
    
    
    
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
                    return "没找到希沃白板5 的进程。";

                var parts = new List<string>();
                if (Killed > 0) parts.Add($"{(DryRun ? "会清掉" : "已清掉")} {Killed} 个");
                if (Skipped > 0) parts.Add($"放过 {Skipped} 个（有窗口，正在用）");
                if (Errors.Count > 0) parts.Add($"{Errors.Count} 个没能结束");
                return string.Join("；", parts) + "。";
            }
        }
    }

    
    public static GuardResult RunOnce(bool dryRun)
    {
        var r = new GuardResult { DryRun = dryRun };

        foreach (var p in Scan())
        {
            if (p.HasWindow)                       
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
                
                r.Errors.Add($"{p.Name}（PID {p.Pid}）：{ex.Message}");
            }
        }
        return r;
    }

    

    private static DispatcherQueueTimer? _timer;

    
    private static string _lastFiredKey = "";

    
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

    

    public static void Log(string msg)
    {
        try
        {
            Core.AppLog.Info("easiguard", msg);
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







public sealed class EasiNoteGuardConfig
{
    public static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassSoftwareHub", "easinote-guard.json");

    
    public bool Enabled { get; set; }

    
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
        catch {  }

        cfg.Times ??= new List<string>();
        
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
        catch {  }
    }

    
    public static bool IsValidTime(string? s) =>
        !string.IsNullOrWhiteSpace(s)
        && TimeSpan.TryParseExact(s.Trim(), @"hh\:mm", null, out _);
}
