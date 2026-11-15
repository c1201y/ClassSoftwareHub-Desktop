using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;












public sealed partial class StickerWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int SwShowNoActivate = 4;
    private const double MinSide = 48;
    private const double MaxSide = 8000;
    private const double EdgeDip = 10;                 
    private const double TouchEdgeDip = 30;            

    private static readonly List<StickerWindow> Keep = new();

    private readonly IntPtr _hwnd;
    private readonly IntPtr _oldExStyle;
    private int _pxW;
    private int _pxH;
    private double _scale = 1.0;

    private enum Mode
    {
        None,
        Move,
        Resize,
    }

    
    private Mode _mode = Mode.None;
    private int _zone;
    private POINT _startCursor;                        
    private RectInt32 _startRect;

    
    private bool _manActive;
    private bool _manPinch;
    private int _manZone;
    private RectInt32 _manStartRect;
    private double _manDx;                             
    private double _manDy;
    private double _manK = 1.0;                        
    private double _manPx;                             
    private double _manPy;

    private StickerWindow(int pxW, int pxH, int screenX, int screenY)
    {
        InitializeComponent();

        _pxW = Math.Max(1, pxW);
        _pxH = Math.Max(1, pxH);

        _hwnd = WindowNative.GetWindowHandle(this);
        var app = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        app.Title = "贴图";
        app.IsShownInSwitchers = false;

        if (app.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
        }

        _oldExStyle = GetWindowLongPtr(_hwnd, GwlExStyle);
        SetWindowLongPtr(_hwnd, GwlExStyle, new IntPtr(_oldExStyle.ToInt64() | WsExNoActivate | WsExToolWindow));
        KillSystemBorder(_hwnd);                       

        Root.PointerPressed += Root_PointerPressed;
        Root.PointerMoved += Root_PointerMoved;
        Root.PointerReleased += Root_PointerReleased;
        Root.PointerCaptureLost += (_, _) => { _mode = Mode.None; };
        Root.PointerWheelChanged += Root_WheelChanged;
        Root.DoubleTapped += (_, _) => CloseSticker();
        Root.RightTapped += (_, _) => CloseSticker();

        
        
        Root.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY | ManipulationModes.Scale;
        Root.ManipulationStarted += Root_ManipulationStarted;
        Root.ManipulationDelta += Root_ManipulationDelta;
        Root.ManipulationCompleted += Root_ManipulationCompleted;

        _scale = DpiScale();
        ApplyRect(screenX, screenY, _pxW, _pxH);

        Closed += (_, _) => Keep.Remove(this);
    }

    
    public static void Pin(byte[] bmp, int screenX, int screenY, int pxW, int pxH)
    {
        try
        {
            var w = new StickerWindow(pxW, pxH, screenX, screenY);
            Keep.Add(w);
            _ = w.LoadAsync(bmp);

            w.Activate();                              
            ShowWindow(w._hwnd, SwShowNoActivate);     
            UpdateWindow(w._hwnd);

            ScreenCapture.Log($"贴图已出: {pxW}x{pxH} @{screenX},{screenY}（当前共 {Keep.Count} 张）");
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("贴图失败: " + ex);
        }
    }

    private async Task LoadAsync(byte[] bmp)
    {
        try
        {
            var img = await ScreenCapture.ToImageAsync(bmp);
            if (img is not null) Shot.Source = img;
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("贴图载图失败: " + ex);
        }
    }

    private double DpiScale()
    {
        try
        {
            var dpi = GetDpiForWindow(_hwnd);
            if (dpi > 0) return dpi / 96.0;
        }
        catch { }
        return 1.0;
    }

    
    private void ApplyRect(int x, int y, int w, int h)
    {
        try
        {
            _pxW = Math.Clamp(w, (int)MinSide, (int)MaxSide);
            _pxH = Math.Clamp(h, (int)MinSide, (int)MaxSide);
            AppWindow.MoveAndResize(new RectInt32(x, y, _pxW, _pxH));
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("贴图摆位失败: " + ex.Message);
        }
    }

    

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType.ToString() != "Mouse") return;   
        var dip = e.GetCurrentPoint(Root).Position;
        _zone = ZoneOf(dip);
        _mode = _zone == 0 ? Mode.Move : Mode.Resize;
        _startCursor = CursorNow();
        _startRect = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, _pxW, _pxH);
        try { Root.CapturePointer(e.Pointer); } catch { }
        e.Handled = true;
    }

    private int ZoneOf(Point dip, double edge = EdgeDip)
    {
        var w = _pxW / _scale;
        var h = _pxH / _scale;
        var nearL = dip.X <= edge;
        var nearR = dip.X >= w - edge;
        var nearT = dip.Y <= edge;
        var nearB = dip.Y >= h - edge;

        if (nearL && nearT) return 5;
        if (nearR && nearT) return 6;
        if (nearL && nearB) return 7;
        if (nearR && nearB) return 8;
        if (nearL) return 1;
        if (nearR) return 2;
        if (nearT) return 3;
        if (nearB) return 4;
        return 0;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType.ToString() != "Mouse") return;   
        if (_mode == Mode.None)
        {
            ShowHover(ZoneOf(e.GetCurrentPoint(Root).Position));
            return;
        }

        var cur = CursorNow();
        var dx = cur.X - _startCursor.X;
        var dy = cur.Y - _startCursor.Y;

        if (_mode == Mode.Move)
        {
            ApplyRect(_startRect.X + dx, _startRect.Y + dy, _startRect.Width, _startRect.Height);
            e.Handled = true;
            return;
        }

        ResizeBy(_zone, dx, dy);
        e.Handled = true;
    }

    
    private void ResizeBy(int zone, int dx, int dy)
    {
        double w0 = _startRect.Width;
        double h0 = _startRect.Height;
        var aspect = w0 / h0;

        double w;
        double h;
        if (zone is 1 or 2)
        {
            w = w0 + (zone == 2 ? dx : -dx);
            h = w / aspect;
        }
        else if (zone is 3 or 4)
        {
            h = h0 + (zone == 4 ? dy : -dy);
            w = h * aspect;
        }
        else
        {
            
            var dw = zone is 6 or 8 ? dx : -dx;
            var dh = zone is 7 or 8 ? dy : -dy;
            if (Math.Abs(dw) >= Math.Abs(dh)) { w = w0 + dw; h = w / aspect; }
            else { h = h0 + dh; w = h * aspect; }
        }

        w = Math.Clamp(w, MinSide, MaxSide);
        h = Math.Clamp(h, MinSide, MaxSide);

        
        double x = _startRect.X;
        double y = _startRect.Y;
        var right = _startRect.X + w0;
        var bottom = _startRect.Y + h0;

        if (zone is 1 or 5 or 7) x = right - w;                       
        else if (zone is 2 or 6 or 8) x = _startRect.X;               
        else x = _startRect.X + (w0 - w) / 2;                        

        if (zone is 3 or 5 or 6) y = bottom - h;                     
        else if (zone is 4 or 7 or 8) y = _startRect.Y;              
        else y = _startRect.Y + (h0 - h) / 2;                        

        ApplyRect((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(w), (int)Math.Round(h));
    }

    
    
    
    

    private void Root_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
    {
        if (e.PointerDeviceType.ToString() == "Mouse") return;   
        ResetManipulation(e.Position);
        if (_manZone != 0) ShowHover(_manZone);                       
        e.Handled = true;
    }

    private void Root_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (!_manActive || e.PointerDeviceType.ToString() == "Mouse") return;

        var pinch = Math.Abs(e.Delta.Scale - 1.0f) > 0.001f;
        if (pinch && !_manPinch)
        {
            
            _manPinch = true;
            _manZone = 0;
            _manDx = _manDy = 0;
            _manK = 1.0;
            _manStartRect = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, _pxW, _pxH);
            _manPx = e.Position.X;
            _manPy = e.Position.Y;
        }

        _manDx += e.Delta.Translation.X;
        _manDy += e.Delta.Translation.Y;

        if (_manZone != 0 && !_manPinch)
        {
            
            ResizeBy(_manZone, (int)Math.Round(_manDx * _scale), (int)Math.Round(_manDy * _scale));
            e.Handled = true;
            return;
        }

        if (_manPinch) _manK = Math.Clamp(_manK * e.Delta.Scale, 0.15, 8.0);

        var w = Math.Clamp(_manStartRect.Width * _manK, MinSide, MaxSide);
        var h = Math.Clamp(_manStartRect.Height * _manK, MinSide, MaxSide);
        var k = w / _manStartRect.Width;                              

        
        var x = _manStartRect.X + _manDx * _scale + _manPx * _scale * (1 - k);
        var y = _manStartRect.Y + _manDy * _scale + _manPy * _scale * (1 - k);

        ApplyRect((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(w), (int)Math.Round(h));
        e.Handled = true;
    }

    private void Root_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        _manActive = false;
        _manPinch = false;
        _manZone = 0;
        HoverEdge.Visibility = Visibility.Collapsed;
        ScreenCapture.Log($"贴图：手指操作结束 → {_pxW}x{_pxH} @{AppWindow.Position.X},{AppWindow.Position.Y}");
    }

    private void ResetManipulation(Point at)
    {
        _manActive = true;
        _manPinch = false;
        _manZone = ZoneOf(at, TouchEdgeDip);
        _manStartRect = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, _pxW, _pxH);
        _manDx = _manDy = 0;
        _manK = 1.0;
        _manPx = at.X;
        _manPy = at.Y;
    }

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_mode == Mode.None) return;
        _mode = Mode.None;
        try { Root.ReleasePointerCapture(e.Pointer); } catch { }
        e.Handled = true;
    }

    private void Root_WheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(Root).Properties.MouseWheelDelta;
        if (delta == 0) return;

        var factor = delta > 0 ? 1.1 : 1.0 / 1.1;
        var w = _pxW * factor;
        var h = _pxH * factor;
        if (w < MinSide || h < MinSide || w > MaxSide || h > MaxSide) return;

        ApplyRect(AppWindow.Position.X, AppWindow.Position.Y, (int)Math.Round(w), (int)Math.Round(h));
        e.Handled = true;
    }

    
    private void ShowHover(int zone)
    {
        if (zone == 0 || _mode != Mode.None)
        {
            HoverEdge.Visibility = Visibility.Collapsed;
            return;
        }

        const double t = 6;
        HoverEdge.Width = double.NaN;
        HoverEdge.Height = double.NaN;
        HoverEdge.HorizontalAlignment = HorizontalAlignment.Stretch;
        HoverEdge.VerticalAlignment = VerticalAlignment.Stretch;
        HoverEdge.Margin = new Thickness(0);

        switch (zone)
        {
            case 1: HoverEdge.Width = t; HoverEdge.HorizontalAlignment = HorizontalAlignment.Left; break;
            case 2: HoverEdge.Width = t; HoverEdge.HorizontalAlignment = HorizontalAlignment.Right; break;
            case 3: HoverEdge.Height = t; HoverEdge.VerticalAlignment = VerticalAlignment.Top; break;
            case 4: HoverEdge.Height = t; HoverEdge.VerticalAlignment = VerticalAlignment.Bottom; break;
            case 5: HoverEdge.Width = t; HoverEdge.Height = t; HoverEdge.HorizontalAlignment = HorizontalAlignment.Left; HoverEdge.VerticalAlignment = VerticalAlignment.Top; break;
            case 6: HoverEdge.Width = t; HoverEdge.Height = t; HoverEdge.HorizontalAlignment = HorizontalAlignment.Right; HoverEdge.VerticalAlignment = VerticalAlignment.Top; break;
            case 7: HoverEdge.Width = t; HoverEdge.Height = t; HoverEdge.HorizontalAlignment = HorizontalAlignment.Left; HoverEdge.VerticalAlignment = VerticalAlignment.Bottom; break;
            case 8: HoverEdge.Width = t; HoverEdge.Height = t; HoverEdge.HorizontalAlignment = HorizontalAlignment.Right; HoverEdge.VerticalAlignment = VerticalAlignment.Bottom; break;
        }

        HoverEdge.Visibility = Visibility.Visible;
    }

    private void CloseSticker()
    {
        ScreenCapture.Log("贴图关闭");
        try { Close(); } catch { }
    }

    private static POINT CursorNow() => GetCursorPos(out var p) ? p : default;

    
    
    internal static void KillSystemBorder(IntPtr hwnd)
    {
        try
        {
            var none = unchecked((IntPtr)(long)0xFFFFFFFE);
            DwmSetWindowAttribute(hwnd, 34, ref none, IntPtr.Size);
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref IntPtr value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
