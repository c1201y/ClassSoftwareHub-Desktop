using System;
using System.Runtime.InteropServices;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 讲台动作：侧边栏里那几个"按一下就干活"的模块都调这里。
///
/// 原则（老师上课的命脉）：
///   · **能调系统 API 就调 API，模拟按键只当兜底**（模拟按键拿不到结果、可能被安全策略拦、会抢焦点）
///   · **关窗口一律优雅关闭**（等于点 ×），绝不硬杀 —— 老师没保存的 PPT 不能没
///   · 动作做完**把焦点还回去**，别把全屏 PPT 的焦点抢走
/// </summary>
public static class TeachingActions
{
    /// <summary>按住型动作（现在只有放大镜）：按下开始、松手结束。</summary>
    /// <summary>有没有"按住型"动作（按下开始、松手结束）。当前没有 —— 放大镜已改成点一下开、再点一下关。</summary>
    public static bool IsHold(string id) => false;

    /// <summary>记着"上一次不在我们自己家里"的前台窗口（见 StartFocusWatcher）。</summary>
    private static IntPtr _lastForeign = IntPtr.Zero;

    /// <summary>「关全部」的待确认清单（第一下攒着，第二下才真关）。</summary>
    private static readonly List<IntPtr> _pendingCloseAll = new();
    private static DateTime _pendingCloseAllAt = DateTime.MinValue;

    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _focusWatcher;

    /// <summary>按下/单击：开始干活。返回"要不要顺便给界面上留一句话"（比如两下确认的提示）。</summary>
    public static string? Begin(string id)
    {
        try
        {
            switch (id)
            {
                case "mag": ToggleSystemMagnifier(); break;
                case "taskview": TaskView(); break;
                case "showdesktop": ShowDesktop(); break;
                case "minall": MinimizeAll(); break;
                case "closefg": CloseForegroundApp(); break;
                case "closeall": return PrepareCloseAll();
                case "screenshot": return StartScreenshot();
                default: Log($"不认识的动作: {id}"); break;
            }
        }
        catch (Exception ex)
        {
            Log($"{id} 失败: " + ex.Message);
            return "出错了（看日志）";
        }

        return null;
    }

    /// <summary>松手：结束按住型动作。</summary>
    public static void End(string id)
    {
        if (!IsHold(id)) return;
        // 现阶段没有"按住型"动作（放大镜改成调系统那个了），留着这个入口给以后的动作。
    }

    /// <summary>一次性动作：先把"用户正在用的那个窗口"记下来，做完再把焦点还回去。</summary>
    public static string? Run(string id)
    {
        var previous = TargetForeground();
        var hint = Begin(id);
        ReturnFocus(previous);
        return hint;
    }

    /// <summary>
    /// 焦点观察器（保留）：只在"确实是个能用的应用窗口"时才记一下。
    /// ⚠️ 现在**不再用它来决定"关前台关谁"**（它会把顺手抢焦点的悬浮助手也算进来，2026-09-25 踩过），
    /// 只作为 ReturnFocus 的兜底。
    /// </summary>
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

    /// <summary>该把焦点还给谁：前台要是干净的应用窗口就用它，否则按 z 序找。</summary>
    private static IntPtr TargetForeground()
    {
        var fg = GetForegroundWindow();
        if (EligibleAppWindow(fg)) return fg;
        if (_lastForeign != IntPtr.Zero && EligibleAppWindow(_lastForeign)) return _lastForeign;
        return IntPtr.Zero;
    }

    // ── 具体动作 ─────────────────────────────────────────────

    /// <summary>回桌面：调系统的"显示桌面"（切换式，再按一次还原）。调不到就退回模拟 Win+D。</summary>
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

    /// <summary>任务视图：等于 Win+Tab。</summary>
    private static void TaskView()
    {
        var sent = SendWinChord(VkTab);
        Log($"任务视图：SendInput 插入 {sent} 个事件" + (sent == 0 ? $"（被拦了？err={Marshal.GetLastWin32Error()}）" : ""));
    }

    /// <summary>
    /// 截屏贴图：先把边条收起来（别照进图里），稍等一下弹出框选层。
    /// 真正干活在 `Views.SnipOverlayWindow`：框完一块 → 进剪贴板（课件 Ctrl+V 直接贴）+ 屏幕上留一张贴图。
    /// </summary>
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
            return "截屏启动失败，看日志";
        }

        return null;
    }

    /// <summary>把焦点还回"用户刚才在用的那个窗口"（面板之类的显示后调用，别打断讲课）。</summary>
    public static void RefocusPrevious()
    {
        var back = TargetForeground();
        if (back != IntPtr.Zero) ReturnFocus(back);
    }

    /// <summary>
    /// 系统放大镜（Windows 自带那个 `Magnify.exe`）：
    /// 没开就调起来（起来后自动切到"镜头"模式 = 跟着鼠标跑的那块），开着就**优雅关掉**（发关闭消息，不硬杀）。
    /// ⚠️ 自研那版（自抓屏 + 自绘控制条）已封存到 `_csh_scratch\magnifier-v3-archive\`，想捡回来再改。
    /// </summary>
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

            // 它起来默认可能是"全屏"模式，压屏幕就太野了 → 等它出来按 Ctrl+Alt+L 切"镜头"（跟着鼠标）
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

    /// <summary>
    /// 「关全部」第一步：**只点数**，把清单攒起来，返回要问用户的那句话（两行：标题 + 说明）。
    /// 真正的关闭在 `ConfirmPending("closeall")` 里做（= 用户在浮出面板上按了红按钮）。
    /// ⚠️ 和「关前台」的区别：这里**连最小化的窗口也算**（最小化 = 还在任务栏里开着）。
    /// </summary>
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
            return "没有可关的窗口";
        }

        _pendingCloseAll.AddRange(found);
        _pendingCloseAllAt = DateTime.Now;

        var titles = found.Select(TitleOf).Where(t => t.Length > 0).ToList();
        var sample = string.Join("、", titles.Take(3));
        if (titles.Count > 3) sample += " 等";

        Log($"关闭全部：待确认 {found.Count} 个 → " + string.Join(" / ", found.Take(12).Select(TitleOf)));
        return $"确定关闭 {found.Count} 个窗口吗？\n含最小化的窗口：{sample}";
    }

    /// <summary>执行关闭（面板上按了红按钮之后）。</summary>
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

    /// <summary>用户在确认面板上按了「确定」——执行这个动作攒着的那件事。</summary>
    public static string? ConfirmPending(string id)
    {
        try
        {
            return id switch
            {
                "closeall" => ExecuteCloseAll(),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            Log($"{id} 确认执行失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>批量关窗口时的"合格"判定：比「关前台」宽松（允许最小化），但排除系统壳、工具窗、我们自家那些。</summary>
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
        // 输入法/系统小窗：有标题、不一定带 TOOLWINDOW，光靠"标题非空"挡不住
        "IME", "MSCTFIME UI", "Default IME", "CicMarshalWnd",
        "EdgeUiInputTopWndClass", "Shell_InputSwitchTopLevelWindow", "ApplicationFrameInputSinkWindow",
    };

    /// <summary>「关前台应用」：正常关（发 WM_CLOSE，等于点 ×），绝不硬杀。</summary>
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

    /// <summary>
    /// 找"用户正在看的那个应用窗口"。
    /// ⚠️ 2026-09-25 教训：原来用"上一次不在我们家的前台窗口"这个记法，**会记错人**
    /// （别的程序（如悬浮助手）顺手抢一下焦点就被记下来了），结果按下去关的是别的应用，
    /// 用户看到的就是"关前台关不掉前台，只关掉一部分应用"。
    /// 现在改成：① 当前前台要是"干净的应用窗口"就用它；② 否则按 **z 序**从上往下找第一个合格的
    /// （我们自己的窗口是置顶的，所以"我们下面那个"正是用户刚才在用的窗口）。
    /// </summary>
    private static IntPtr ResolveTargetWindow()
    {
        var fg = GetForegroundWindow();
        if (EligibleAppWindow(fg)) return RootWindowOf(fg);

        var found = IntPtr.Zero;
        _ = EnumWindows((hwnd, _) =>
        {
            if (!EligibleAppWindow(hwnd)) return true;
            found = hwnd;
            return false;                       // EnumWindows 是按 z 序来的，第一个就是最上面的
        }, IntPtr.Zero);

        return found == IntPtr.Zero ? IntPtr.Zero : RootWindowOf(found);
    }

    /// <summary>是不是"一个可以让人关掉的应用窗口"。</summary>
    private static bool EligibleAppWindow(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return false;
            if (!IsWindowVisible(hwnd)) return false;
            if (IsIconic(hwnd)) return false;                        // 最小化的不算"正在看"
            if (IsOurApp(hwnd)) return false;                        // 别关自己（含我们的其它实例）
            if (GetWindow(hwnd, GwOwner) != IntPtr.Zero) return false;   // 有主的（弹窗之类）跟着主人走
            if ((GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExToolWindow) != 0) return false;

            var cls = ClassNameOf(hwnd);
            if (ShellClassNames.Contains(cls)) return false;

            return TitleOf(hwnd).Length > 0;                          // 没标题的十有八九是杂窗
        }
        catch
        {
            return false;
        }
    }

    /// <summary>往上找到整窗（比如点在某控件/弹窗上时，关掉的是整个应用窗口）。</summary>
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

    /// <summary>最小化全部：所有"普通顶层窗口"收进任务栏（跳过工具窗、有主的窗口、系统窗和我们自己）。</summary>
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
                if (GetWindow(hwnd, GwOwner) != IntPtr.Zero) return true;       // 有主的窗口跟着主人一起收
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

    /// <summary>点完把焦点还给原来那个窗口（否则老师全屏的 PPT 会"失焦"）。</summary>
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
                    if (!IsOwnWindow(now)) return;              // 焦点已经不在我们身上了，别乱抢
                    SetForegroundWindow(previous);
                }
                catch { }
            });
        }
        catch { }
    }

    // ── Win32 ────────────────────────────────────────────────

    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const uint WM_CLOSE = 0x0010;
    private const int SW_MINIMIZE = 6;
    private const uint GwOwner = 4;

    private const ushort VkLWin = 0x5B;
    private const ushort VkTab = 0x09;
    private const ushort VkD = 0x44;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;      // Alt
    private const ushort VkL = 0x4C;
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

        // 联合体起点在偏移 8（type 4 字节 + 4 字节对齐）；KEYBDINPUT 占 24 字节 → 到 32
        public KEYBDINPUT ki;

        // ⚠️ 关键：x64 上 Win32 的 INPUT 是 **40** 字节（联合体那一块按最长的 MOUSEINPUT 算 32 字节）。
        // 少了这 8 字节，SendInput 会整批拒收：返回 0 且 err=87（参数错误）——2026-09-25 实测踩过。
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

    /// <summary>
    /// 是不是"我们家的"窗口 —— **用进程名判断，包含我们的其它实例**。
    /// ⚠️ 2026-09-25：只比自己的 pid 不够（另一个实例的窗口会被当成"别人"，被"关前台"关掉）。
    /// </summary>
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

    /// <summary>模拟 Win+某键（Win 键必须按下再松开成对，不然会"卡住"）。返回实际插入的事件数（0 = 被系统拦了）。</summary>
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

    /// <summary>模拟组合键（如 Ctrl+Alt+L）：按下按给的顺序，松开按倒序。</summary>
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
            var path = System.IO.Path.Combine(SettingsStore.Dir, "teaching.log");
            System.IO.Directory.CreateDirectory(SettingsStore.Dir);
            System.IO.File.AppendAllText(path, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { }
    }
}
