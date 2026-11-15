using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;














public sealed class FlyoutChrome
{
    private readonly Window _window;
    private readonly FrameworkElement _root;
    private readonly Border _panel;

    
    public IntPtr Hwnd { get; }

    
    public AppWindow AppWindow { get; }

    public FlyoutChrome(Window window, FrameworkElement root, Border panel, string title)
    {
        _window = window;
        _root = root;
        _panel = panel;

        Hwnd = WindowNative.GetWindowHandle(window);
        AppWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(Hwnd));

        AppWindow.Title = title;
        AppWindow.IsShownInSwitchers = false;          

        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
        }

        var ex = GetWindowLongPtr(Hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(Hwnd, GwlExStyle, new IntPtr(ex | WsExToolWindow));

        
        try { window.SystemBackdrop = new DesktopAcrylicBackdrop(); } catch { }

        ApplyMaterial();

        root.ActualThemeChanged += (_, _) =>
        {
            ApplyMaterial();
            try { Finish(root.ActualTheme == ElementTheme.Dark); } catch { }
        };
    }

    
    
    
    
    public void ApplyMaterial()
    {
        try
        {
            var dark = _root.ActualTheme == ElementTheme.Dark;
            _panel.Background = new SolidColorBrush(dark
                ? Color.FromArgb(95, 20, 20, 20)
                : Color.FromArgb(110, 255, 255, 255));
        }
        catch { }
    }

    
    public double Scale
    {
        get
        {
            try
            {
                var dpi = GetDpiForWindow(Hwnd);
                return dpi > 0 ? dpi / 96.0 : 1.0;
            }
            catch { return 1.0; }
        }
    }

    
    public RectInt32? CurrentRect
    {
        get
        {
            try
            {
                var p = AppWindow.Position;
                var s = AppWindow.Size;
                return new RectInt32(p.X, p.Y, s.Width, s.Height);
            }
            catch { return null; }
        }
    }

    
    
    
    
    public void Present(PointInt32 start, int width, int height)
    {
        AppWindow.MoveAndResize(new RectInt32(start.X, start.Y, width, height));
        ShowWindow(Hwnd, SW_SHOW);
        SetForegroundWindow(Hwnd);
        _window.Activate();
    }

    
    public void Finish(bool dark) => Core.WindowChrome.RemoveBorder(Hwnd, rounded: true, dark: dark);

    
    public void HideDirect()
    {
        try { ShowWindow(Hwnd, SW_HIDE); } catch { }
    }

    

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int SW_SHOW = 5;
    private const int SW_HIDE = 0;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}







public static class EdgeGeometry
{
    
    public const int GapDip = 8;

    
    public static bool IsFlat(string edge) => edge is "top" or "bottom";

    
    public static int Thickness(string edge, int w, int h) => IsFlat(edge) ? h : w;

    
    public static int Length(string edge, int w, int h) => IsFlat(edge) ? w : h;

    
    
    
    
    
    
    
    
    public static (PointInt32 Start, PointInt32 Final) BesideAnchor(
        string edge, RectInt32 anchor, RectInt32 work, int w, int h, double scale, bool alignStart = false)
    {
        var gap = (int)Math.Round(GapDip * scale);
        var flat = IsFlat(edge);

        
        var along = alignStart
            ? (flat ? anchor.X : anchor.Y)
            : (flat ? anchor.X + anchor.Width / 2 : anchor.Y + anchor.Height / 2) - (flat ? w : h) / 2;

        var final = edge switch
        {
            "left" => new PointInt32(anchor.X + anchor.Width + gap, along),
            "top" => new PointInt32(along, anchor.Y + anchor.Height + gap),
            "bottom" => new PointInt32(along, anchor.Y - h - gap),
            _ => new PointInt32(anchor.X - w - gap, along),
        };

        final = ClampToWork(final, work, w, h);

        
        
        var start = Outward(final, edge, Thickness(edge, w, h) / 2);
        return (start, final);
    }

    
    public static PointInt32 Outward(PointInt32 p, string edge, int distance) => edge switch
    {
        "left" => new PointInt32(p.X - distance, p.Y),
        "top" => new PointInt32(p.X, p.Y - distance),
        "bottom" => new PointInt32(p.X, p.Y + distance),
        _ => new PointInt32(p.X + distance, p.Y),
    };

    
    public static PointInt32 FullyOut(PointInt32 p, string edge, int thickness) => Outward(p, edge, thickness + 2);

    
    public static RectInt32 EdgeBar(string edge, RectInt32 work) => edge switch
    {
        "left" => new RectInt32(work.X, work.Y, 0, work.Height),
        "top" => new RectInt32(work.X, work.Y, work.Width, 0),
        "bottom" => new RectInt32(work.X, work.Y + work.Height, work.Width, 0),
        _ => new RectInt32(work.X + work.Width, work.Y, 0, work.Height),
    };

    
    public static PointInt32 ClampToWork(PointInt32 p, RectInt32 work, int w, int h)
    {
        var x = Math.Clamp(p.X, work.X, Math.Max(work.X, work.X + work.Width - w));
        var y = Math.Clamp(p.Y, work.Y, Math.Max(work.Y, work.Y + work.Height - h));
        return new PointInt32(x, y);
    }
}


public static class FlyoutFade
{
    
    public static void Prepare(UIElement element)
    {
        try { ElementCompositionPreview.GetElementVisual(element).Opacity = 0f; }
        catch { }
    }

    public static void In(UIElement element, double ms)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;

            visual.Opacity = 0f;

            var anim = compositor.CreateScalarKeyFrameAnimation();
            anim.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
            anim.Duration = TimeSpan.FromMilliseconds(ms);

            visual.StartAnimation("Opacity", anim);
        }
        catch
        {
            try { element.Opacity = 1.0; } catch { }
        }
    }

    
    public static void Out(UIElement element, double ms, Action? done = null)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;
            visual.StopAnimation("Opacity");

            var anim = compositor.CreateScalarKeyFrameAnimation();
            anim.InsertKeyFrame(1f, 0f, compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
            anim.Duration = TimeSpan.FromMilliseconds(ms);

            if (done is not null)
            {
                var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                batch.Completed += (_, _) => done();
                visual.StartAnimation("Opacity", anim);
                batch.End();
            }
            else
            {
                visual.StartAnimation("Opacity", anim);
            }
        }
        catch
        {
            try { element.Opacity = 0.0; } catch { }
            done?.Invoke();
        }
    }
}
