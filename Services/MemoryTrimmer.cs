using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 内存回收（教学机 8G 内存，不能一路涨上去）。
///
/// 两步走：
///   ① 先让东西能被放掉 —— 页面容器关掉导航缓存（ContentFrame.CacheSize=0）、
///      每个页面自己 Unloaded 时停掉自己的定时器（定时器会通过委托把页面对象钉在内存里）
///   ② 再把内存真还给系统 —— GC + EmptyWorkingSet（把闲置页丢进系统 standby list，
///      进程的工作集会立刻降下来，别的程序要用内存时系统优先回收）
///
/// 只在「用户看不见」的时候收：收进托盘 / 最小化 / 切页停稳之后 / 浮窗收起。
/// 节流 4 秒，避免连续切页时反复 GC 卡顿。
/// </summary>
public static class MemoryTrimmer
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private static DateTimeOffset _last = DateTimeOffset.MinValue;
    private static readonly object Gate = new();

    /// <summary>上一次回收把托管堆压下去多少（字节），界面上可以显示。</summary>
    public static long LastFreedBytes { get; private set; }

    /// <summary>累计回收次数（排查用）。</summary>
    public static int TrimCount { get; private set; }

    private static string LogPath => Path.Combine(SettingsStore.Dir, "memory.log");

    /// <summary>收一次。force = 用户主动触发，忽略节流。</summary>
    public static void Trim(bool force = false)
    {
        lock (Gate)
        {
            var now = DateTimeOffset.Now;
            if (!force && (now - _last).TotalMilliseconds < 4000) return;
            _last = now;
            TrimCount++;
        }

        try
        {
            var before = GC.GetTotalMemory(false);

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);

            var after = GC.GetTotalMemory(false);
            LastFreedBytes = Math.Max(0, before - after);

            EmptyWorkingSet(GetCurrentProcess());

            Log($"第 {TrimCount} 次：托管堆 {before / 1048576.0:0.#}MB → {after / 1048576.0:0.#}MB (force={force})");
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[mem] 回收失败: " + ex.Message);
            Log("回收失败：" + ex.Message);
        }
    }

    /// <summary>等界面渲染停稳再收（切页动画没结束就 GC，纯浪费）。</summary>
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

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Dir);
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { }
    }
}
