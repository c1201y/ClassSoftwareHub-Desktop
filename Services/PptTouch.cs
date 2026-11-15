using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;









public sealed class PptTouchConfig
{
    public static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassSoftwareHub", "ppt-touch.json");

    
    public bool Enabled { get; set; }

    
    
    
    
    public bool CustomMenu { get; set; } = true;

    
    
    
    
    public List<string> MenuExtras { get; set; } = new();

    public static PptTouchConfig Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<PptTouchConfig>(File.ReadAllText(StorePath))
                       ?? new PptTouchConfig();
        }
        catch {  }

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


























public static class PptTouch
{
    

    
    private const double TapMaxDip = 16;

    
    
    
    
    
    
    
    
    
    private const int CursorNearPx = 32;

    
    
    
    
    private const double SwipeMinDip = 60;

    
    
    
    
    
    
    
    
    
    
    
    
    
    private const long TapMaxMs = 400;

    

    
    private const long LongPressMinMs = 450;

    
    
    
    
    private const long LongPressMaxMs = 2500;

    
    
    
    
    private const double LongPressMaxDip = 48;




    

    private static Thread? _thread;
    private static uint _threadId;
    private static IntPtr _hook;
    private static Native.HookProc? _hookProc;
    private static volatile bool _active;
    private static long _taps;

    
    
    
    
    private static volatile bool _customMenu = true;

    
    private static long _lastDiagTick;

    
    
    
    
    
    private static volatile int _lastTouchTick;

    
    private static IntPtr _touchHwnd;

    
    private static Native.WndProcDelegate? _touchWndProc;

    
    private static IntPtr _touchClassName;



    
    private static bool _tracking;
    private static int _startX;
    private static int _startY;
    private static long _startTick;
    private static double _scale;

    
    private static IntPtr _slideshowHwnd;

    
    public static bool IsActive => _active;

    
    public static long Taps => Interlocked.Read(ref _taps);

    
    public static bool IsSlideshowShowing() => FindSlideshowWindow() != IntPtr.Zero;

    
    
    
    
    
    
    
    
    
    
    
    
    private static IntPtr FindSlideshowWindow()
    {
        var found = IntPtr.Zero;

        try
        {
            Native.EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (!Native.IsWindowVisible(hwnd)) return true;

                    var cls = new StringBuilder(64);
                    if (Native.GetClassName(hwnd, cls, cls.Capacity) == 0) return true;
                    if (!string.Equals(cls.ToString(), "screenClass", StringComparison.OrdinalIgnoreCase))
                        return true;

                    Native.GetWindowThreadProcessId(hwnd, out var pid);
                    if (!IsPowerPointProcess(pid)) return true;

                    found = hwnd;
                    return false;   
                }
                catch
                {
                    return true;
                }
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            AppLog.Exception("ppttouch", ex, "枚举 PowerPoint 放映窗口");
        }

        return found;
    }

    
    
    
    
    
    public static void DescribePowerPointWindows()
    {
        var now = Environment.TickCount64;
        if (now - _lastDiagTick < 30_000) return;
        _lastDiagTick = now;

        var list = new List<string>();

        try
        {
            Native.EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (!Native.IsWindowVisible(hwnd)) return true;

                    Native.GetWindowThreadProcessId(hwnd, out var pid);
                    if (!IsPowerPointProcess(pid)) return true;

                    var cls = new StringBuilder(64);
                    Native.GetClassName(hwnd, cls, cls.Capacity);
                    Native.GetWindowRect(hwnd, out var r);
                    list.Add($"{cls}（{r.Right - r.Left}×{r.Bottom - r.Top}）");
                }
                catch { }

                return true;
            }, IntPtr.Zero);
        }
        catch { }

        AppLog.Info("ppttouch", list.Count == 0
            ? "未检测到放映：当前没有可见的 PowerPoint 窗口"
            : "未检测到放映：现有 PowerPoint 窗口 " + string.Join("、", list));
    }

    
    public static void Start()
    {
        if (_active) return;

        
        if (_thread is { IsAlive: true }) return;
        _thread = null;

        var config = PptTouchConfig.Load();
        _customMenu = config.CustomMenu;
        if (!config.Enabled) return;

        var thread = new Thread(HookThread)
        {
            IsBackground = true,
            Name = "ppttouch-hook",
        };
        _thread = thread;
        thread.Start();

        
        
        for (var i = 0; i < 100 && thread.IsAlive && !_active; i++) Thread.Sleep(10);

        
        if (_active) PptPointerState.Start();

    }

    
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

        PptPointerState.Stop();

    }

    
    
    
    
    public static void ApplyConfig()
    {
        _customMenu = PptTouchConfig.Load().CustomMenu;
    }

    
    public static IntPtr SlideshowWindow() => FindSlideshowWindow();

    
    
    
    
    
    
    
    
    
    public static void ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            if (Native.GetForegroundWindow() == hwnd) return;

            var fg = Native.GetForegroundWindow();
            var fgThread = fg == IntPtr.Zero ? 0u : Native.GetWindowThreadProcessId(fg, out _);
            var self = Native.GetCurrentThreadId();

            var attached = fgThread != 0 && fgThread != self
                           && Native.AttachThreadInput(self, fgThread, true);
            try
            {
                Native.SetForegroundWindow(hwnd);
                Native.SetFocus(hwnd);
            }
            finally
            {
                if (attached) Native.AttachThreadInput(self, fgThread, false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("ppttouch", "切回放映窗口失败：" + ex.Message);
        }
    }

    
    public static void RestoreSlideshowForeground()
    {
        var hwnd = FindSlideshowWindow();
        if (hwnd != IntPtr.Zero) ForceForeground(hwnd);
    }

    
    public static bool IsForeground(IntPtr hwnd) => hwnd != IntPtr.Zero && Native.GetForegroundWindow() == hwnd;

    
    
    
    
    public static void SendCombo(params ushort[] keys)
    {
        if (keys.Length == 0) return;

        var list = new List<Native.INPUT>(keys.Length * 2);
        foreach (var vk in keys) list.Add(KeyInput(vk, up: false));
        for (var i = keys.Length - 1; i >= 0; i--) list.Add(KeyInput(keys[i], up: true));

        var inputs = list.ToArray();
        var sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
        if (sent != inputs.Length)
            AppLog.Warning("ppttouch", $"补按键失败（SendInput 返回 {sent}）：{Marshal.GetLastWin32Error()}");
    }

    private static Native.INPUT KeyInput(ushort vk, bool up) => new()
    {
        type = Native.INPUT_KEYBOARD,
        U = new Native.InputUnion
        {
            ki = new Native.KEYBDINPUT
            {
                wVk = vk,
                dwFlags = up ? Native.KEYEVENTF_KEYUP : 0,
                dwExtraInfo = Native.OurInjectedMark,
            },
        },
    };



    
    
    
    
    
    
    
    
    
    
    
    
    
    private static void EnsureTouchWindow()
    {
        if (_touchHwnd != IntPtr.Zero) return;

        try
        {
            var hInstance = Native.GetModuleHandle(null);
            _touchWndProc = TouchWndProc;

            
            _touchClassName = Marshal.StringToHGlobalUni("CshPptTouchRawInput");

            var wc = new Native.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_touchWndProc),
                hInstance = hInstance,
                lpszClassName = _touchClassName,
            };
            Native.RegisterClassEx(ref wc);

            _touchHwnd = Native.CreateWindowEx(
                0, "CshPptTouchRawInput", "", Native.WS_POPUP, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            if (_touchHwnd == IntPtr.Zero)
            {
                AppLog.Warning("ppttouch", "创建触摸监听窗口失败：" + Marshal.GetLastWin32Error());
                return;
            }

            var devices = new[]
            {
                new Native.RAWINPUTDEVICE
                {
                    usUsagePage = Native.HID_USAGE_PAGE_DIGITIZER,
                    usUsage = Native.HID_USAGE_DIGITIZER_TOUCH_SCREEN,
                    dwFlags = Native.RIDEV_INPUTSINK,
                    hwndTarget = _touchHwnd,
                },
            };

            if (Native.RegisterRawInputDevices(
                    devices, (uint)devices.Length, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>()))
            {
                AppLog.Info("ppttouch", "触摸 Raw Input 已注册（用来区分手指与鼠标）");
            }
            else
            {
                AppLog.Warning("ppttouch", "注册触摸 Raw Input 失败：" + Marshal.GetLastWin32Error());
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("ppttouch", ex, "建立触摸监听");
        }
    }

    private static IntPtr TouchWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Native.WM_INPUT)
        {
            
            _lastTouchTick = Environment.TickCount;
        }

        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    

    private static void HookThread()
    {
        _threadId = Native.GetCurrentThreadId();

        
        
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

        
        EnsureTouchWindow();

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
                var msg = wParam.ToInt32();
                var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);

                
                if (SwallowRightButton(msg, info)) return 1;

                
                if (PptMenu.IsBlocking && SwallowOutsideMenuClick(msg, info)) return 1;

                
                
                var started = Stopwatch.GetTimestamp();
                HandleMouse(msg, info);
                var costMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                if (costMs >= 3.0) LogAsync($"钩子回调耗时 {costMs:0.0}ms（msg=0x{msg:X}）");
            }
            catch (Exception ex)
            {
                
                AppLog.Exception("ppttouch", ex, "处理鼠标事件");
            }
        }

        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private static bool SwallowRightButton(int msg, Native.MSLLHOOKSTRUCT info)
    {
        if (msg is not (Native.WM_RBUTTONDOWN or Native.WM_RBUTTONUP)) return false;

        
        if (info.dwExtraInfo == Native.OurInjectedMark) return false;

        
        if (!IsSlideshowForeground(out _, out _)) return false;

        
        if (msg == Native.WM_RBUTTONDOWN && !PptMenu.IsBlocking && _customMenu)
        {
            AppLog.Info("ppttouch", $"放映中收到右键（flags=0x{info.flags:X}），改弹本程序菜单");
            PptMenu.ShowAt(info.pt.x, info.pt.y);
        }

        return true;
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private static bool SwallowOutsideMenuClick(int msg, Native.MSLLHOOKSTRUCT info)
    {
        if (msg is not (Native.WM_LBUTTONDOWN or Native.WM_LBUTTONUP)) return false;

        _tracking = false;

        if (IsOverOwnWindow(info.pt)) return false;     

        if (msg == Native.WM_LBUTTONDOWN)
        {
            AppLog.Info("ppttouch", "点在菜单外面，收起菜单");
            PptMenu.MarkDismissed();
            PptMenu.HideMenu();
        }

        return true;                                    
    }

    
    private static bool IsOverOwnWindow(Native.POINT pt)
    {
        var hwnd = Native.WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return false;

        Native.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    private static void HandleMouse(int msg, Native.MSLLHOOKSTRUCT info)
    {
        if (msg is not (Native.WM_LBUTTONDOWN or Native.WM_LBUTTONUP)) return;

        
        
        
        
        if (info.dwExtraInfo == Native.OurInjectedMark) return;

        
        if (PptMenu.IsBlocking)
        {
            _tracking = false;
            return;
        }

        
        
        if (unchecked(Environment.TickCount64 - PptMenu.LastInvokeTick) < 400)
        {
            _tracking = false;
            return;
        }

        
        
        
        var touched = unchecked(Environment.TickCount - _lastTouchTick) <= 800;

        
        
        var isTouch = IsTouchOrPen(info.dwExtraInfo) || touched || _tracking;
        if (!isTouch) return;

        if (msg == Native.WM_LBUTTONDOWN)
        {
            if (!IsSlideshowForeground(out var fgHwnd, out var scale))
            {
                _tracking = false;
                DiagNotSlideshow(fgHwnd);
                return;
            }

            
            
            
            
            if (PptPointerState.IsToolMode)
            {
                _tracking = false;
                DiagToolMode();
                return;
            }

            _tracking = true;
            _startX = info.pt.x;
            _startY = info.pt.y;
            _startTick = Environment.TickCount64;
            _scale = scale;
            _slideshowHwnd = fgHwnd;   
            return;
        }

        
        if (!_tracking)
        {
            LogAsync("抬起被丢弃：这次按下没有进入跟踪（按下时前台不是放映窗口）");
            return;
        }

        _tracking = false;

        var dx = info.pt.x - _startX;
        var dy = info.pt.y - _startY;
        var elapsed = Environment.TickCount64 - _startTick;

        
        
        
        
        
        var endHwnd = Native.GetForegroundWindow();
        if (endHwnd != _slideshowHwnd)
        {
            DiagLeftSlideshow(endHwnd);
            return;
        }

        var limit = (int)Math.Ceiling(TapMaxDip * _scale);
        var movedSq = (long)dx * dx + (long)dy * dy;

        
        
        
        
        if (elapsed <= TapMaxMs && movedSq <= (long)limit * limit)
        {
            SendTapAt(_startX, _startY);
            Interlocked.Increment(ref _taps);
            LogAsync($"轻点：({_startX},{_startY})　{elapsed}ms　位移 {Math.Sqrt(movedSq):0.#}px");
            return;
        }

        
        
        
        var swipe = (int)Math.Ceiling(SwipeMinDip * _scale);
        if (Math.Abs(dy) >= swipe && Math.Abs(dy) > Math.Abs(dx))
        {
            var next = dy < 0;
            SendKey(next ? Native.VK_NEXT : Native.VK_PRIOR);
            Interlocked.Increment(ref _taps);
            LogAsync($"滑动翻{(next ? "下一页" : "上一页")}：({_startX},{_startY})→({info.pt.x},{info.pt.y})　{elapsed}ms");
            return;
        }

        
        
        
        
        if (_customMenu && elapsed >= LongPressMinMs && elapsed <= LongPressMaxMs)
        {
            var longLimit = (int)Math.Ceiling(LongPressMaxDip * _scale);
            if (movedSq <= (long)longLimit * longLimit)
            {
                LogAsync($"长按 {elapsed}ms，在 ({_startX},{_startY}) 弹出菜单");
                PptMenu.ShowAt(_startX, _startY);
                return;
            }
        }

        
        
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private static void SendTapAt(int x, int y)
    {
        var needMove = true;

        if (Native.GetCursorPos(out var cur))
        {
            long ddx = cur.x - x;
            long ddy = cur.y - y;
            needMove = ddx * ddx + ddy * ddy > (long)CursorNearPx * CursorNearPx;
        }

        var list = new System.Collections.Generic.List<Native.INPUT>(3);

        if (needMove)
        {
            var (mx, my) = ToAbsolute(x, y);
            list.Add(new Native.INPUT
            {
                type = Native.INPUT_MOUSE,
                U = new Native.InputUnion
                {
                    mi = new Native.MOUSEINPUT
                    {
                        dx = mx,
                        dy = my,
                        dwFlags = Native.MOUSEEVENTF_MOVE
                                | Native.MOUSEEVENTF_ABSOLUTE
                                | Native.MOUSEEVENTF_VIRTUALDESK,
                        dwExtraInfo = Native.OurInjectedMark,
                    },
                },
            });
        }

        
        list.Add(new Native.INPUT
        {
            type = Native.INPUT_MOUSE,
            U = new Native.InputUnion
            {
                mi = new Native.MOUSEINPUT
                {
                    dwFlags = Native.MOUSEEVENTF_LEFTDOWN,
                    dwExtraInfo = Native.OurInjectedMark,
                },
            },
        });

        list.Add(new Native.INPUT
        {
            type = Native.INPUT_MOUSE,
            U = new Native.InputUnion
            {
                mi = new Native.MOUSEINPUT
                {
                    dwFlags = Native.MOUSEEVENTF_LEFTUP,
                    dwExtraInfo = Native.OurInjectedMark,
                },
            },
        });

        var inputs = list.ToArray();
        var sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
        if (sent != inputs.Length)
            AppLog.Warning("ppttouch", $"补单击失败（SendInput 返回 {sent}）：{Marshal.GetLastWin32Error()}");
    }

    
    private static (int X, int Y) ToAbsolute(int x, int y)
    {
        var vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        var vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        var vw = Math.Max(1, Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN));
        var vh = Math.Max(1, Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));

        return (
            (int)Math.Round((x - vx) * 65535.0 / (vw - 1)),
            (int)Math.Round((y - vy) * 65535.0 / (vh - 1)));
    }

    
    
    
    
    
    
    
    
    
    private static void SendKey(ushort vk) => SendCombo(vk);

    
    
    
    
    
    private static void LogAsync(string message) =>
        ThreadPool.QueueUserWorkItem(static m => AppLog.Info("ppttouch", (string)m!), message);

    
    private static void DiagLeftSlideshow(IntPtr hwnd)
    {
        var now = Environment.TickCount64;
        if (now - _lastDiagTick < 30_000) return;
        _lastDiagTick = now;

        var cls = new StringBuilder(64);
        Native.GetClassName(hwnd, cls, cls.Capacity);
        LogAsync($"抬起被丢弃：前台已不是放映窗口（现在是「{cls}」）");
    }

    
    
    
    
    private static void DiagToolMode()
    {
        var now = Environment.TickCount64;
        if (now - _lastDiagTick < 30_000) return;
        _lastDiagTick = now;

        AppLog.Info("ppttouch", $"工具模式（指针类型 {PptPointerState.Current}），本次触摸不介入手势");
    }

    private static void DiagNotSlideshow(IntPtr hwnd)    {
        var now = Environment.TickCount64;
        if (now - _lastDiagTick < 30_000) return;
        _lastDiagTick = now;

        var cls = new StringBuilder(64);
        Native.GetClassName(hwnd, cls, cls.Capacity);
        Native.GetWindowThreadProcessId(hwnd, out var pid);

        var path = new StringBuilder(512);
        var size = path.Capacity;
        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle != IntPtr.Zero)
        {
            try { Native.QueryFullProcessImageName(handle, 0, path, ref size); }
            finally { Native.CloseHandle(handle); }
        }

        AppLog.Info("ppttouch",
            $"收到触屏点击，但前台不是放映窗口：类名「{cls}」，进程「{Path.GetFileName(path.ToString())}」");
    }

    
    
    
    private static bool IsTouchOrPen(IntPtr extraInfo) =>
        (extraInfo.ToInt64() & 0xFFFFFF00L) == 0xFF515700L;

    
    
    
    
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

    private static uint _cachedPptPid;
    private static bool _cachedPptResult;

    private static bool IsPowerPointProcess(uint pid)
    {
        if (pid == 0) return false;

        
        
        if (pid == _cachedPptPid) return _cachedPptResult;

        var result = QueryIsPowerPointProcess(pid);
        _cachedPptPid = pid;
        _cachedPptResult = result;
        return result;
    }

    private static bool QueryIsPowerPointProcess(uint pid)
    {
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

    

    private static class Native
    {
        public const int WH_MOUSE_LL = 14;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;

        
        
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_RBUTTONUP = 0x0205;
        public const uint WM_QUIT = 0x0012;
        public const uint PM_NOREMOVE = 0x0000;

        public const uint LLMHF_INJECTED = 0x00000001;

        
        
        
        
        
        public static readonly IntPtr OurInjectedMark = new(0x0C5CA5E);

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;

        public const uint INPUT_MOUSE = 0;
        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_KEYUP = 0x0002;

        
        public const ushort VK_SPACE = 0x20;

        
        public const ushort VK_NEXT = 0x22;

        
        public const ushort VK_PRIOR = 0x21;
        public const uint MOUSEEVENTF_MOVE = 0x0001;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;

        
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
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

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
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

        
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AttachThreadInput(
            uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT pt);

        
        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT point);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        
        
        

        public const uint WM_INPUT = 0x00FF;
        public const uint WS_POPUP = 0x80000000;
        public const uint RIDEV_INPUTSINK = 0x00000100;
        public const ushort HID_USAGE_PAGE_DIGITIZER = 0x0D;
        public const ushort HID_USAGE_DIGITIZER_TOUCH_SCREEN = 0x04;
        public const int HWND_MESSAGE = -3;

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterRawInputDevices(
            RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

        public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            
            public IntPtr lpszMenuName;
            public IntPtr lpszClassName;
            public IntPtr hIconSm;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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
