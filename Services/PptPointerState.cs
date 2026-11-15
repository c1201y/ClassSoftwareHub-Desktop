using System;
using System.Runtime.InteropServices;
using System.Threading;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 放映中 PowerPoint 的指针类型（箭头 / 笔 / 橡皮 / 激光笔 …），用 Office 自己的 COM 对象读。
///
/// <para>
/// 为什么要它（2026-11-07 Nick 反馈）：用批注写字时，上下滑也被当成翻页。
/// 墨迹模式下手指是在画线，位移轻松越过滑动阈值，<see cref="PptTouch"/> 根本无从分辨。
/// 三条常规路子都试过、都不行：
/// 放映窗口对 UIA 完全不暴露子树（元素数 0）；放映光标是 6×6 全透明隐藏光标，
/// 箭头和笔拿到的是同一个句柄；也没有独立的工具栏窗口可查类名。
/// 只剩问 Office 自己这一条。
/// </para>
/// <para>
/// 判据是 <c>SlideShowView.PointerType</c>：无 0、箭头 1、笔 2、橡皮 5
/// （2026-11-07 在 PowerPoint 365 上实地读出，笔模式下值为 2）。
/// ⛔ 判「非箭头即工具模式」，别逐个枚举：Office 各版本还多出激光笔等取值，
/// 白名单（0 / 1）永远不会漏。WPS 演示没有这个 COM 接口，取不到时一律当箭头处理，不误伤原有手势。
/// </para>
/// <para>
/// ⚠️ COM 只能在 STA 线程上访问，因此本类的所有 COM 调用都固定跑在自己的后台线程里；
/// 钩子回调只读 <see cref="IsToolMode"/> 这个 volatile 布尔值，绝不跨线程碰 COM 对象
/// （低级鼠标钩子回调卡住会连累整个系统的输入）。
/// </para>
/// </summary>
internal static class PptPointerState
{
    /// <summary>ppSlideShowPointerNone</summary>
    private const int PointerNone = 0;

    /// <summary>ppSlideShowPointerArrow</summary>
    private const int PointerArrow = 1;

    /// <summary>轮询间隔。从切到笔到真正落笔有几百毫秒，250ms 足够跟上，又不至于反复跨进程调用。</summary>
    private const int PollMs = 250;

    private static volatile bool _running;
    private static volatile bool _known;
    private static volatile int _pointerType = PointerArrow;
    private static Thread? _thread;

    /// <summary>
    /// 当前是否处于「笔 / 橡皮 / 激光笔」这类工具模式。
    /// 读不到状态（没装 PowerPoint、没放映、WPS 演示）时恒为 false —— 保持原有手势行为。
    /// </summary>
    internal static bool IsToolMode => _known && _pointerType != PointerArrow && _pointerType != PointerNone;

    /// <summary>给诊断用：最近一次读到的原始值。</summary>
    internal static int Current => _pointerType;

    internal static void Start()
    {
        if (_running) return;
        if (_thread is { IsAlive: true }) return;

        _running = true;
        var thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "ppttouch-pointer",
        };
        thread.SetApartmentState(ApartmentState.STA);
        _thread = thread;
        thread.Start();
    }

    internal static void Stop()
    {
        _running = false;
        _known = false;
        _pointerType = PointerArrow;
        _thread = null;
    }

    private static void Loop()
    {
        // COM 对象只在本线程创建、本线程使用；失效了就整个丢弃重取（放映结束、PowerPoint 退出、换文档）
        object? view = null;
        var lastLogged = -1;

        while (_running)
        {
            try
            {
                if (!PptTouch.IsSlideshowShowing())
                {
                    // 不在放映：释放掉对象，避免 PowerPoint 退出后还攥着一份 RCW
                    ReleaseCom(ref view);
                    _known = false;
                    lastLogged = -1;
                }
                else
                {
                    view ??= ResolveView();
                    if (view is null)
                    {
                        _known = false;
                    }
                    else
                    {
                        var type = ReadPointerType(view);
                        if (type < 0)
                        {
                            // 对象已失效（放映退出 / 文档切换），下一轮重新解析
                            ReleaseCom(ref view);
                            _known = false;
                        }
                        else
                        {
                            _pointerType = type;
                            _known = true;
                            if (type != lastLogged)
                            {
                                AppLog.Info("ppttouch", $"放映指针类型 = {type}（{Describe(type)}）");
                                lastLogged = type;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 限流：状态类噪声只记一次，别把日志刷满
                if (lastLogged != -2)
                {
                    AppLog.Warn("ppttouch", "读取放映指针类型失败：" + ex.Message);
                    lastLogged = -2;
                }

                _known = false;
                ReleaseCom(ref view);
            }

            for (var i = 0; i < PollMs / 25 && _running; i++) Thread.Sleep(25);
        }

        ReleaseCom(ref view);
    }

    private static string Describe(int type) => type switch
    {
        PointerNone => "隐藏指针",
        PointerArrow => "箭头",
        2 => "笔",
        3 => "指针常隐",
        4 => "橡皮（旧版编号）",
        5 => "橡皮",
        _ => "其他工具",
    };

    /// <summary>拿到当前放映窗口的 <c>View</c> 对象。没有放映窗口返回 null。</summary>
    private static object? ResolveView()
    {
        object? app = null;
        try
        {
            ClsidFromProgId("PowerPoint.Application", out var clsid);
            GetActiveObject(ref clsid, IntPtr.Zero, out app);
            if (app is null)
            {
                Diag("GetActiveObject 返回空");
                return null;
            }

            // 链路：Application → ActivePresentation → SlideShowWindow → View，
            // 全是**无参属性**，所以 Invoke 的 DISPPARAMS 可以一直是空的。
            // ⚠️ 单数 SlideShowWindow 挂在 Presentation 上，不挂在 Application 上
            //    （Application 只有集合 SlideShowWindows，取 Item 就得构造 VARIANT 参数）。
            //    2026-11-07 实测：直接问 Application 要 SlideShowWindow 会得到 DISP_E_UNKNOWNNAME。
            var presentation = GetProperty(app, "ActivePresentation");
            if (presentation is null) return null;

            var show = GetProperty(presentation, "SlideShowWindow");
            if (show is null) return null;

            return GetProperty(show, "View");
        }
        catch (COMException ex)
        {
            // PowerPoint 没在跑 / 没放映：正常情况，不当错误
            Diag($"GetActiveObject 失败 0x{ex.HResult:X8}：{ex.Message}");
            return null;
        }
        finally
        {
            ReleaseCom(ref app);
        }
    }

    private static long _lastDiagTick;
    private static string _lastDiag = "";

    /// <summary>同一条诊断 30 秒内只记一次，避免 250ms 一轮把日志刷满。</summary>
    private static void Diag(string message)
    {
        var now = Environment.TickCount64;
        if (message == _lastDiag && now - _lastDiagTick < 30_000) return;
        _lastDiag = message;
        _lastDiagTick = now;
        AppLog.Info("ppttouch", "指针状态：" + message);
    }

    /// <summary>读 <c>PointerType</c>；读不到返回 -1（对象可能已经失效）。</summary>
    private static int ReadPointerType(object view)
    {
        var value = GetProperty(view, "PointerType");
        return value switch
        {
            int i => i,
            short s => s,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            _ => -1,
        };
    }

    // ── IDispatch 晚绑定 ────────────────────────────────────────────────
    //
    // ⛔ 别换成「引用 Microsoft.Office.Interop.PowerPoint」：那要求目标机器装了
    //    PIAs，而且 .NET Core 早就砍掉 COM binder 的编译期绑定，到了运行时照样要走 IDispatch。

    private const ushort DispatchPropertyGet = 0x2;
    private static readonly Guid IidNull = Guid.Empty;

    [StructLayout(LayoutKind.Sequential)]
    private struct DispParams
    {
        public IntPtr Args;
        public IntPtr NamedArgs;
        public uint ArgCount;
        public uint NamedArgCount;
    }

    [ComImport]
    [Guid("00020400-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDispatch
    {
        [PreserveSig]
        int GetTypeInfoCount(out uint count);

        [PreserveSig]
        int GetTypeInfo(uint index, uint lcid, out IntPtr typeInfo);

        [PreserveSig]
        int GetIDsOfNames(
            ref Guid riid,
            [MarshalAs(UnmanagedType.LPArray)] IntPtr[] names,
            uint nameCount,
            uint lcid,
            [MarshalAs(UnmanagedType.LPArray)] int[] dispIds);

        [PreserveSig]
        int Invoke(
            int member,
            ref Guid riid,
            uint lcid,
            ushort flags,
            ref DispParams parameters,
            [MarshalAs(UnmanagedType.Struct)] out object result,
            IntPtr exceptionInfo,
            out uint argumentError);
    }

    // ⚠️ 这个函数在 **ole32.dll**，不在 oleaut32.dll —— Win11 上 oleaut32 的那几个导出
    //    已经被 API Set 转到 combase，从 oleaut32 里取会报 "Unable to find an entry point"
    //    （2026-11-07 实测：oleaut32 只剩 GetActiveObject，CLSIDFromProgID 在 ole32 / combase）
    [DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = false, EntryPoint = "CLSIDFromProgID")]
    private static extern void ClsidFromProgId(string progId, out Guid clsid);

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(
        ref Guid clsid,
        IntPtr reserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object? unknown);

    /// <summary>取一个无参属性的值。取不到返回 null。</summary>
    private static object? GetProperty(object? target, string name)
    {
        if (target is not IDispatch dispatch) return null;

        var namePtr = Marshal.StringToCoTaskMemUni(name);
        int member;
        try
        {
            var names = new[] { namePtr };
            var ids = new int[1];
            var riid = IidNull;
            var hrIds = dispatch.GetIDsOfNames(ref riid, names, 1, 0, ids);
            if (hrIds != 0)
            {
                Diag($"GetIDsOfNames(\"{name}\") 失败 0x{hrIds:X8}");
                return null;
            }

            member = ids[0];
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePtr);
        }

        var parameters = new DispParams();
        var invokeId = IidNull;
        var hr = dispatch.Invoke(member, ref invokeId, 0, DispatchPropertyGet, ref parameters,
            out var result, IntPtr.Zero, out _);
        if (hr != 0)
        {
            Diag($"Invoke(\"{name}\") 失败 0x{hr:X8}");
            return null;
        }

        return result;
    }

    private static void ReleaseCom(ref object? comObject)
    {
        if (comObject is null) return;
        try
        {
            if (Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
        }
        catch
        {
            // 释放失败无所谓，进程还在跑，GC 会收尾
        }

        comObject = null;
    }
}
