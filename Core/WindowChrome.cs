using System;
using System.Runtime.InteropServices;

namespace ClassSoftwareHub.Desktop.Core;











public static class WindowChrome
{
    private const int DwmwaUseImmersiveDarkMode = 20;      
    private const int DwmwaNcRenderingPolicy = 2;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;

    private const int NcRenderingDisabled = 2;             

    private const int CornerDefault = 0;
    private const int CornerDoNotRound = 1;
    private const int CornerRound = 2;
    private const int CornerRoundSmall = 3;

    private const uint ColorDefault = 0xFFFFFFFF;
    private const uint ColorNone = 0xFFFFFFFE;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    
    
    
    

    private const int GwlStyle = -16;
    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private const int WsSysMenu = 0x00080000;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsMaximizeBox = 0x00010000;
    private const int WsBorder = 0x00800000;
    private const int WsDlgFrame = 0x00400000;
    private const int WsPopup = unchecked((int)0x80000000);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
    
    
    
    

    [DllImport("user32.dll")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    
    
    
    
    
    
    

    private const uint WmNcCalcSize = 0x0083;

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
        uint uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, uint uIdSubclass, IntPtr dwRefData);

    private const uint NcCalcSizeSubclassId = 0x43534842;   

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    
    
    private static void InstallNcCalcSizeHook(IntPtr hwnd)
    {
        var ok = SetWindowSubclass(hwnd, NcCalcSizeProc, NcCalcSizeSubclassId, IntPtr.Zero);
        ChromeLog($"subclass hwnd={hwnd} ok={ok}");
    }

    private static bool _ncProcLogged;

    private static IntPtr NcCalcSizeProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
        uint uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WmNcCalcSize && wParam != IntPtr.Zero)
        {
            if (!_ncProcLogged)
            {
                _ncProcLogged = true;
                ChromeLog($"NCCALCSIZE fired hwnd={hWnd} → 返回0（客户区=整个窗口）");
            }
            return IntPtr.Zero;                            
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private static long ReadStyle(IntPtr hwnd) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GwlStyle).ToInt64() : GetWindowLong32(hwnd, GwlStyle);

    
    private static void ChromeLog(string message)
    {
        try
        {
            AppLog.Info("chrome", message);
        }
        catch { }
    }

    
    
    
    
    
    public static bool MakeBorderless(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            var remove = WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox
                         | WsBorder | WsDlgFrame;

            if (IntPtr.Size == 8)
            {
                var style = GetWindowLongPtr64(hwnd, GwlStyle).ToInt64();
                style = (style & ~remove) | unchecked((uint)WsPopup);
                _ = SetWindowLongPtr64(hwnd, GwlStyle, new IntPtr(style));
            }
            else
            {
                var style = GetWindowLong32(hwnd, GwlStyle);
                style = (style & ~remove) | WsPopup;
                _ = SetWindowLong32(hwnd, GwlStyle, style);
            }

            _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
            return true;
        }
        catch
        {
            
            return false;
        }
    }

    
    
    
    
    
    
    
    
    
    
    
    public static bool HideMinimizeMaximizeButtons(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            var remove = WsMinimizeBox | WsMaximizeBox;

            if (IntPtr.Size == 8)
            {
                var style = GetWindowLongPtr64(hwnd, GwlStyle).ToInt64();
                _ = SetWindowLongPtr64(hwnd, GwlStyle, new IntPtr(style & ~(long)remove));
            }
            else
            {
                var style = GetWindowLong32(hwnd, GwlStyle);
                _ = SetWindowLong32(hwnd, GwlStyle, style & ~remove);
            }

            
            _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
            return true;
        }
        catch
        {
            return false;
        }
    }

    
    
    
    
    
    public static void RemoveBorder(IntPtr hwnd, bool rounded = false, bool dark = false)
    {
        if (hwnd == IntPtr.Zero) return;

        ChromeLog($"RemoveBorder begin hwnd={hwnd}");
        try
        {
            var styleBefore = ReadStyle(hwnd);
            var borderless = MakeBorderless(hwnd);   
            ChromeLog($"  MakeBorderless={borderless} styleAfter=0x{ReadStyle(hwnd):X}");

            
            
            try { InstallNcCalcSizeHook(hwnd); }
            catch (Exception ex) { ChromeLog("  subclass FAILED: " + ex.GetType().Name + " " + ex.Message); }

            SetDarkMode(hwnd, dark);
            SetBackgroundFallback(hwnd, dark);

            if (borderless)
            {
                
                
                
                
                
                var none = unchecked((int)ColorNone);
                _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref none, sizeof(int));
            }
            else
            {
                
                
                var disabled = NcRenderingDisabled;
                if (DwmSetWindowAttribute(hwnd, DwmwaNcRenderingPolicy, ref disabled, sizeof(int)) != 0)
                {
                    var none2 = unchecked((int)ColorNone);
                    _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref none2, sizeof(int));
                }
            }

            var corner = rounded ? CornerRound : CornerDoNotRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));

            ChromeLog($"  done style=0x{ReadStyle(hwnd):X} corner={(rounded ? "round" : "square")}");
        }
        catch (Exception ex)
        {
            ChromeLog("EXCEPTION: " + ex.GetType().Name + " " + ex.Message);
        }
    }

    
    
    
    
    
    
    
    
    
    
    
    
    public static void SetRounded(IntPtr hwnd, bool rounded)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var corner = rounded ? CornerRound : CornerDoNotRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        }
        catch { }
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool redraw);

    
    
    
    
    
    
    public static void SetRoundRegion(IntPtr hwnd, int widthPx, int heightPx, int radiusPx)
    {
        if (hwnd == IntPtr.Zero || widthPx <= 0 || heightPx <= 0) return;
        try
        {
            var r = Math.Clamp(radiusPx, 0, Math.Min(widthPx, heightPx) / 2);
            if (r <= 0) return;

            
            var rgn = CreateRoundRectRgn(0, 0, widthPx + 1, heightPx + 1, r, r);
            if (rgn == IntPtr.Zero) { ChromeLog("CreateRoundRectRgn 失败"); return; }

            
            
            if (SetWindowRgn(hwnd, rgn, true) == 0) ChromeLog("SetWindowRgn 失败");
        }
        catch (Exception ex)
        {
            ChromeLog("圆角区域失败: " + ex.Message);
        }
    }

    
    
    
    
    
    
    public static void ClearRoundRegion(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try { SetWindowRgn(hwnd, IntPtr.Zero, true); }
        catch (Exception ex) { ChromeLog("清圆角区域失败: " + ex.Message); }
    }

    
    public static void SetDarkMode(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var value = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            {
                
                _ = DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));
            }
        }
        catch { }
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private const int GclpHbrBackground = -10;

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW", SetLastError = true)]
    private static extern IntPtr SetClassLongPtrW(IntPtr hwnd, int index, IntPtr value);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    private static IntPtr _bgBrushDark = IntPtr.Zero;
    private static IntPtr _bgBrushLight = IntPtr.Zero;

    
    
    
    
    public static void SetBackgroundFallback(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var brush = dark ? _bgBrushDark : _bgBrushLight;
            if (brush == IntPtr.Zero)
            {
                
                brush = CreateSolidBrush(dark ? 0x00202020u : 0x00F3F3F3u);
                if (brush == IntPtr.Zero) return;
                if (dark) _bgBrushDark = brush; else _bgBrushLight = brush;
            }

            var old = SetClassLongPtrW(hwnd, GclpHbrBackground, brush);
            if (old != brush) ChromeLog($"背景刷 → {(dark ? "深色 #202020" : "浅色 #F3F3F3")}（old={old}）");
        }
        catch (Exception ex)
        {
            ChromeLog("SetBackgroundFallback 失败: " + ex.Message);
        }
    }

    
    public static void RestoreBorder(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var corner = CornerDefault;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
            var color = unchecked((int)ColorDefault);
            _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref color, sizeof(int));
        }
        catch { }
    }

    
    public static void SetRoundSmall(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var corner = CornerRoundSmall;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        }
        catch { }
    }
}
