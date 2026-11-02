using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 「PowerPoint 触控优化」的本地存档（%LOCALAPPDATA%\ClassSoftwareHub\ppt-touch.json）。
///
/// <para>
/// ⚠️ 单独一份文件、不和 settings.json 混：这功能将来整个不要了，删掉本文件与
/// <see cref="PptTouch"/> / <c>PptTouchPage</c> 即可，不动用户其它设置。
/// </para>
/// </summary>
public sealed class PptTouchConfig
{
    public static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassSoftwareHub", "ppt-touch.json");

    /// <summary>总开关。默认**关** —— 实验性功能，改的是全局输入行为。</summary>
    public bool Enabled { get; set; }

    public static PptTouchConfig Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<PptTouchConfig>(File.ReadAllText(StorePath))
                       ?? new PptTouchConfig();
        }
        catch { /* 存档损坏就退回默认值 */ }

        return new PptTouchConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex)
        {
            AppLog.Warning("ppttouch", "保存设置失败：" + ex.Message);
        }
    }
}

/// <summary>
/// PowerPoint 幻灯片放映的「轻点翻页」（需求清单第 2 条，2026-11-01）。
///
/// <para>
/// 来由：教室里用一体机放 PPT，老师习惯像 WPS 那样轻点一下翻页，而 PowerPoint 放映时
/// 只认滑动。上游 PowerPoint-Touch-Assist（CC0）的思路是「轻点就补一个空格键」，
/// 但那样连点视频、音频也会翻页（它自己的 issue #5：「触屏点击视频、音频进行播放时也会翻页」），
/// 媒体根本暂停不下来 —— 需求点名不要这个毛病。
/// </para>
///
/// <para>
/// ⛔⛔ 这里的做法是**把轻点还原成一次鼠标点击**，而不是补空格：
/// 点空白处 PowerPoint 自己翻页；点视频 / 音频 / 按钮，PowerPoint 自己按点击处理（暂停、播放、按钮动作）。
/// 命中判断全部交给 PowerPoint，本程序不猜「点到的是什么」。
/// 滑动不拦、长按不拦 —— 它们仍走 PowerPoint 原有的触控逻辑。
/// </para>
///
/// <para>
/// 识别方式：WH_MOUSE_LL 全局鼠标钩子 + 只处理触屏/笔合成出来的鼠标消息
/// （<c>dwExtraInfo</c> 的低 24 位是 0xFF5157），并按「按下与抬起的位移、时长」区分轻点与滑动。
/// 钩子跑在独立线程的消息循环里 —— ⛔ 别放到 UI 线程，钩子回调卡住会拖慢整机输入。
/// </para>
/// </summary>
public static class PptTouch
{
    // ── 判定参数 ──────────────────────────────────────────────

    /// <summary>按下到抬起超过这个时长就不算轻点（长按要留给右键 / 书写）。</summary>
    private const long TapMaxMs = 700;

    /// <summary>允许的手指抖动，单位是**DIP**，按放映窗口所在屏幕的缩放换算成像素。</summary>
    private const double TapMaxDip = 16;

    // ── 运行状态 ──────────────────────────────────────────────

    private static Thread? _thread;
    private static uint _threadId;
    private static IntPtr _hook;
    private static Native.HookProc? _hookProc;
    private static volatile bool _active;
    private static long _taps;

    // 当前这次按下的记录（只在钩子线程读写）
    private static bool _tracking;
    private static int _startX;
    private static int _startY;
    private static long _startTick;
    private static double _scale;

    /// <summary>钩子是否已装上（页面用它显示运行状态）。</summary>
    public static bool IsActive => _active;

    /// <summary>本次运行期间由轻点触发的翻页次数。</summary>
    public static long Taps => Interlocked.Read(ref _taps);

    /// <summary>当前前台是不是 PowerPoint 的放映窗口（页面定时刷新状态用）。</summary>
    public static bool IsSlideshowShowing() => IsSlideshowForeground(out _, out _);

    /// <summary>按存档里的开关启动。已开着就什么都不做。</summary>
    public static void Start()
    {
        if (_active) return;

        // 上一次启动失败留下的死线程不算「已经在跑」，重试时要能重新起一个
        if (_thread is { IsAlive: true }) return;
        _thread = null;

        if (!PptTouchConfig.Load().Enabled) return;

        var thread = new Thread(HookThread)
        {
            IsBackground = true,
            Name = "ppttouch-hook",
        };
        _thread = thread;
        thread.Start();

        // 等钩子真正装上再返回：页面点完开关马上读状态，不能还停在「没开」
        // （装失败时线程会自己退出，这里也就不会再等满 1 秒）
        for (var i = 0; i < 100 && thread.IsAlive && !_active; i++) Thread.Sleep(10);
    }

    /// <summary>卸载钩子。没在跑就直接返回。</summary>
    public static void Stop()
    {
        var thread = _thread;
        if (thread is null) return;

        var id = _threadId;
        if (id != 0) Native.PostThreadMessage(id, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);

        thread.Join(1000);

        _thread = null;
        _threadId = 0;
        _tracking = false;
    }

    // ── 钩子线程 ──────────────────────────────────────────────

    private static void HookThread()
    {
        _threadId = Native.GetCurrentThreadId();

        // 先把线程的消息队列建出来：⚠️ 没有队列时 PostThreadMessage(WM_QUIT) 会失败，
        // 用户开关拨得太快的话 Stop() 就停不掉这个线程了。
        Native.PeekMessage(out _, IntPtr.Zero, 0, 0, Native.PM_NOREMOVE);

        _hookProc = OnMouse;
        _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _hookProc, Native.GetModuleHandle(null), 0);

        if (_hook == IntPtr.Zero)
        {
            AppLog.Warning("ppttouch", "安装鼠标钩子失败：" + Marshal.GetLastWin32Error());
            _threadId = 0;
            return;
        }

        _active = true;
        AppLog.Info("ppttouch", "鼠标钩子已安装，放映时轻点翻页待命");

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }

        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _hookProc = null;
        _active = false;
        AppLog.Info("ppttouch", "鼠标钩子已卸载");
    }

    private static IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                HandleMouse(wParam.ToInt32(), Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam));
            }
            catch (Exception ex)
            {
                // 钩子回调里抛异常会把整个钩子带走，这里一律吞掉只记一次
                AppLog.Exception("ppttouch", ex, "处理鼠标事件");
            }
        }

        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static void HandleMouse(int msg, Native.MSLLHOOKSTRUCT info)
    {
        if (msg is not (Native.WM_LBUTTONDOWN or Native.WM_LBUTTONUP)) return;

        // 注入事件不处理 —— 我们自己补的那次点击会原样再走一遍钩子
        if ((info.flags & Native.LLMHF_INJECTED) != 0) return;

        // 只认触屏 / 笔合成出来的鼠标消息：鼠标拖拽（例如拖视频进度条）不该翻页
        if (!IsTouchOrPen(info.dwExtraInfo)) return;

        if (msg == Native.WM_LBUTTONDOWN)
        {
            if (!IsSlideshowForeground(out _, out var scale))
            {
                _tracking = false;
                return;
            }

            _tracking = true;
            _startX = info.pt.x;
            _startY = info.pt.y;
            _startTick = Environment.TickCount64;
            _scale = scale;
            return;
        }

        // 抬起：不在跟踪中 = 按下时不在放映窗口（或者是滑动、长按）
        if (!_tracking) return;
        _tracking = false;

        var elapsed = Environment.TickCount64 - _startTick;
        if (elapsed > TapMaxMs) return;

        var limit = (int)Math.Ceiling(TapMaxDip * _scale);
        var dx = info.pt.x - _startX;
        var dy = info.pt.y - _startY;
        if ((long)dx * dx + (long)dy * dy > (long)limit * limit) return;

        // 抬起那一刻放映已经退出（按 Esc / 切窗口）就别再补点击
        if (!IsSlideshowForeground(out _, out _)) return;

        ClickAt(_startX, _startY);
        Interlocked.Increment(ref _taps);
    }

    /// <summary>
    /// 触屏 / 笔合成出来的鼠标消息，<c>dwExtraInfo</c> 形如 <c>0xFF5157xx</c>。
    /// </summary>
    private static bool IsTouchOrPen(IntPtr extraInfo) =>
        (extraInfo.ToInt64() & 0xFFFFFF00L) == 0xFF515700L;

    /// <summary>
    /// 前台窗口是 PowerPoint 的全屏放映窗口（窗口类名 <c>screenClass</c>）。
    /// ⚠️ 必须同时核对进程名：只看类名的话，别的程序起个同名窗口就能让本功能乱点。
    /// </summary>
    private static bool IsSlideshowForeground(out IntPtr hwnd, out double scale)
    {
        hwnd = Native.GetForegroundWindow();
        scale = 1.0;
        if (hwnd == IntPtr.Zero) return false;

        var cls = new StringBuilder(64);
        if (Native.GetClassName(hwnd, cls, cls.Capacity) == 0) return false;
        if (!string.Equals(cls.ToString(), "screenClass", StringComparison.OrdinalIgnoreCase)) return false;

        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (!IsPowerPointProcess(pid)) return false;

        var dpi = Native.GetDpiForWindow(hwnd);
        if (dpi > 0) scale = dpi / 96.0;
        return true;
    }

    private static bool IsPowerPointProcess(uint pid)
    {
        if (pid == 0) return false;

        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return false;

        try
        {
            var path = new StringBuilder(512);
            var size = path.Capacity;
            if (!Native.QueryFullProcessImageName(handle, 0, path, ref size)) return false;

            return string.Equals(Path.GetFileName(path.ToString()), "POWERPNT.EXE",
                                 StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>
    /// 在指定屏幕坐标补一次左键点击。用虚拟桌面坐标（绝对定位），多屏、不同缩放都对得上。
    /// </summary>
    private static void ClickAt(int x, int y)
    {
        var vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        var vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        var vw = Math.Max(1, Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN));
        var vh = Math.Max(1, Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));

        var nx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        var ny = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));

        const uint Common = Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK;
        const uint LeftDown = Common | Native.MOUSEEVENTF_LEFTDOWN;
        const uint LeftUp = Common | Native.MOUSEEVENTF_LEFTUP;

        var inputs = new[]
        {
            new Native.INPUT
            {
                type = Native.INPUT_MOUSE,
                U = new Native.InputUnion
                {
                    mi = new Native.MOUSEINPUT
                    {
                        dx = nx, dy = ny,
                        dwFlags = Common | Native.MOUSEEVENTF_MOVE,
                    },
                },
            },
            new Native.INPUT
            {
                type = Native.INPUT_MOUSE,
                U = new Native.InputUnion { mi = new Native.MOUSEINPUT { dx = nx, dy = ny, dwFlags = LeftDown } },
            },
            new Native.INPUT
            {
                type = Native.INPUT_MOUSE,
                U = new Native.InputUnion { mi = new Native.MOUSEINPUT { dx = nx, dy = ny, dwFlags = LeftUp } },
            },
        };

        var sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
        if (sent != inputs.Length)
            AppLog.Warning("ppttouch", $"补点击失败（SendInput 返回 {sent}）：{Marshal.GetLastWin32Error()}");
    }

    // ── 原生 ─────────────────────────────────────────────────

    private static class Native
    {
        public const int WH_MOUSE_LL = 14;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const uint WM_QUIT = 0x0012;
        public const uint PM_NOREMOVE = 0x0000;

        public const uint LLMHF_INJECTED = 0x00000001;

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;

        public const uint INPUT_MOUSE = 0;
        public const uint MOUSEEVENTF_MOVE = 0x0001;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
            public uint lPrivate;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PeekMessage(
            out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageName(
            IntPtr process, uint flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint count, INPUT[] inputs, int size);
    }
}
