using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClassSoftwareHub.Desktop.Services;































public static class MemoryTrimmer
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    

    
    private const long SoftWaterMarkBytes = 320L * 1024 * 1024;

    
    private const long HardWaterMarkBytes = 512L * 1024 * 1024;

    
    private const int IdleSoftMs = 10000;

    
    private const int IdleHardMs = 2500;

    private const int CooldownSoftMs = 30000;
    private const int CooldownHardMs = 10000;
    private const int CooldownHiddenMs = 4000;

    
    private const int PollMs = 5000;

    
    private const int NavCooldownMs = 1200;

    
    private const int NavDelayMs = 600;

    
    private const int ObserveLogMs = 60000;

    
    private const int BaselineLogMs = 5 * 60 * 1000;

    
    private const long SoftFloorPaddingBytes = 64L * 1024 * 1024;

    private static DateTimeOffset _last = DateTimeOffset.MinValue;
    private static readonly object Gate = new();
    private static Thread? _watch = null;
    private static DateTimeOffset _lastObserve = DateTimeOffset.MinValue;
    private static DateTimeOffset _lastBaseline = DateTimeOffset.MinValue;

    
    private static bool _hiddenTrimActive;

    
    private static int _hiddenQuietCount;

    
    
    
    
    
    
    private static long _softFloor = SoftWaterMarkBytes;

    
    
    
    
    
    public static Func<bool>? IsIdle { get; set; }

    
    public static long LastFreedBytes { get; private set; }

    
    public static int TrimCount { get; private set; }

    
    public static long LastWorkingSetBytes { get; private set; }

    
    
    
    
    
    public static void StartWatch()
    {
        lock (Gate)
        {
            if (_watch is { IsAlive: true }) return;

            _watch = new Thread(WatchLoop)
            {
                IsBackground = true,
                Name = "csh-mem-watch",
            };
            _watch.Start();
        }
    }

    
    private static void WatchLoop()
    {
        while (true)
        {
            try { Thread.Sleep(PollMs); }
            catch { return; }

            try { Trim(); }
            catch {  }
        }
    }

    
    
    
    
    public static void Trim(bool force = false)
    {
        if (force) { Run("用户主动", 0); return; }

        var hidden = IsIdle is { } idleFn && SafeCall(idleFn);
        var ws = CurrentWorkingSet();
        LastWorkingSetBytes = ws;

        
        
        
        MaybeBaseline(ws);

        if (hidden)
        {
            
            
            
            
            
            
            if (!_hiddenTrimActive)
            {
                if (Run("窗口不可见", CooldownHiddenMs))
                {
                    _hiddenTrimActive = true;
                    _hiddenQuietCount = 0;
                }
            }
            else if (Run("窗口不可见", CooldownHiddenMs, quiet: true))
            {
                _hiddenQuietCount++;
            }
            return;
        }

        if (_hiddenTrimActive)
        {
            _hiddenTrimActive = false;
            if (_hiddenQuietCount > 0)
                Log($"窗口不可见期间另静默回收 {_hiddenQuietCount} 次（同一种情况不再逐条记录）");
            _hiddenQuietCount = 0;
        }

        if (ws < _softFloor) return;

        var idle = HostIdleMs();
        var hard = ws >= HardWaterMarkBytes;
        var needIdle = hard ? IdleHardMs : IdleSoftMs;

        if (idle < needIdle)
        {
            
            
            Observe(ws, idle, hard);
            return;
        }

        Run($"{Mb(ws):0}MB / 停手 {idle / 1000.0:0.#}s{(hard ? " / 硬水位" : "")}",
            hard ? CooldownHardMs : CooldownSoftMs);
    }

    
    
    
    
    
    
    
    
    
    public static void ScheduleAfterNavigate()
    {
        try
        {
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (queue is null) { OnNavigated(); return; }

            var timer = queue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(NavDelayMs);
            timer.IsRepeating = false;
            timer.Tick += (s, _) =>
            {
                if (s is Microsoft.UI.Dispatching.DispatcherQueueTimer t) t.Stop();
                OnNavigated();
            };
            timer.Start();
        }
        catch
        {
            OnNavigated();
        }
    }

    
    private static void OnNavigated() => Run("切页", NavCooldownMs);

    
    
    
    
    private static bool Run(string reason, int cooldownMs, bool quiet = false)
    {
        lock (Gate)
        {
            var now = DateTimeOffset.Now;
            if ((now - _last).TotalMilliseconds < cooldownMs) return false;
            _last = now;
            TrimCount++;
        }

        
        System.Threading.Tasks.Task.Run(() => DoTrim(reason, quiet));
        return true;
    }

    private static void DoTrim(string reason, bool quiet = false)
    {
        try
        {
            var wsBefore = CurrentWorkingSet();
            var before = GC.GetTotalMemory(false);

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);

            var after = GC.GetTotalMemory(false);
            LastFreedBytes = Math.Max(0, before - after);

            EmptyWorkingSet(GetCurrentProcess());

            var wsAfter = CurrentWorkingSet();
            LastWorkingSetBytes = wsAfter;

            
            
            
            if (!quiet)
                Log($"第 {TrimCount} 次（{reason}）：工作集 {Mb(wsBefore):0.#} → {Mb(wsAfter):0.#} MB，" +
                    $"托管堆 {Mb(before):0.#} → {Mb(after):0.#} MB");

            RaiseFloorIfNeeded(wsAfter);
        }
        catch (Exception ex)
        {
            
            
            Core.AppLog.Warn("memory", "内存回收失败：" + ex.Message);
        }
    }

    
    private static void RaiseFloorIfNeeded(long wsAfter)
    {
        if (wsAfter <= _softFloor) return;

        var raised = Math.Min(wsAfter + SoftFloorPaddingBytes, HardWaterMarkBytes);
        if (raised <= _softFloor) return;

        _softFloor = raised;
        Log($"回收后工作集仍有 {Mb(wsAfter):0.#}MB（属于刚需部分）→ 软水位自适应抬到 {Mb(raised):0.#}MB");
    }

    
    
    
    
    
    private static void MaybeBaseline(long ws)
    {
        lock (Gate)
        {
            var now = DateTimeOffset.Now;
            if ((now - _lastBaseline).TotalMilliseconds < BaselineLogMs) return;
            _lastBaseline = now;
        }

        Log($"[基线] 工作集 {Mb(ws):0.#} MB，托管堆 {Mb(GC.GetTotalMemory(false)):0.#} MB");
    }

    
    
    
    
    
    private static void Observe(long ws, int idle, bool hard)
    {
        lock (Gate)
        {
            var now = DateTimeOffset.Now;
            if ((now - _lastObserve).TotalMilliseconds < ObserveLogMs) return;
            _lastObserve = now;
        }

        Log($"[观察] 工作集 {Mb(ws):0.#}MB（{(hard ? "已过硬水位" : "超软水位")}），" +
            $"用户仍在操作（停手 {idle / 1000.0:0.#}s）→ 暂不回收");
    }

    
    
    
    
    
    public static void TrimLater(int delayMs = 3000, bool force = false)
    {
        try
        {
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (queue is null) { Trim(force); return; }

            var timer = queue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(delayMs);
            timer.IsRepeating = false;
            timer.Tick += (s, _) =>
            {
                if (s is Microsoft.UI.Dispatching.DispatcherQueueTimer t) t.Stop();
                Trim(force);
            };
            timer.Start();
        }
        catch
        {
            Trim(force);
        }
    }

    
    private static long CurrentWorkingSet()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return self.WorkingSet64;
        }
        catch { return 0; }
    }

    
    
    
    private static int HostIdleMs()
    {
        try
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return 0;

            
            
            var elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
            return elapsed > int.MaxValue ? 0 : (int)elapsed;
        }
        catch { return 0; }
    }

    private static bool SafeCall(Func<bool> f)
    {
        try { return f(); }
        catch { return false; }      
    }

    private static double Mb(long bytes) => bytes / 1048576.0;

    private static void Log(string message)
    {
        try
        {
            
            
            
            
            Core.AppLog.Debug("memory", message);
        }
        catch { }
    }
}
