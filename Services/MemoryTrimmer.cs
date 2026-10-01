using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 内存回收（教学机 8G 内存，不能一路涨上去）。
///
/// 两步走：
///   ① 先让东西能被放掉 —— 页面不再被 Frame 永久钉住（CacheSize 有限）、
///      每个页面自己 Unloaded 时停掉自己的定时器（定时器会通过委托把页面对象钉在内存里）
///   ② 再把内存真还给系统 —— GC + EmptyWorkingSet（把闲置页丢进系统 standby list，
///      进程的工作集会立刻降下来，别的程序要用内存时系统优先回收）
///
/// ⛔⛔ **只在「用户看不见窗口」的时候收** —— 这条是 2026-10-01 用实测换来的：
///    原来每次切页停稳 3 秒就在 UI 线程上来一次，正好撞在用户开始滚动/点击的瞬间，
///    表现就是"切过去先顿一下、回来更顿"。现在切页完全不收，只在收进托盘 / 最小化时收。
///    <see cref="IsIdle"/> 由 MainWindow 注入，用来判"用户此刻看不见窗口"。
///
/// 回收本身也挪到后台线程：它不碰 UI 对象，没必要占着 UI 线程做（就算中途用户把窗口叫回来，
/// 也只是后台忙一下，不会卡住渲染）。
/// </summary>
public static class MemoryTrimmer
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private static DateTimeOffset _last = DateTimeOffset.MinValue;
    private static readonly object Gate = new();

    /// <summary>
    /// 由 <c>MainWindow</c> 注入：返回 true 表示"窗口此刻用户看不见"（收进托盘 / 最小化）。
    /// 没注入时按"可以回收"处理（保持旧行为，别把调用方搞死）。
    /// </summary>
    public static Func<bool>? IsIdle { get; set; }

    /// <summary>上一次回收把托管堆压下去多少（字节），界面上可以显示。</summary>
    public static long LastFreedBytes { get; private set; }

    /// <summary>累计回收次数（排查用）。</summary>
    public static int TrimCount { get; private set; }

    private static string LogPath => Path.Combine(SettingsStore.Dir, "memory.log");

    /// <summary>收一次。force = 用户主动触发，忽略节流与"窗口可见"判断。</summary>
    public static void Trim(bool force = false)
    {
        // 用户正看着窗口 → 一律不回收（force 例外，那是用户自己点的）
        if (!force && IsIdle is { } idle && !idle()) return;

        lock (Gate)
        {
            var now = DateTimeOffset.Now;
            if (!force && (now - _last).TotalMilliseconds < 4000) return;
            _last = now;
            TrimCount++;
        }

        // 换后台线程做：GC / EmptyWorkingSet 都不需要 UI 线程，别在这儿堵界面
        System.Threading.Tasks.Task.Run(() => DoTrim(force));
    }

    private static void DoTrim(bool force)
    {
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

    /// <summary>
    /// 等界面渲染停稳再收。⚠️ 真正决定收不收的是 <see cref="Trim"/> 里的 IsIdle 判断 ——
    /// 这个延时只是让"刚最小化又马上还原"这种一秒钟的来回不要白忙。
    /// </summary>
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
