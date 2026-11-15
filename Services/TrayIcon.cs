using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ClassSoftwareHub.Desktop.Services;


public sealed class TrayMenuItem
{
    public string Text { get; set; } = "";

    
    public string Command { get; set; } = "";

    
    public bool Separator { get; set; }

    
    public bool IsDefault { get; set; }
}







public sealed class TrayIcon : IDisposable
{
    
    private const uint WM_APP = 0x8000;
    private const uint WM_TRAY = WM_APP + 1;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_NULL = 0x0000;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10;
    private const uint NIIF_INFO = 0x1;
    private const uint NIN_BALLOONUSERCLICK = 0x0405;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010, LR_DEFAULTSIZE = 0x0040;
    private const uint TPM_RETURNCMD = 0x0100, TPM_NONOTIFY = 0x0080;
    private const uint MF_STRING = 0x0000, MF_SEPARATOR = 0x0800, MF_DEFAULT = 0x1000;

    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    
    public event Action? LeftClick;

    
    public event Action<string>? CommandInvoked;

    
    public event Action? BalloonClicked;

    
    
    
    
    public Func<IReadOnlyList<TrayMenuItem>, Action<string>, bool>? MenuRequested;

    
    
    
    
    public List<TrayMenuItem> MenuItems { get; } = new();

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
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
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved,
        IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    
    private readonly string _className = "CshTrayIconWnd_" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _iconPath;
    private readonly WndProcDelegate _proc;          
    private readonly uint _taskbarCreated;
    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _hIcon = IntPtr.Zero;
    private string _tip = "ClassSoftwareHub";
    private bool _added;
    private bool _disposed;

    public bool IsReady => _added;

    public TrayIcon(string iconPath)
    {
        _iconPath = iconPath;
        _proc = WndProc;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    }

    private static void Log(string message)
    {
        try
        {
            Core.AppLog.Info("tray", message);
        }
        catch { }
    }

    
    public bool Setup(string tip)
    {
        _tip = tip;
        try
        {
            var hInstance = GetModuleHandle(null);
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = hInstance,
                lpszClassName = _className,
            };
            if (RegisterClassEx(ref wc) == 0)
                Log($"RegisterClassEx 失败: {Marshal.GetLastWin32Error()}");

            
            _hwnd = CreateWindowEx(0, _className, "ClassSoftwareHub tray", 0,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                Log($"CreateWindowEx 失败: {Marshal.GetLastWin32Error()}");
                return false;
            }

            return AddIcon();
        }
        catch (Exception ex)
        {
            Log("Setup 异常: " + ex);
            return false;
        }
    }

    private bool AddIcon()
    {
        try
        {
            if (_hIcon == IntPtr.Zero && File.Exists(_iconPath))
                _hIcon = LoadImage(IntPtr.Zero, _iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);

            var data = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAY,
                hIcon = _hIcon,
                szTip = _tip,
                szInfo = "",
                szInfoTitle = "",
            };

            _added = Shell_NotifyIcon(NIM_ADD, ref data);
            if (!_added)
                Log($"Shell_NotifyIcon(NIM_ADD) 失败: {Marshal.GetLastWin32Error()} cbSize={data.cbSize} hwnd={_hwnd} icon={_hIcon}");
            else
                Log($"托盘图标已挂上（hwnd={_hwnd}，icon={_hIcon}，cbSize={data.cbSize}）");
            return _added;
        }
        catch (Exception ex)
        {
            Log("AddIcon 异常: " + ex);
            return false;
        }
    }

    
    public void SetTip(string tip)
    {
        _tip = tip;
        if (!_added) return;
        try
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NIF_TIP,
                szTip = tip,
                szInfo = "",
                szInfoTitle = "",
            };
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
        catch { }
    }

    
    
    
    
    public void ShowBalloon(string title, string text)
    {
        if (!_added) return;
        try
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NIF_INFO | NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAY,
                hIcon = _hIcon,
                szTip = _tip,
                szInfo = Clip(text, 255),
                szInfoTitle = Clip(title, 63),
                dwInfoFlags = NIIF_INFO,
                uVersion = 4,
            };
            var ok = Shell_NotifyIcon(NIM_MODIFY, ref data);
            if (!ok) Log($"ShowBalloon 失败: {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            Log("ShowBalloon 异常: " + ex.Message);
        }
    }

    private static string Clip(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_TRAY)
            {
                var evt = (uint)(lParam.ToInt64() & 0xFFFF);
                if (evt == WM_LBUTTONUP)
                {
                    LeftClick?.Invoke();
                    return IntPtr.Zero;
                }
                if (evt == WM_RBUTTONUP)
                {
                    ShowMenu();
                    return IntPtr.Zero;
                }
                if (evt == NIN_BALLOONUSERCLICK)
                {
                    BalloonClicked?.Invoke();
                    return IntPtr.Zero;
                }
            }
            else if (msg == WM_DESTROY)
            {
                _added = false;
            }
            else if (_taskbarCreated != 0 && msg == _taskbarCreated)
            {
                
                _added = false;
                AddIcon();
            }
        }
        catch (Exception ex)
        {
            Log("WndProc 异常: " + ex);
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    
    
    
    private void ShowMenu()
    {
        var items = MenuItems.Count > 0 ? MenuItems : DefaultMenu();

        if (MenuRequested is { } present)
        {
            try
            {
                if (present(items, cmd => CommandInvoked?.Invoke(cmd)))
                    return;
            }
            catch (Exception ex)
            {
                Log("WinUI 菜单失败，退回原生菜单: " + ex);
            }
        }

        ShowNativeMenu(items);
    }

    
    private void ShowNativeMenu(IReadOnlyList<TrayMenuItem> items)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            var live = new List<TrayMenuItem>();
            uint nextId = 1;
            foreach (var item in items)
            {
                if (item.Separator)
                {
                    AppendMenu(menu, MF_SEPARATOR, 0, null);
                    continue;
                }

                var id = nextId++;
                live.Add(item);
                AppendMenu(menu, MF_STRING | (item.IsDefault ? MF_DEFAULT : 0), new IntPtr(id), item.Text);
            }

            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd);   
            var cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_NONOTIFY, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (cmd > 0 && cmd <= live.Count)
                CommandInvoked?.Invoke(live[cmd - 1].Command);
        }
        catch (Exception ex)
        {
            Log("ShowMenu 异常: " + ex);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static List<TrayMenuItem> DefaultMenu() => new()
    {
        new TrayMenuItem { Text = "打开主界面", Command = "show", IsDefault = true },
        new TrayMenuItem { Text = "常用工具", Command = "palette" },
        new TrayMenuItem { Separator = true },
        new TrayMenuItem { Text = "检查更新", Command = "update" },
        new TrayMenuItem { Separator = true },
        new TrayMenuItem { Text = "退出", Command = "exit" },
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_added)
            {
                var data = new NOTIFYICONDATA
                {
                    cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                    hWnd = _hwnd,
                    uID = 1,
                    szTip = "",
                    szInfo = "",
                    szInfoTitle = "",
                };
                Shell_NotifyIcon(NIM_DELETE, ref data);
                _added = false;
            }
            if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
            UnregisterClass(_className, GetModuleHandle(null));
            if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        }
        catch { }
        _hwnd = IntPtr.Zero;
        _hIcon = IntPtr.Zero;
    }
}
