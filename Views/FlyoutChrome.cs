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

/// <summary>
/// 浮窗的「窗口外壳」——音量两个浮窗（主音量 / 合成器）共用，**完全照 <see cref="ToolSidebarWindow"/> 那一套**：
///
///   · 材质：<see cref="DesktopAcrylicBackdrop"/> 打底 + <see cref="ApplyMaterial"/> 压一层纯色薄纱
///     （深色压深、浅色压白）。跟边条**一模一样**，两个东西摆在一起才像一个整体。
///   · 无边框 + <c>WS_EX_TOOLWINDOW</c> + 圆角 8 + 去白边，走 <see cref="Core.WindowChrome"/>。
///   · <see cref="Core.ThemeHost"/> 管深浅色跟着设置走。
///
/// ⚠️ 规矩（都是踩出来的，别改）：
///   1. <see cref="Finish"/> 必须在窗口**显示之后**再调 —— 显示前改窗口样式会让窗口显示不出来；
///   2. 材质随主题变：<c>ActualThemeChanged</c> 里重压一次薄纱 + 重画一次窗口边框；
///   3. 别在这儿自己写无边框那套，去用 WindowChrome。
/// </summary>
public sealed class FlyoutChrome
{
    private readonly Window _window;
    private readonly FrameworkElement _root;
    private readonly Border _panel;

    /// <summary>窗口句柄（注册给 <see cref="VolumeFlyoutGroup"/> 判焦点用）。</summary>
    public IntPtr Hwnd { get; }

    /// <summary>这个窗口的 AppWindow（移动、尺寸都走它）。</summary>
    public AppWindow AppWindow { get; }

    public FlyoutChrome(Window window, FrameworkElement root, Border panel, string title)
    {
        _window = window;
        _root = root;
        _panel = panel;

        Hwnd = WindowNative.GetWindowHandle(window);
        AppWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(Hwnd));

        AppWindow.Title = title;
        AppWindow.IsShownInSwitchers = false;          // 别进 Alt+Tab、别占任务栏

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

        // 跟侧边栏同一个底：亚克力
        try { window.SystemBackdrop = new DesktopAcrylicBackdrop(); } catch { }

        ApplyMaterial();

        root.ActualThemeChanged += (_, _) =>
        {
            ApplyMaterial();
            try { Finish(root.ActualTheme == ElementTheme.Dark); } catch { }
        };
    }

    /// <summary>
    /// 侧边栏同款薄纱：亚克力上面再压一层很淡的纯色 —— 深色压深、浅色压白，保证字看得清。
    /// 色值跟 <see cref="ToolSidebarWindow"/> 里的 ApplyPanelBrush **逐字一致**，改就一起改。
    /// </summary>
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

    /// <summary>窗口所在显示器的缩放比（96dpi = 1.0）。</summary>
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

    /// <summary>当前在屏幕上的矩形。</summary>
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

    /// <summary>
    /// 一次把「起点位置 + 最终尺寸」设下去，再显形抢前台。
    /// ⚠️ 位置和尺寸必须**一次** MoveAndResize：分开写的话中间那一帧会被系统画出来，就是"闪一下再滑"的来源。
    /// </summary>
    public void Present(PointInt32 start, int width, int height)
    {
        AppWindow.MoveAndResize(new RectInt32(start.X, start.Y, width, height));
        ShowWindow(Hwnd, SW_SHOW);
        SetForegroundWindow(Hwnd);
        _window.Activate();
    }

    /// <summary>外观收尾（去白边 + 圆角 + 深浅色）。**必须在窗口显示之后调**。</summary>
    public void Finish(bool dark) => Core.WindowChrome.RemoveBorder(Hwnd, rounded: true, dark: dark);

    /// <summary>立刻藏起来（滑出动画播完后由动画回调调它）。</summary>
    public void HideDirect()
    {
        try { ShowWindow(Hwnd, SW_HIDE); } catch { }
    }

    // ── Win32 ────────────────────────────────────────────────────────────

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

/// <summary>
/// 贴边浮窗的位置计算。两个概念要分清：
///   · **沿边方向**（left/right 时是 Y、top/bottom 时是 X）—— 浮窗在这条线上跟锚点对齐；
///   · **进退方向**（垂直那条边的方向）—— 浮窗从边上滑进来多少。
/// 坐标一律物理像素：dip × <see cref="FlyoutChrome.Scale"/>。
/// </summary>
public static class EdgeGeometry
{
    /// <summary>挨着锚点时留的缝（dip）。</summary>
    public const int GapDip = 8;

    /// <summary>贴上/下（横条）时，浮窗也走横版；贴左/右走竖版。</summary>
    public static bool IsFlat(string edge) => edge is "top" or "bottom";

    /// <summary>浮窗「厚度」：竖版是宽、横版是高。</summary>
    public static int Thickness(string edge, int w, int h) => IsFlat(edge) ? h : w;

    /// <summary>沿边方向上的长度：竖版是高、横版是宽。</summary>
    public static int Length(string edge, int w, int h) => IsFlat(edge) ? w : h;

    /// <summary>
    /// 挨着锚点（边条 / 主音量浮窗）往屏幕**里侧**排：
    /// 进退方向贴住锚点的内侧，沿边方向跟锚点**对齐居中**，最后夹进工作区别跑出屏幕。
    /// 返回（滑入起点 = 往边外退半块，最终位置）。
    /// </summary>
    public static (PointInt32 Start, PointInt32 Final) BesideAnchor(
        string edge, RectInt32 anchor, RectInt32 work, int w, int h, double scale)
    {
        var gap = (int)Math.Round(GapDip * scale);
        var flat = IsFlat(edge);

        // 沿边方向：以锚点这条边的中点为基准，浮窗自己居中
        var anchorCenter = flat ? anchor.X + anchor.Width / 2 : anchor.Y + anchor.Height / 2;
        var along = anchorCenter - (flat ? w : h) / 2;

        var final = edge switch
        {
            "left" => new PointInt32(anchor.X + anchor.Width + gap, along),
            "top" => new PointInt32(along, anchor.Y + anchor.Height + gap),
            "bottom" => new PointInt32(along, anchor.Y - h - gap),
            _ => new PointInt32(anchor.X - w - gap, along),
        };

        final = ClampToWork(final, work, w, h);

        // ⚠️ 起点只往屏幕外退**半块**：整块挪到屏幕外的窗口 DWM 常常不给它刷帧，
        //    滑进来的第一帧会发虚/卡顿（侧边栏当初就是踩了这个才改的）。
        var start = Outward(final, edge, Thickness(edge, w, h) / 2);
        return (start, final);
    }

    /// <summary>从某个位置往"屏幕外"退 <paramref name="distance"/> 像素（滑入起点 / 滑出终点）。</summary>
    public static PointInt32 Outward(PointInt32 p, string edge, int distance) => edge switch
    {
        "left" => new PointInt32(p.X - distance, p.Y),
        "top" => new PointInt32(p.X, p.Y - distance),
        "bottom" => new PointInt32(p.X, p.Y + distance),
        _ => new PointInt32(p.X + distance, p.Y),
    };

    /// <summary>整个窗口都在屏幕外（滑出要滑到底，屏幕边上不留残片）。</summary>
    public static PointInt32 FullyOut(PointInt32 p, string edge, int thickness) => Outward(p, edge, thickness + 2);

    /// <summary>把这条屏幕边当成一条零厚度的「锚点」（边条拿不到时的退路）。</summary>
    public static RectInt32 EdgeBar(string edge, RectInt32 work) => edge switch
    {
        "left" => new RectInt32(work.X, work.Y, 0, work.Height),
        "top" => new RectInt32(work.X, work.Y, work.Width, 0),
        "bottom" => new RectInt32(work.X, work.Y + work.Height, work.Width, 0),
        _ => new RectInt32(work.X + work.Width, work.Y, 0, work.Height),
    };

    /// <summary>把矩形夹进工作区（贴屏幕下边时会长出屏幕，这里兜住）。</summary>
    public static PointInt32 ClampToWork(PointInt32 p, RectInt32 work, int w, int h)
    {
        var x = Math.Clamp(p.X, work.X, Math.Max(work.X, work.X + work.Width - w));
        var y = Math.Clamp(p.Y, work.Y, Math.Max(work.Y, work.Y + work.Height - h));
        return new PointInt32(x, y);
    }
}

/// <summary>浮窗内容淡入（走合成器，跟窗口滑动同时进行，别让字"啪"一下出现）。</summary>
public static class FlyoutFade
{
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
}
