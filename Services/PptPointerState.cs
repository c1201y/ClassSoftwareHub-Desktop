using System;
using System.Runtime.InteropServices;
using System.Threading;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;
























internal static class PptPointerState
{
    
    private const int PointerNone = 0;

    
    private const int PointerArrow = 1;

    
    private const int PollMs = 250;

    private static volatile bool _running;
    private static volatile bool _known;
    private static volatile int _pointerType = PointerArrow;
    private static Thread? _thread;

    
    
    
    
    internal static bool IsToolMode => _known && _pointerType != PointerArrow && _pointerType != PointerNone;

    
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
        
        object? view = null;
        var lastLogged = -1;

        while (_running)
        {
            try
            {
                if (!PptTouch.IsSlideshowShowing())
                {
                    
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

            
            
            
            
            
            var presentation = GetProperty(app, "ActivePresentation");
            if (presentation is null) return null;

            var show = GetProperty(presentation, "SlideShowWindow");
            if (show is null) return null;

            return GetProperty(show, "View");
        }
        catch (COMException ex)
        {
            
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

    
    private static void Diag(string message)
    {
        var now = Environment.TickCount64;
        if (message == _lastDiag && now - _lastDiagTick < 30_000) return;
        _lastDiag = message;
        _lastDiagTick = now;
        AppLog.Info("ppttouch", "指针状态：" + message);
    }

    
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

    
    
    
    [DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = false, EntryPoint = "CLSIDFromProgID")]
    private static extern void ClsidFromProgId(string progId, out Guid clsid);

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(
        ref Guid clsid,
        IntPtr reserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object? unknown);

    
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
            
        }

        comObject = null;
    }
}
