using System;
using System.Runtime.InteropServices;

namespace ClassSoftwareHub.Desktop.Services;









public static class TeachingActions
{
    
    
    public static bool IsHold(string id) => false;

    
    private static IntPtr _lastForeign = IntPtr.Zero;

    
    private static readonly List<IntPtr> _pendingCloseAll = new();

    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _focusWatcher;

    
    public static string? Begin(string id)
    {
        try
        {
            switch (id)
            {
                
                
                case "explorer": return OpenExplorer();
                case "run": RunDialog(); break;
                case "osk": return OpenOnScreenKeyboard();
                
                case "lock": LockScreen(); break;

                
                case "taskmgr": OpenTaskManager(); break;
                case "clipboard": ClipboardHistory(); break;
                case "switchuser": SwitchUser(); break;
                case "logout": return PrepareLogout();
                case "mag": ToggleSystemMagnifier(); break;
                case "taskview": TaskView(); break;
                case "showdesktop": ShowDesktop(); break;
                case "minall": MinimizeAll(); break;
                case "closefg": CloseForegroundApp(); break;
                case "closeall": return PrepareCloseAll();
                case "screenshot": return StartScreenshot();
                
                
                case "cancelshutdown": return Data.AutoShutdown.CancelPending();
                
                
                case "killseewo": return KillEasiNote();
                case "shutdown": return PrepareShutdown();
                default: Log($"不认识的动作: {id}"); break;
            }
        }
        catch (Exception ex)
        {
            Log($"{id} 失败: " + ex.Message);
            return "操作失败，详见日志";
        }

        return null;
    }

    
    public static void End(string id)
    {
        if (!IsHold(id)) return;
        
    }

    
    
    
    
    
    
    
    
    public static string? Run(string id)
    {
        var previous = TargetForeground();
        var hint = Begin(id);

        if (!AsksFirst(id)) ReturnFocus(previous);

        return hint;
    }

    
    
    
    
    
    
    
    public static bool AsksFirst(string id) => id is "closeall" or "logout" or "shutdown";

    
    
    
    
    
    public static void StartFocusWatcher()
    {
        if (_focusWatcher is not null) return;
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is null) return;

        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(300);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            try
            {
                var fg = GetForegroundWindow();
                if (EligibleAppWindow(fg)) _lastForeign = fg;
            }
            catch { }
        };
        timer.Start();
        _focusWatcher = timer;
    }

    
    private static IntPtr TargetForeground()
    {
        var fg = GetForegroundWindow();
        if (EligibleAppWindow(fg)) return fg;
        if (_lastForeign != IntPtr.Zero && EligibleAppWindow(_lastForeign)) return _lastForeign;
        return IntPtr.Zero;
    }

    

    
    private static void ShowDesktop()
    {
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is not null)
            {
                var shell = Activator.CreateInstance(type);
                type.InvokeMember("ToggleDesktop", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
                Log("回桌面：COM OK，之后前台=" + ClassNameOf(GetForegroundWindow()));
                return;
            }
        }
        catch (Exception ex)
        {
            Log("回桌面（COM）失败，改用 Win+D: " + ex.Message);
        }
        SendWinChord(VkD);
    }

    

    
    
    
    
    private static void OpenTaskManager()
    {
        try
        {
            var exe = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "taskmgr.exe");
            if (!System.IO.File.Exists(exe))
            {
                Log("任务管理器：找不到 " + exe);
                return;
            }

            _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
            Log("任务管理器：已调起");
        }
        catch (Exception ex)
        {
            Log("任务管理器失败: " + ex.Message);
        }
    }

    

    
    
    
    
    
    
    
    
    
    private static string? OpenExplorer()
    {
        try
        {
            var exe = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            if (!System.IO.File.Exists(exe))
            {
                Log("资源管理器：找不到 " + exe);
                return "找不到文件资源管理器，详见日志";
            }

            
            
            
            _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "shell:MyComputerFolder")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Log("资源管理器：已调起");
            return null;
        }
        catch (Exception ex)
        {
            Log("资源管理器失败: " + ex.Message);
            return "打开文件资源管理器失败，详见日志";
        }
    }

    
    
    
    
    
    
    
    
    private static void RunDialog()
    {
        RefocusPrevious();

        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            SendWinChord(VkR);
            return;
        }

        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(150);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            try
            {
                timer.Stop();
                var sent = SendWinChord(VkR);
                Log($"运行对话框：SendInput 插入 {sent} 个事件" + (sent == 0 ? $"（被拦了？err={Marshal.GetLastWin32Error()}）" : ""));
            }
            catch { }
        };
        timer.Start();
    }

    
    
    
    
    
    
    
    
    
    
    
    
    private static string? OpenOnScreenKeyboard()
    {
        try
        {
            foreach (var existing in System.Diagnostics.Process.GetProcessesByName("osk"))
            {
                if (existing.MainWindowHandle != IntPtr.Zero)
                {
                    SetForegroundWindow(existing.MainWindowHandle);
                    Log("屏幕键盘：已在前台，未重复启动");
                    return null;
                }
            }

            var exe = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "osk.exe");
            if (!System.IO.File.Exists(exe))
            {
                Log("屏幕键盘：找不到 " + exe);
                return "这台电脑上找不到系统自带的屏幕键盘";
            }

            _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
            Log("屏幕键盘：已调起");
            return null;
        }
        catch (Exception ex)
        {
            
            Log("屏幕键盘失败: " + ex.Message);
            return "屏幕键盘启动失败，详见日志";
        }
    }

    
    
    
    
    
    
    
    private static void ClipboardHistory()
    {
        RefocusPrevious();

        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            SendWinChord(VkV);
            return;
        }

        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(150);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            try
            {
                timer.Stop();
                var sent = SendWinChord(VkV);
                Log($"剪贴板历史：SendInput 插入 {sent} 个事件" + (sent == 0 ? $"（被拦了？err={Marshal.GetLastWin32Error()}）" : ""));
            }
            catch { }
        };
        timer.Start();
    }

    
    
    
    
    
    
    
    private static string KillEasiNote()
    {
        var r = Data.EasiNoteGuard.RunOnce(dryRun: false);

        
        Log($"杀希沃（侧边栏手点）：{r.Summary}"
            + (r.KilledNames.Count > 0 ? " 清掉=" + string.Join("、", r.KilledNames) : "")
            + (r.SkippedNames.Count > 0 ? " 放过=" + string.Join("、", r.SkippedNames) : "")
            + (r.Errors.Count > 0 ? " 失败=" + string.Join("；", r.Errors) : ""));

        return r.Summary;
    }

    
    
    
    
    private static string? PrepareShutdown()
    {
        var mode = App.Settings.Current.ShutdownMode;
        return NormalizeShutdownMode(mode) == Data.ShutdownMethods.Slide
            ? "确认关机？\n将打开系统「滑动关机」界面，下滑滑块才会真正关闭计算机"
            : "确认关机？\n将立即关闭计算机，未保存的工作内容会丢失";
    }

    
    private static string? ExecuteShutdown()
    {
        try
        {
            Data.AutoShutdown.ShutdownNow(App.Settings.Current.ShutdownMode);
        }
        catch (Exception ex)
        {
            Log("关机失败: " + ex.Message);
        }

        return null;
    }

    
    private static string NormalizeShutdownMode(string? mode) =>
        string.Equals(mode, Data.ShutdownMethods.Shutdown, StringComparison.OrdinalIgnoreCase)
            ? Data.ShutdownMethods.Shutdown
            : Data.ShutdownMethods.Slide;

    
    private static void LockScreen()
    {
        try
        {
            var ok = LockWorkStation();
            Log("锁定电脑：" + (ok ? "已下发" : $"失败 err={Marshal.GetLastWin32Error()}"));
        }
        catch (Exception ex)
        {
            Log("锁定电脑失败: " + ex.Message);
        }
    }

    
    
    
    
    private static void SwitchUser()
    {
        try
        {
            var exe = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "tsdiscon.exe");
            if (!System.IO.File.Exists(exe))
            {
                Log("切换用户：找不到 " + exe);
                return;
            }

            _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
            Log("切换用户：已调起 tsdiscon.exe");
        }
        catch (Exception ex)
        {
            Log("切换用户失败: " + ex.Message);
        }
    }

    
    private static string? PrepareLogout() =>
        "注销当前用户\n所有程序都会被关掉，没有保存的内容会丢失";

    
    private static string? ExecuteLogout()
    {
        try
        {
            _ = EnableShutdownPrivilege();       
            var ok = ExitWindowsEx(EWX_LOGOFF, 0);
            Log("注销：" + (ok ? "已下发" : $"失败 err={Marshal.GetLastWin32Error()}"));
        }
        catch (Exception ex)
        {
            Log("注销失败: " + ex.Message);
        }

        return null;
    }

    
    private static bool EnableShutdownPrivilege()
    {
        const uint TokenAdjustPrivileges = 0x0020;
        const uint TokenQuery = 0x0008;
        const uint SePrivilegeEnabled = 0x0002;

        var token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out token)) return false;
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid)) return false;

            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)
                   && Marshal.GetLastWin32Error() == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    
    private static void TaskView()
    {
        var sent = SendWinChord(VkTab);
        Log($"任务视图：SendInput 插入 {sent} 个事件" + (sent == 0 ? $"（被拦了？err={Marshal.GetLastWin32Error()}）" : ""));
    }

    
    
    
    
    private static string? StartScreenshot()
    {
        try
        {
            Views.ToolSidebarWindow.CollapseForCapture();
            Views.SnipOverlayWindow.Begin(240);
            Log("截屏：已请求打开框选层");
        }
        catch (Exception ex)
        {
            Log("截屏启动失败: " + ex.Message);
            return "截屏启动失败，详见日志";
        }

        return null;
    }

    
    public static void RefocusPrevious()
    {
        var back = TargetForeground();
        if (back != IntPtr.Zero) ReturnFocus(back);
    }

    
    
    
    
    
    private static void ToggleSystemMagnifier()
    {
        try
        {
            var running = System.Diagnostics.Process.GetProcessesByName("Magnify");
            if (running.Length > 0)
            {
                var ok = 0;
                foreach (var p in running)
                {
                    try
                    {
                        if (p.CloseMainWindow()) ok++;
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
                Log($"系统放大镜：关闭（{ok}/{running.Length} 个窗口收到关闭消息）");
                return;
            }

            var exe = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "Magnify.exe");
            if (!System.IO.File.Exists(exe))
            {
                Log("系统放大镜：找不到 " + exe);
                return;
            }

            _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
            Log("系统放大镜：已调起（稍后切镜头模式）");

            
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (queue is null) return;

            var timer = queue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(700);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                try
                {
                    timer.Stop();
                    var sent = SendChord(VkControl, VkMenu, VkL);
                    Log($"系统放大镜：切镜头模式 SendInput 插入 {sent} 个事件" + (sent == 0 ? "（被拦了？）" : ""));
                }
                catch { }
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            Log("系统放大镜失败: " + ex.Message);
        }
    }

    
    
    
    
    
    private static string? PrepareCloseAll()
    {
        var found = new List<IntPtr>();
        _ = EnumWindows((hwnd, _) =>
        {
            if (EligibleForBulkClose(hwnd)) found.Add(RootWindowOf(hwnd));
            return true;
        }, IntPtr.Zero);

        found = found.Distinct().ToList();
        _pendingCloseAll.Clear();

        if (found.Count == 0)
        {
            Log("关闭全部：没有可关的窗口");
            return "无可关闭的窗口";
        }

        _pendingCloseAll.AddRange(found);

        var titles = found.Select(TitleOf).Where(t => t.Length > 0).ToList();
        var sample = string.Join("、", titles.Take(3));
        if (titles.Count > 3) sample += " 等";

        Log($"关闭全部：待确认 {found.Count} 个 → " + string.Join(" / ", found.Take(12).Select(TitleOf)));
        return $"将关闭 {found.Count} 个窗口\n含最小化的窗口：{sample}";
    }

    
    private static string? ExecuteCloseAll()
    {
        var list = _pendingCloseAll.ToList();
        _pendingCloseAll.Clear();

        if (list.Count == 0) return null;

        var ok = 0;
        foreach (var h in list)
        {
            try
            {
                if (PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero)) ok++;
            }
            catch { }
        }

        Log($"关闭全部：{ok}/{list.Count} 个窗口收到关闭消息");
        return null;
    }

    
    public static string? ConfirmPending(string id)
    {
        try
        {
            return id switch
            {
                "closeall" => ExecuteCloseAll(),
                "logout" => ExecuteLogout(),
                "shutdown" => ExecuteShutdown(),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            Log($"{id} 确认执行失败: " + ex.Message);
            return null;
        }
    }

    
    private static bool EligibleForBulkClose(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return false;
            if (!IsWindowVisible(hwnd)) return false;
            if (IsOurApp(hwnd)) return false;
            if (GetWindow(hwnd, GwOwner) != IntPtr.Zero) return false;
            if ((GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExToolWindow) != 0) return false;

            var cls = ClassNameOf(hwnd);
            if (ShellClassNames.Contains(cls)) return false;

            return TitleOf(hwnd).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static readonly HashSet<string> ShellClassNames = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow_WASDK",
        "ShellHandwritingCanvas", "DuiHostWnd", "ThumbnailDeviceHelperWnd",
        
        "IME", "MSCTFIME UI", "Default IME", "CicMarshalWnd",
        "EdgeUiInputTopWndClass", "Shell_InputSwitchTopLevelWindow", "ApplicationFrameInputSinkWindow",
    };

    
    private static void CloseForegroundApp()
    {
        var hwnd = ResolveTargetWindow();
        if (hwnd == IntPtr.Zero)
        {
            Log("关前台：没找到能关的前台窗口");
            return;
        }

        var cls = ClassNameOf(hwnd);
        var ttl = TitleOf(hwnd);
        _ = GetWindowThreadProcessId(hwnd, out var pid);

        if (!PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero))
        {
            Log($"关前台：发送失败 err={Marshal.GetLastWin32Error()} → {ttl} [{cls}]");
            return;
        }

        Log($"关前台：{ttl} [{cls}] pid={pid}");
    }

    
    
    
    
    
    
    
    
    private static IntPtr ResolveTargetWindow()
    {
        var fg = GetForegroundWindow();
        if (EligibleAppWindow(fg)) return RootWindowOf(fg);

        var found = IntPtr.Zero;
        _ = EnumWindows((hwnd, _) =>
        {
            if (!EligibleAppWindow(hwnd)) return true;
            found = hwnd;
            return false;                       
        }, IntPtr.Zero);

        return found == IntPtr.Zero ? IntPtr.Zero : RootWindowOf(found);
    }

    
    private static bool EligibleAppWindow(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return false;
            if (!IsWindowVisible(hwnd)) return false;
            if (IsIconic(hwnd)) return false;                        
            if (IsOurApp(hwnd)) return false;                        
            if (GetWindow(hwnd, GwOwner) != IntPtr.Zero) return false;   
            if ((GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExToolWindow) != 0) return false;

            var cls = ClassNameOf(hwnd);
            if (ShellClassNames.Contains(cls)) return false;

            return TitleOf(hwnd).Length > 0;                          
        }
        catch
        {
            return false;
        }
    }

    
    private static IntPtr RootWindowOf(IntPtr hwnd)
    {
        try
        {
            var root = GetAncestor(hwnd, GaRootOwner);
            return root == IntPtr.Zero ? hwnd : root;
        }
        catch
        {
            return hwnd;
        }
    }

    
    private static void MinimizeAll()
    {
        var count = 0;
        var detail = new System.Text.StringBuilder();
        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hwnd)) return true;
                if (IsIconic(hwnd)) return true;
                if (IsOurApp(hwnd)) return true;
                if ((GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExToolWindow) != 0) return true;
                if (GetWindow(hwnd, GwOwner) != IntPtr.Zero) return true;       
                var cls = ClassNameOf(hwnd);
                if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;

                var ok = ShowWindow(hwnd, SW_MINIMIZE);
                count++;
                if (detail.Length < 300) detail.Append($"[{TitleOf(hwnd)}/{cls}→{ok}] ");
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        Log($"最小化全部：{count} 个窗口 {detail}");
    }

    
    private static void ReturnFocus(IntPtr previous)
    {
        if (previous == IntPtr.Zero) return;
        try
        {
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            queue?.TryEnqueue(() =>
            {
                try
                {
                    var now = GetForegroundWindow();
                    if (now == IntPtr.Zero || now == previous) return;
                    if (!IsOwnWindow(now)) return;              
                    SetForegroundWindow(previous);
                }
                catch { }
            });
        }
        catch { }
    }

    

    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const uint WM_CLOSE = 0x0010;
    private const int SW_MINIMIZE = 6;
    private const uint GwOwner = 4;

    private const ushort VkLWin = 0x5B;
    private const ushort VkTab = 0x09;
    private const ushort VkD = 0x44;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;      
    private const ushort VkL = 0x4C;
    private const ushort VkV = 0x56;         
    private const ushort VkR = 0x52;         
    private const uint EWX_LOGOFF = 0x00000000;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static bool _inputSizeLogged;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;

        
        public KEYBDINPUT ki;

        
        
        public ulong pad;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private const uint GaRootOwner = 3;

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    private static bool IsOwnWindow(IntPtr hwnd)
    {
        try
        {
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            return pid == (uint)Environment.ProcessId;
        }
        catch
        {
            return false;
        }
    }

    private static readonly System.Collections.Generic.Dictionary<uint, bool> _ourPidCache = new();

    
    
    
    
    private static bool IsOurApp(IntPtr hwnd)
    {
        try
        {
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == (uint)Environment.ProcessId) return true;
            if (_ourPidCache.TryGetValue(pid, out var cached)) return cached;

            var ours = false;
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                ours = string.Equals(p.ProcessName, "ClassSoftwareHub", StringComparison.OrdinalIgnoreCase);
            }
            catch { }

            _ourPidCache[pid] = ours;
            return ours;
        }
        catch
        {
            return false;
        }
    }

    private static string ClassNameOf(IntPtr hwnd)
    {
        try
        {
            var sb = new System.Text.StringBuilder(256);
            _ = GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }

    
    private static uint SendWinChord(ushort vk)
    {
        try
        {
            var inputs = new[]
            {
                Key(VkLWin, false),
                Key(vk, false),
                Key(vk, true),
                Key(VkLWin, true),
            };
            var cb = Marshal.SizeOf<INPUT>();
            var sent = SendInput((uint)inputs.Length, inputs, cb);
            if (sent == 0 && !_inputSizeLogged)
            {
                _inputSizeLogged = true;
                Log($"模拟按键被拒：INPUT 大小={cb}（x64 应为 40）err={Marshal.GetLastWin32Error()}");
            }
            return sent;
        }
        catch (Exception ex)
        {
            Log("模拟按键失败: " + ex.Message);
            return 0;
        }
    }

    
    private static uint SendChord(params ushort[] keys)
    {
        try
        {
            var list = new System.Collections.Generic.List<INPUT>();
            foreach (var k in keys) list.Add(Key(k, false));
            for (var i = keys.Length - 1; i >= 0; i--) list.Add(Key(keys[i], true));

            var inputs = list.ToArray();
            var cb = Marshal.SizeOf<INPUT>();
            var sent = SendInput((uint)inputs.Length, inputs, cb);
            if (sent == 0 && !_inputSizeLogged)
            {
                _inputSizeLogged = true;
                Log($"模拟按键被拒：INPUT 大小={cb}（x64 应为 40）err={Marshal.GetLastWin32Error()}");
            }
            return sent;
        }
        catch (Exception ex)
        {
            Log("模拟按键失败: " + ex.Message);
            return 0;
        }
    }

    private static string TitleOf(IntPtr hwnd)
    {
        try
        {
            var sb = new System.Text.StringBuilder(128);
            _ = GetWindowTextW(hwnd, sb, sb.Capacity);
            var t = sb.ToString().Replace('\n', ' ').Replace('\r', ' ');
            return t.Length > 28 ? t[..28] : t;
        }
        catch
        {
            return "";
        }
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = InputKeyboard,
        ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = up ? KeyEventKeyUp : 0, time = 0, dwExtraInfo = IntPtr.Zero }
    };

    private static void Log(string message)
    {
        try
        {
            Core.AppLog.Info("teaching", message);
        }
        catch { }
    }
}
