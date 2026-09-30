using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClassSoftwareHub.Desktop.Services.VirtualKeyboard;

/// <summary>落点能不能打字。</summary>
internal enum Typability
{
    /// <summary>不判定（落在我们自己身上 / 查不出来 / 超时）—— 调用方**什么都不要做**。</summary>
    Skip,
    /// <summary>可以打字 → 该弹键盘。</summary>
    Yes,
    /// <summary>不能打字（桌面、按钮、空白…）→ 该收键盘。</summary>
    No,
}

/// <summary>
/// 问一句"屏幕这个点上那个东西能吃键盘输入吗"。
///
/// 走 UI Automation 的 <c>ElementFromPoint</c> —— 这是**唯一不挑输入来源**的判据：
/// 手指、笔、鼠标、别的程序注入的点击，只要落点对，答案都一样。
///
/// ⚠️ 手写 COM interop 的理由：项目是 WinUI 3 **unpackaged 自包含**发布，引
///    <c>System.Windows.Automation</c>（属 WPF）会平白多几十 MB。
///
/// ⛔⛔ **vtable 顺序错一格就会跳到别的方法上，表现为随机崩溃。改这个文件只许在末尾追加，
///    不许插队**（见 TOPICS §M.9）。
///
/// ⚠️ UIA 是跨进程 COM，对象认 apartment：**创建与使用必须在同一条线程**。所以这里自带一条
///    MTA 后台线程，所有查询都排队到它上面跑；调用方（也必须是后台线程）同步等结果。
/// </summary>
internal static class FocusProbe
{
    // UIA 属性 id
    private const int PropProcessId = 30002;
    private const int PropControlType = 30003;
    private const int PropHasKeyboardFocus = 30008;
    private const int PropIsKeyboardFocusable = 30009;
    private const int PropIsEnabled = 30010;
    private const int PropIsPassword = 30017;
    private const int PropIsOffscreen = 30020;

    // 控件类型
    private const int CtrlComboBox = 50003;
    private const int CtrlEdit = 50004;
    private const int CtrlText = 50020;
    private const int CtrlDocument = 50030;

    private static readonly Guid ClsidAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");

    private static readonly object Gate = new();
    private static readonly AutoResetEvent Work = new(false);
    private static readonly object QueueGate = new();
    private static readonly System.Collections.Generic.Queue<Request> Pending = new();

    private static Thread? _thread;
    private static IUIAutomation? _automation;
    private static bool _stopping;

    private static int _ownPid;

    public static bool IsRunning
    {
        get { lock (Gate) return _automation is not null; }
    }

    private sealed class Request
    {
        public bool PointMode;
        public int X;
        public int Y;
        public Typability Result = Typability.Skip;
        public readonly ManualResetEventSlim Done = new(false);
    }

    // ── 生命周期 ────────────────────────────────────────────

    /// <summary>起 UIA 线程。</summary>
    public static bool Start()
    {
        lock (Gate)
        {
            if (_automation is not null) return true;

            _ownPid = Environment.ProcessId;
            _stopping = false;
            _thread = new Thread(ThreadProc)
            {
                IsBackground = true,
                Name = "CSH-FocusProbe",
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();

            for (var i = 0; i < 100 && _automation is null && _thread.IsAlive; i++)
                Thread.Sleep(10);

            var ok = _automation is not null;
            VkbdLog.Write(ok ? "落点判定：已启动" : "⚠️ 落点判定：没起来（UIA 可能不可用）");
            return ok;
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            if (_thread is null) return;
            _stopping = true;
        }

        Work.Set();
        _thread?.Join(800);

        lock (Gate)
        {
            _thread = null;
            _automation = null;
        }
    }

    // ── 查询（⛔ 只在后台线程上调）──────────────────────────

    /// <summary>落点 <paramref name="x"/>,<paramref name="y"/>（**物理像素**）上那个东西能不能打字。</summary>
    public static Typability ProbeAtPoint(int x, int y)
    {
        var req = new Request { PointMode = true, X = x, Y = y };
        return Run(req, 400);
    }

    /// <summary>当前有键盘焦点的那个东西能不能打字。</summary>
    public static Typability ProbeFocused()
    {
        var req = new Request { PointMode = false };
        return Run(req, 400);
    }

    private static Typability Run(Request req, int timeoutMs)
    {
        if (!IsRunning) return Typability.Skip;

        lock (QueueGate) Pending.Enqueue(req);
        Work.Set();

        return req.Done.Wait(timeoutMs) ? req.Result : Typability.Skip;
    }

    // ── UIA 线程 ────────────────────────────────────────────

    private static void ThreadProc()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidAutomation);
            if (type is null) return;

            // ⚠️ 用 Activator 而不是 [ComImport] coclass 的 new —— .NET 5+ 上后者不可靠。
            _automation = (IUIAutomation?)Activator.CreateInstance(type);
            if (_automation is null) return;
        }
        catch (Exception ex)
        {
            VkbdLog.Write("⚠️ 落点判定：创建 UIA 失败 —— " + ex.Message);
            return;
        }

        while (true)
        {
            _ = Work.WaitOne(500);

            while (true)
            {
                Request? req;
                lock (QueueGate)
                {
                    req = Pending.Count > 0 ? Pending.Dequeue() : null;
                }
                if (req is null) break;

                try
                {
                    req.Result = req.PointMode ? JudgePoint(req.X, req.Y) : JudgeFocused();
                }
                catch (Exception ex)
                {
                    VkbdLog.Detail("落点判定出错：" + ex.Message);
                    req.Result = Typability.Skip;
                }
                finally
                {
                    req.Done.Set();
                }
            }

            lock (Gate)
            {
                if (_stopping) return;
            }
        }
    }

    private static Typability JudgePoint(int x, int y)
    {
        if (_automation is null) return Typability.Skip;

        var pt = new Point32 { X = x, Y = y };
        var hr = _automation.ElementFromPoint(pt, out var element);
        if (hr != 0 || element is null) return Typability.Skip;

        var verdict = Judge(element);

        // 点到输入框**里面的文字**时（WinUI / WPF / Chromium 都会这样），落点上给的是 Text 元素，
        // 判不出来 —— 退回看键盘焦点，别一口咬定"不能打字"。
        return verdict == Typability.Skip ? JudgeFocused() : verdict;
    }

    private static Typability JudgeFocused()
    {
        if (_automation is null) return Typability.Skip;

        var hr = _automation.GetFocusedElement(out var element);
        if (hr != 0 || element is null) return Typability.Skip;

        return Judge(element);
    }

    private static Typability Judge(IUIAutomationElement element)
    {
        // 落在我们自己身上：不判定。用户可能正在本应用的输入框里打字（正当场景），
        // 也可能正在设置页调外观 —— 两种情况都不该让键盘自作主张。
        var pid = GetInt(element, PropProcessId);
        if (pid is not null && pid.Value == _ownPid) return Typability.Skip;

        var ctrl = GetInt(element, PropControlType);
        if (ctrl is null) return Typability.Skip;

        // Text 元素本身不是输入框，交给调用方退回焦点判定。
        if (ctrl.Value == CtrlText) return Typability.Skip;

        var enabled = GetBool(element, PropIsEnabled);
        if (enabled == false) return Typability.No;

        var offscreen = GetBool(element, PropIsOffscreen);
        if (offscreen == true) return Typability.No;

        // 密码框特判放行：用户就是要输密码，而且它常被同时标成 readonly。
        if (GetBool(element, PropIsPassword) == true) return Typability.Yes;

        switch (ctrl.Value)
        {
            case CtrlEdit:
            case CtrlDocument:
                return Typability.Yes;

            case CtrlComboBox:
                return GetBool(element, PropIsKeyboardFocusable) == false
                    ? Typability.No
                    : Typability.Yes;
        }

        // 其它控件：只要它自己能接键盘焦点，也算能吃输入（比如某些自绘编辑区）。
        if (GetBool(element, PropHasKeyboardFocus) == true &&
            GetBool(element, PropIsKeyboardFocusable) == true)
        {
            return Typability.Yes;
        }

        return Typability.No;
    }

    private static int? GetInt(IUIAutomationElement element, int propertyId)
    {
        try
        {
            if (element.GetCurrentPropertyValue(propertyId, out var value) != 0) return null;
            return value is int i ? i : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool? GetBool(IUIAutomationElement element, int propertyId)
    {
        try
        {
            if (element.GetCurrentPropertyValue(propertyId, out var value) != 0) return null;
            return value is bool b ? b : null;
        }
        catch
        {
            return null;
        }
    }

    // ── COM 声明 ⛔ 方法顺序 = vtable 顺序，只许在末尾追加 ──

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [ComImport]
    [Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        [PreserveSig] int CompareElements(IntPtr first, IntPtr second, out int areSame);
        [PreserveSig] int CompareRuntimeIds(IntPtr first, IntPtr second, out int areSame);
        [PreserveSig] int GetRootElement(out IUIAutomationElement root);
        [PreserveSig] int ElementFromHandle(IntPtr hwnd, out IUIAutomationElement element);

        // 第 5 个 —— 就靠它。
        [PreserveSig] int ElementFromPoint(Point32 pt, out IUIAutomationElement element);

        // 第 6 个。
        [PreserveSig] int GetFocusedElement(out IUIAutomationElement element);
    }

    [ComImport]
    [Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        // ⛔ 这个占位只是为了把 vtable 位置对齐 —— **永远不要调用它**（会真的抢键盘焦点）。
        [PreserveSig] int SetFocus();
        [PreserveSig] int GetRuntimeId(out IntPtr runtimeId);
        [PreserveSig] int FindFirst(int scope, IntPtr condition, out IUIAutomationElement? found);
        [PreserveSig] int FindAll(int scope, IntPtr condition, out IntPtr found);
        [PreserveSig] int FindFirstBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IUIAutomationElement? found);
        [PreserveSig] int FindAllBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IntPtr found);
        [PreserveSig] int BuildUpdatedCache(IntPtr cacheRequest, out IUIAutomationElement? updated);

        // 第 8 个 —— 靠它读属性。
        [PreserveSig] int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
    }
}
