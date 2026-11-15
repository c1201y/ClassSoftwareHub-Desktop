using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace ClassSoftwareHub.Desktop.Data;


public static class ShutdownMethods
{
    
    public const string Shutdown = "shutdown";

    
    
    
    
    
    public const string Slide = "slide";

    
    public static string DisplayName(string? method) => method switch
    {
        Slide => "滑动关机",
        _ => "直接关机",
    };
}







public sealed class ShutdownPoint
{
    
    public string Time { get; set; } = "17:30";

    
    public string Method { get; set; } = ShutdownMethods.Shutdown;

    
    public int DelaySeconds { get; set; } = 60;
}







public sealed class AutoShutdownConfig
{
    public static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassSoftwareHub", "autoshutdown.json");

    
    
    
    
    public const int MaxDelaySeconds = 86400;

    
    
    
    
    public bool Enabled { get; set; }

    
    
    
    
    
    
    public List<List<ShutdownPoint>> Days { get; set; } = NewDays();

    
    
    
    
    
    
    
    
    
    
    public ShutdownPoint? LastPoint { get; set; }

    
    public static List<List<ShutdownPoint>> NewDays() =>
        Enumerable.Range(0, 7).Select(_ => new List<ShutdownPoint>()).ToList();

    
    public static int DayIndexOf(DateTime time) => ((int)time.DayOfWeek + 6) % 7;

    
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
        catch {  }

        
        cfg.Days ??= NewDays();
        while (cfg.Days.Count < 7) cfg.Days.Add(new List<ShutdownPoint>());
        if (cfg.Days.Count > 7) cfg.Days = cfg.Days.Take(7).ToList();

        for (var i = 0; i < cfg.Days.Count; i++)
        {
            cfg.Days[i] ??= new List<ShutdownPoint>();
            cfg.Days[i].RemoveAll(p => p is null || !IsValidTime(p.Time));
            foreach (var p in cfg.Days[i]) p.Method = NormalizeMethod(p.Method);
        }

        
        
        if (cfg.LastPoint is not null)
        {
            if (!IsValidTime(cfg.LastPoint.Time))
            {
                cfg.LastPoint = null;
            }
            else
            {
                cfg.LastPoint.Method = NormalizeMethod(cfg.LastPoint.Method);
                cfg.LastPoint.DelaySeconds = Math.Clamp(cfg.LastPoint.DelaySeconds, 0, MaxDelaySeconds);
            }
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

    
    public static bool IsValidTime(string? s) =>
        !string.IsNullOrWhiteSpace(s)
        && TimeSpan.TryParseExact(s.Trim(), @"hh\:mm", null, out _);

    
    public static string NormalizeMethod(string? method) =>
        string.Equals(method, ShutdownMethods.Slide, StringComparison.OrdinalIgnoreCase)
            ? ShutdownMethods.Slide
            : ShutdownMethods.Shutdown;
}


















public static class AutoShutdown
{
    
    private const int TickSeconds = 20;

    
    
    
    
    private static readonly TimeSpan LateWindow = TimeSpan.FromMinutes(3);

    
    private const int MaxDelaySeconds = AutoShutdownConfig.MaxDelaySeconds;

    private static DispatcherQueueTimer? _timer;

    
    private static readonly HashSet<string> _fired = new();

    
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

            
            if (_fired.Count > 512) _fired.Clear();

            for (var i = 0; i < points.Count; i++)
            {
                var point = points[i];
                if (point is null) continue;
                if (!TimeSpan.TryParseExact(point.Time.Trim(), @"hh\:mm", null, out var span)) continue;

                var target = now.Date.Add(span);
                var late = now - target;
                if (late < TimeSpan.Zero || late > LateWindow) continue;   

                if (!_fired.Add($"{now:yyyy-MM-dd} {dayIndex} {i}")) continue;   
                Execute(point, target);
            }
        }
        catch (Exception ex)
        {
            Log("巡检出错: " + ex.Message);
        }
    }

    
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

    
    
    
    
    public static string CancelPending()
    {
        var code = RunShutdown("/a", out var error);

        
        if (code == 0)
        {
            Log("已取消本次自动关机");
            return "已取消本次自动关机";
        }

        Log($"取消关机未成功 exit={code}" + (error.Length > 0 ? " " + error : ""));
        return "当前没有待执行的关机";
    }

    
    
    
    public static void ShutdownNow(string method, int delaySeconds = 0)
    {
        try
        {
            if (AutoShutdownConfig.NormalizeMethod(method) == ShutdownMethods.Slide)
            {
                var exe = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "SlideToShutDown.exe");
                if (!File.Exists(exe))
                {
                    Log("滑动关机：找不到 " + exe);
                    return;
                }

                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                Log("已调起滑动关机（手动触发）");
                return;
            }

            var delay = Math.Clamp(delaySeconds, 0, MaxDelaySeconds);
            var code = RunShutdown($"/s /f /t {delay}", out var error);
            Log($"直接关机 delay={delay}s exit={code}" + (error.Length > 0 ? " " + error : ""));
        }
        catch (Exception ex)
        {
            Log("执行关机失败: " + ex.Message);
        }
    }

    
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

    

    public static void Log(string msg)
    {
        try
        {
            Core.AppLog.Info("autoshutdown", msg);
        }
        catch {  }
    }
}
