using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

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
/// ⛔⛔ **切页路径 2026-10-04 又被挂回来了 —— 但别照抄 2026-10-01 那版**（Nick 要求回到"跳一页收一次"的激进思路）。
///   两版的差别是**本质**的，不是调参：
///     · 被否掉那版：在 **UI 线程**上跑 `GC.Collect` + `WaitForPendingFinalizers`【阻塞】，还延时 3 秒 ——
///       正好落在用户"切过去、刚开始滚动/点击"的瞬间，所以是"切过去先顿一下、回来更顿"；
///     · 现在这版：触发点只做一次节流判断，回收本体走的还是 <see cref="DoTrim"/> 的**后台线程**、
///       `blocking:false`、不阻塞 UI；并且由调用方错开约 0.6 秒（等切页动画与首帧过去）再触发。
///   也就是说："顿"的根源（UI 线程阻塞回收）没有回来，回来的只是"收得勤"。
///
/// 2026-10-02 改触发条件（Nick：用户一直可见地乱点，占用也能顶到半个 G）：
///   原先只在"窗口看不见"（托盘 / 最小化）时收 —— 用户不最小化就永远收不到，等于没有回收。
///   现在**三条路都能收**：
///     · 窗口看不见 → 立刻收（最该收的时候，不等水位、不等停手）
///     · 切一页   → 停稳约 0.6 秒收一次（2026-10-04 加回，见上）
///     · 窗口可见 → **水位 + 停手** 双条件，由 <see cref="StartWatch"/> 起的巡检线程每 5 秒问一次
///   "停手"用系统级最后输入时间（<c>GetLastInputInfo</c>）判断：不挂钩子、零侵入、也不区分
///   用户在我们窗口里还是别的窗口里 —— 反正他此刻没在操作，收一下就不会被感觉到。
///   水位分两档：软水位等停手 10 秒（多半在看内容），硬水位只等 2.5 秒（再涨就要出事，宁可顿一下）。
///
/// ⚠️ 软水位会**自适应抬高**：回收完工作集仍然高于水位，说明那部分是刚需（内容包 / WebView2 常驻），
///    于是把水位抬上去 —— 否则每 30 秒白收一次，抖动比多占那点内存更伤（见 <see cref="_softFloor"/>）。
/// </summary>
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

    // ══════════ 触发参数（2026-10-02 定，改之前先想清楚代价）══════════

    /// <summary>软水位初始值：工作集超过它就"想收"了，但要等用户停手。</summary>
    private const long SoftWaterMarkBytes = 320L * 1024 * 1024;

    /// <summary>硬水位：到这儿就别等了（只在用户正按着鼠标那一下躲一躲）。</summary>
    private const long HardWaterMarkBytes = 512L * 1024 * 1024;

    /// <summary>软水位下要"停手"多久才算没在用。</summary>
    private const int IdleSoftMs = 10000;

    /// <summary>硬水位下要"停手"多久 —— 只是躲开"手正按着"那一下。</summary>
    private const int IdleHardMs = 2500;

    private const int CooldownSoftMs = 30000;
    private const int CooldownHardMs = 10000;
    private const int CooldownHiddenMs = 4000;

    /// <summary>巡检间隔。</summary>
    private const int PollMs = 5000;

    /// <summary>切页回收的节流：连着点导航时不要每一下都 GC（两下导航间隔常小于 1 秒）。</summary>
    private const int NavCooldownMs = 1200;

    /// <summary>切页之后等这么久再收 —— 让切页动画和首帧先过去，别跟在建的页面抢内存页。</summary>
    private const int NavDelayMs = 600;

    /// <summary>"够水位了但还没收"的观察日志最小间隔（别把日志刷爆）。</summary>
    private const int ObserveLogMs = 60000;

    /// <summary>基线日志间隔：每 5 分钟记一行"现在多少"。</summary>
    private const int BaselineLogMs = 5 * 60 * 1000;

    /// <summary>回收后仍高于水位时，往上抬多少作余量。</summary>
    private const long SoftFloorPaddingBytes = 64L * 1024 * 1024;

    private static DateTimeOffset _last = DateTimeOffset.MinValue;
    private static readonly object Gate = new();
    private static Thread? _watch = null;
    private static DateTimeOffset _lastObserve = DateTimeOffset.MinValue;
    private static DateTimeOffset _lastBaseline = DateTimeOffset.MinValue;

    /// <summary>
    /// 软水位的**自适应下限**：回收完工作集仍然高于水位，说明那部分是刚需
    /// （内容包 / WebView2 常驻之类），就把水位抬上去。
    /// 否则一旦基线本来就高于水位，会每 30 秒白收一次 —— 抖动比多占那点内存更伤。
    /// 上限不超过硬水位；只升不降（降下去只会在下次回收时再抬回来，白折腾）。
    /// </summary>
    private static long _softFloor = SoftWaterMarkBytes;

    /// <summary>
    /// 由 <c>MainWindow</c> 注入：返回 true 表示"窗口此刻用户看不见"（收进托盘 / 最小化）。
    /// 看不见时**不做水位与停手判断，直接收** —— 这是最该收的时候。
    /// 没注入时按"看得见"处理（只走水位那条路，保守但不会把界面卡住）。
    /// </summary>
    public static Func<bool>? IsIdle { get; set; }

    /// <summary>上一次回收把托管堆压下去多少（字节），界面上可以显示。</summary>
    public static long LastFreedBytes { get; private set; }

    /// <summary>累计回收次数（排查用）。</summary>
    public static int TrimCount { get; private set; }

    /// <summary>最近一次测到的**进程工作集**（字节）——日志与排查用（Nick 要求把工作集记下来）。</summary>
    public static long LastWorkingSetBytes { get; private set; }

    /// <summary>
    /// 起巡检线程（幂等，重复调用不会起第二条）。
    /// ⚠️ <c>IsBackground = true</c>：这条线程绝不能拦住进程退出。
    /// 由 <c>MainWindow</c> 在注入 <see cref="IsIdle"/> 之后调用一次。
    /// </summary>
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

    /// <summary>巡检：定时问一次"现在该不该收"。判定全在 <see cref="Trim"/> 里，这里不重复写一遍。</summary>
    private static void WatchLoop()
    {
        while (true)
        {
            try { Thread.Sleep(PollMs); }
            catch { return; }

            try { Trim(); }
            catch { /* 巡检绝不能把进程搞崩 */ }
        }
    }

    /// <summary>
    /// 收一次。判定与节流都在这里，调用方不用自己判断。
    /// <paramref name="force"/> = 用户主动触发，忽略一切判断与节流。
    /// </summary>
    public static void Trim(bool force = false)
    {
        if (force) { Run("用户主动", 0); return; }

        var hidden = IsIdle is { } idleFn && SafeCall(idleFn);
        var ws = CurrentWorkingSet();
        LastWorkingSetBytes = ws;

        // 基线日志（每 5 分钟一行，第一次调用就会记 —— 相当于"启动基线"）。
        // 为什么必须有：工作集没到水位时上面几条路一条都不会写日志，memory.log 就是空的 ——
        // 而"到底涨到多少、涨得快不快"恰恰是 Nick 要看的第一件事（2026-10-02 要求先补工作集日志）。
        MaybeBaseline(ws);

        if (hidden)
        {
            Run("窗口不可见", CooldownHiddenMs);
            return;
        }

        if (ws < _softFloor) return;

        var idle = HostIdleMs();
        var hard = ws >= HardWaterMarkBytes;
        var needIdle = hard ? IdleHardMs : IdleSoftMs;

        if (idle < needIdle)
        {
            // 够水位了，但用户还在动 —— 记一条观察日志（节流），
            // 好让事后能回答"当时涨到多少、为什么没收"。
            Observe(ws, idle, hard);
            return;
        }

        Run($"{Mb(ws):0}MB / 停手 {idle / 1000.0:0.#}s{(hard ? " / 硬水位" : "")}",
            hard ? CooldownHardMs : CooldownSoftMs);
    }

    /// <summary>
    /// 切完一页之后调（<c>Pages/ShellPage.xaml.cs</c> 的 <c>ContentFrame.Navigated</c>）。
    ///
    /// 行为：错开 <see cref="NavDelayMs"/> 再收一次；连点导航时由 <see cref="NavCooldownMs"/> 节流。
    /// **不看水位、不等停手** —— 这就是 Nick 要的"跳一页收一次"。
    ///
    /// ⚠️ 它跟 2026-10-01 被否掉的那版**不是一回事**（那版在 UI 线程上同步 GC 且延时 3 秒，
    ///    详见类注释）。这里只是"触发"：UI 线程上仅有一次节流判断，回收本体在后台线程。
    /// </summary>
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

    /// <summary>切页回收本体（节流走 <see cref="Run"/> 那个共享的"上次回收时间"，不会跟别的路重复收）。</summary>
    private static void OnNavigated() => Run("切页", NavCooldownMs);

    /// <summary>过节流就真收（换后台线程做）。</summary>
    private static void Run(string reason, int cooldownMs)
    {
        lock (Gate)
        {
            var now = DateTimeOffset.Now;
            if ((now - _last).TotalMilliseconds < cooldownMs) return;
            _last = now;
            TrimCount++;
        }

        // 换后台线程做：GC / EmptyWorkingSet 都不需要 UI 线程，别在这儿堵界面
        System.Threading.Tasks.Task.Run(() => DoTrim(reason));
    }

    private static void DoTrim(string reason)
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

            // 工作集是**真实占用**，托管堆只是其中一部分 —— 两个数都记，
            // 才能分清"内存被托管对象占着"（GC 能收回来）还是"被非托管/常驻模块占着"（收了也不降）。
            Log($"第 {TrimCount} 次（{reason}）：工作集 {Mb(wsBefore):0.#} → {Mb(wsAfter):0.#} MB，" +
                $"托管堆 {Mb(before):0.#} → {Mb(after):0.#} MB");

            RaiseFloorIfNeeded(wsAfter);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[mem] 回收失败: " + ex.Message);
            Log("回收失败：" + ex.Message);
        }
    }

    /// <summary>收完还是那么高 → 那部分是刚需，把软水位抬上去，免得每 30 秒白收一次。</summary>
    private static void RaiseFloorIfNeeded(long wsAfter)
    {
        if (wsAfter <= _softFloor) return;

        var raised = Math.Min(wsAfter + SoftFloorPaddingBytes, HardWaterMarkBytes);
        if (raised <= _softFloor) return;

        _softFloor = raised;
        Log($"回收后工作集仍有 {Mb(wsAfter):0.#}MB（属于刚需部分）→ 软水位自适应抬到 {Mb(raised):0.#}MB");
    }

    /// <summary>
    /// 基线日志：每 5 分钟记一行"现在多少"（第一次调用就记，等于启动基线）。
    /// 工作集没到水位的时候，上面几条路一条都不会写日志 —— memory.log 会是空的。
    /// 而"到底涨到多少、涨得快不快"正是这条日志首先要回答的问题。
    /// </summary>
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

    /// <summary>
    /// 记一条"够水位了但没到停手条件"的观察日志（节流 60 秒）。
    /// 没有它，日志里只剩"回收了几次"，看不出什么时候涨起来、为什么没被收 ——
    /// 而"涨到半个 G"这类问题的第一问正是这个。
    /// </summary>
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

    /// <summary>
    /// 等界面渲染停稳再收（托盘 / 最小化那条路调它）。
    /// ⚠️ 真正决定收不收的是 <see cref="Trim"/> —— 这个延时只是让"刚最小化又马上还原"这种
    ///    一秒钟的来回不要白忙。
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

    /// <summary>进程当前工作集（64 位）。读不到返回 0 —— 调用方按"低于水位"处理即可。</summary>
    private static long CurrentWorkingSet()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return self.WorkingSet64;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 系统级"用户停手多久了"（毫秒）。读数失败返回 0（当作他正在操作 —— 宁可不收）。
    /// </summary>
    private static int HostIdleMs()
    {
        try
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return 0;

            // ⚠️ 必须用 uint 减法：Environment.TickCount 是 32 位、约 49.7 天回绕一次，
            //    直接相减会在回绕点得到一个巨大的负数（那一次就永远等不到"停手"）。
            var elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
            return elapsed > int.MaxValue ? 0 : (int)elapsed;
        }
        catch { return 0; }
    }

    private static bool SafeCall(Func<bool> f)
    {
        try { return f(); }
        catch { return false; }      // 判断不了就当作"看得见"（保守：只走水位那条路）
    }

    private static double Mb(long bytes) => bytes / 1048576.0;

    private static void Log(string message)
    {
        try
        {
            Core.AppLog.Info("memory", message);
        }
        catch { }
    }
}
