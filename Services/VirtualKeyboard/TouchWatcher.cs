using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClassSoftwareHub.Desktop.Services.VirtualKeyboard;

/// <summary>
/// 盯着"有没有人用手指点屏幕"，记下落点。
///
/// <b>为什么要钩子</b>：Windows 不提供"全局触摸事件"接口，触摸会被合成成鼠标消息再发出去。
/// 合成出来的鼠标消息在 <c>MSLLHOOKSTRUCT.dwExtraInfo</c> 里带一个签名，靠它才能把
/// 「手指点的」和「鼠标点的」分开 —— 这正是设置里「只有触摸才弹」那条要求的实现基础。
///
/// <b>⛔⛔ 钩子装在一条专用的后台线程上，不是 UI 线程</b>（2026-09-30 事故的直接教训）：
/// 低级钩子意味着<b>全系统的鼠标事件都要先过装钩子那条线程</b>。上一版装在 UI 线程上，
/// 键盘一忙（重建键帽）整台机器的鼠标就跟着顿。现在这条线程只跑一个空转的消息循环 +
/// 每次回调只做两次整数位运算，跟 UI 彻底隔离。
///
/// <b>⛔ 回调里有三条纪律</b>：① 只记数据，绝不做耗时的事（超时会**被系统静默摘掉钩子**）；
/// ② 委托实例用静态字段一直持有，否则 GC 收掉之后回调跳野指针；
/// ③ <c>catch { }</c> 是**故意**的 —— 异常抛出去会连累整条系统输入链。
/// </summary>
internal static class TouchWatcher
{
    private const int WhMouseLowLevel = 14;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmQuit = 0x0012;

    /// <summary>触摸/笔合成的鼠标消息签名（<c>MI_WP_SIGNATURE</c>）。</summary>
    private const long SignatureMask = 0xFFFFFF00L;
    private const long SignatureValue = 0xFF515700L;

    /// <summary>签名低 8 位里这一位**置位 = 手指**，清零 = 笔。</summary>
    private const long TouchFlag = 0x80L;

    private static readonly object Gate = new();

    private static Thread? _thread;
    private static uint _threadId;
    private static IntPtr _hook;
    private static LowLevelMouseProc? _proc;   // ⛔ 必须静态持有，防 GC

    private static long _touchTick;            // 最近一次触摸的时间戳（TickCount64）
    private static int _touchX;
    private static int _touchY;
    private static long _touchCount;

    /// <summary>诊断用：见过的签名组合（只在钩子线程上访问，不用加锁）。</summary>
    private static readonly HashSet<long> SeenSignatures = [];

    public static bool IsRunning
    {
        get { lock (Gate) return _hook != IntPtr.Zero; }
    }

    public static long TouchCount => Interlocked.Read(ref _touchCount);

    // ── 生命周期 ────────────────────────────────────────────

    /// <summary>装钩子。重复调用是安全的。</summary>
    public static bool Start()
    {
        lock (Gate)
        {
            if (_hook != IntPtr.Zero) return true;

            _proc = HookProc;
            _thread = new Thread(ThreadProc)
            {
                IsBackground = true,
                Name = "CSH-TouchWatcher",
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();

            // 等钩子装好（最多 1 秒）
            for (var i = 0; i < 100 && _hook == IntPtr.Zero; i++)
                Thread.Sleep(10);

            var ok = _hook != IntPtr.Zero;
            VkbdLog.Write(ok ? "触摸监听：已启动（独立线程）" : "⚠️ 触摸监听：钩子没装上");
            return ok;
        }
    }

    /// <summary>摘钩子。功能关掉时必须调 —— 不留常驻钩子。</summary>
    public static void Stop()
    {
        lock (Gate)
        {
            if (_hook == IntPtr.Zero && _thread is null) return;

            if (_threadId != 0)
                _ = PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);

            _thread?.Join(500);
            _thread = null;
            _threadId = 0;
            _hook = IntPtr.Zero;

            VkbdLog.Write("触摸监听：已停止");
        }
    }

    // ── 给消费者用 ──────────────────────────────────────────

    /// <summary>
    /// 取一次"还没被处理过"的触摸。没有新的就返回 <c>false</c>。
    /// </summary>
    /// <param name="handledTick">调用方自己持有的水位（初始 0），取到之后会被更新。</param>
    public static bool TryTake(ref long handledTick, out int x, out int y)
    {
        var tick = Interlocked.Read(ref _touchTick);
        if (tick == 0 || tick == handledTick)
        {
            x = 0;
            y = 0;
            return false;
        }

        handledTick = tick;
        x = Volatile.Read(ref _touchX);
        y = Volatile.Read(ref _touchY);
        return true;
    }

    // ── 线程 + 钩子 ─────────────────────────────────────────

    private static void ThreadProc()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WhMouseLowLevel, _proc, GetModuleHandle(null), 0);

        if (_hook == IntPtr.Zero)
        {
            VkbdLog.Write($"⚠️ SetWindowsHookEx 失败，err={Marshal.GetLastWin32Error()}");
            return;
        }

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            _ = TranslateMessage(ref msg);
            _ = DispatchMessage(ref msg);
        }

        if (_hook != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && (uint)wParam == WmLButtonDown)
            {
                var info = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                var extra = info.ExtraInfo.ToInt64();

                if ((extra & SignatureMask) == SignatureValue)
                {
                    if ((extra & TouchFlag) != 0)
                    {
                        // 手指点的 —— 这就是我们要的那一下。
                        Volatile.Write(ref _touchX, info.Point.X);
                        Volatile.Write(ref _touchY, info.Point.Y);
                        Interlocked.Increment(ref _touchCount);
                        Interlocked.Exchange(ref _touchTick, Environment.TickCount64);
                    }
                    else
                    {
                        Note(extra & ~SignatureMask, info.Flags, "笔");
                    }
                }
                else
                {
                    // 真鼠标 / 被其它程序注入的（UU 远程那类）。⚠️ 这一种**不弹键盘**。
                    Note(extra, info.Flags, "鼠标或注入");
                }
            }
        }
        catch
        {
            // ⛔ 故意吞掉：异常抛出去会连累整条系统输入链。
        }

        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>每种"没见过"的签名只记一行 —— 日志不轮转，不能无条件刷。</summary>
    private static void Note(long key, uint flags, string kind)
    {
        var combined = (key & 0xFFFFFFL) | ((long)flags << 24);
        if (!SeenSignatures.Add(combined)) return;

        VkbdLog.Write($"触摸监听：把一类点击判为「{kind}」（extra=0x{key:X} flags=0x{flags:X}）");
    }

    // ── Win32 ───────────────────────────────────────────────

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public Point32 Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg32
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point32 Point;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg32 msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg32 msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg32 msg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
