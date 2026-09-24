using System;
using System.Numerics;
using System.Runtime.InteropServices;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 屏幕边缘侧边栏：贴在屏幕的某一条边上（上下左右都能放）。
/// 全屏放 PPT / 视频时够不到任务栏，从边上点一下就能用工具。
///   · 收起 = 一小截圆角抓手（亚克力底）：点一下展开；**按住可以拖**着挪位置、或拖到别的边
///   · 展开 = 四个工具 + 「收起 / 位置复原 / 隐藏」（展开状态下不能拖，免得跟点按钮打架）
/// 位置（贴哪条边 + 沿边位置）会记在设置里；「位置复原」= 回到右边的居中位置。
/// 置顶、不进任务栏、无标题栏、不能缩放/最大化/最小化。
///
/// 用法：<c>ToolSidebarWindow.ShowSidebar()</c> / <c>HideSidebar()</c> / <c>ApplySetting()</c>。
/// </summary>
public sealed partial class ToolSidebarWindow : Window
{
    private const int CollapsedThicknessDip = 20;   // 收起时那条的厚度（竖条=宽，横条=高）
    private const int CollapsedLengthDip = 110;     // 收起时的长度（竖条=高）
    private const int PanelThicknessDip = 92;       // 展开后的厚度（竖着放时=宽）
    private const int PanelLengthDip = 450;         // 展开后的长度（竖着放时=高）
    private const int PanelThicknessFlatDip = 112;  // 上/下边时：两行（工具一行、按钮一行）的高度
    private const int PanelLengthFlatDip = 360;     // 上/下边时：一排三个按钮（每个 104 dip）要放得下

    private static ToolSidebarWindow? _instance;

    private AppWindow? _appWindow;
    private bool _expanded;
    private bool _shownOnce;
    private bool _visible;

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _idle;

    // 收起状态下的拖拽（屏幕坐标算，别用窗口内坐标，会自己滚起来）
    private bool _pressed;
    private bool _dragging;
    private POINT _dragStart;
    private PointInt32 _dragOrigin;

    private ToolSidebarWindow()
    {
        InitializeComponent();

        // 展开后没人动 → 自己收回去（触屏没地方"点空白处收起"）
        _idle = DispatcherQueue.CreateTimer();
        _idle.Interval = TimeSpan.FromSeconds(10);
        _idle.IsRepeating = false;
        _idle.Tick += (_, _) => { if (_expanded) Collapse(); };

        Configure();
    }

    // ── 对外入口 ─────────────────────────────────────────────

    /// <summary>显示侧边栏（托盘菜单等入口调它；顺手把设置里的开关打开）。</summary>
    public static void ShowSidebar()
    {
        if (!App.Settings.Current.SidebarEnabled)
        {
            App.Settings.Current.SidebarEnabled = true;
            App.Settings.Save();
        }
        (_instance ??= new ToolSidebarWindow()).Present();
    }

    public static void HideSidebar() => _instance?.HideSelf();

    public static bool IsSidebarVisible => _instance?._visible == true;

    /// <summary>设置里的开关/边选项变了：开就显示、关就藏起来；边变了重新贴过去。</summary>
    public static void ApplySetting()
    {
        if (!App.Settings.Current.SidebarEnabled) { HideSidebar(); return; }
        ShowSidebar();
        _instance?.SnapToSetting();
    }

    private void SnapToSetting()
    {
        ApplyEdgeLayout();
        ApplySize();
        MoveToEdge();
    }

    // ── 窗口本身 ─────────────────────────────────────────────

    /// <summary>贴哪条边：left | right | top | bottom（默认 right）。</summary>
    private static string Edge
    {
        get
        {
            var e = App.Settings.Current.SidebarEdge;
            return e is "left" or "top" or "bottom" ? e : "right";
        }
    }

    private static bool IsFlat => Edge is "top" or "bottom";

    private void Configure()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "工具侧边栏";
            _appWindow.IsShownInSwitchers = false;          // 不进任务栏、不进 Alt+Tab

            if (_appWindow.Presenter is OverlappedPresenter p)
            {
                p.SetBorderAndTitleBar(false, false);       // 就一条贴着边的条，不要标题栏/边框
                p.IsResizable = false;
                p.IsMaximizable = false;
                p.IsMinimizable = false;
                p.IsAlwaysOnTop = true;                    // 全屏播放时也要显示在上面
            }

            var ex = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(ex | WsExToolWindow));

            // 亚克力底（模糊背后的画面）；机器不支持的话会自动退回纯色，不会崩
            try { SystemBackdrop = new DesktopAcrylicBackdrop(); }
            catch (Exception ex2) { Log("亚克力不可用: " + ex2.Message); }

            ApplyPanelBrush();
            Root.PointerMoved += (_, _) => Touch();
            Root.PointerPressed += (_, _) => Touch();

            Activated += (_, args) =>
            {
                if (args.WindowActivationState == WindowActivationState.Deactivated && _expanded && !_pressed)
                    Collapse();
            };
            Root.ActualThemeChanged += (_, _) => ApplyPanelBrush();

            ApplyEdgeLayout();
        }
        catch (Exception ex)
        {
            Log("初始化失败: " + ex.Message);
        }
    }

    private void Present()
    {
        try
        {
            ApplyEdgeLayout();
            ApplySize();

            var hwnd = WindowNative.GetWindowHandle(this);

            if (!_shownOnce)
            {
                _shownOnce = true;
                Activate();
            }
            else
            {
                // 之前被「隐藏」或收起过 → 得显式再显示出来，否则窗口一直藏着
                ShowWindow(hwnd, SW_SHOW);
                SetForegroundWindow(hwnd);
            }

            _visible = true;
            Collapse();                                    // 每次出现都从收起状态开始（不挡画面）

            if (_appWindow?.Presenter is OverlappedPresenter p) p.IsAlwaysOnTop = true;

            MoveToEdge();
            // ⚠️ 外观要等窗口真显示出来之后再收尾（构造期改窗口样式会让窗口显示不出来）
            WindowChrome.RemoveBorder(hwnd, rounded: true, dark: Root.ActualTheme == ElementTheme.Dark);
        }
        catch (Exception ex)
        {
            Log("显示失败: " + ex.Message);
        }
    }

    private void HideSelf()
    {
        if (!_visible) return;
        _visible = false;
        try { ShowWindow(WindowNative.GetWindowHandle(this), SW_HIDE); } catch { }
    }

    /// <summary>亚克力上面再压一层很淡的色：深色主题压深、浅色主题压白，保证字看得清。</summary>
    private void ApplyPanelBrush()
    {
        var dark = Root.ActualTheme == ElementTheme.Dark;
        Panel.Background = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(95, 20, 20, 20)
            : Windows.UI.Color.FromArgb(110, 255, 255, 255));
    }

    // ── 展开 / 收起 / 隐藏 ───────────────────────────────────

    private void Expand()
    {
        _expanded = true;
        CollapsedView.Visibility = Visibility.Collapsed;
        ExpandedView.Visibility = Visibility.Visible;
        ApplySize();
        MoveToEdge();
        PlaySlideIn();                                    // 从屏幕边平滑滑进来
        Touch();
    }

    private void Collapse()
    {
        _expanded = false;
        ExpandedView.Visibility = Visibility.Collapsed;
        CollapsedView.Visibility = Visibility.Visible;
        ApplySize();
        MoveToEdge();
        _idle.Stop();
    }

    private void Touch()
    {
        if (!_expanded || _pressed) return;
        _idle.Stop();
        _idle.Start();
    }

    private void Collapse_Click(object sender, RoutedEventArgs e) => Collapse();

    /// <summary>位置复原：回右边、沿边居中。</summary>
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Current.SidebarEdge = "right";
        App.Settings.Current.SidebarPosRatio = -1;
        App.Settings.Save();
        SnapToSetting();
        Touch();
    }

    /// <summary>隐藏：直接关掉（设置里也不显示了），想找回来去「内置工具」页或托盘图标菜单。</summary>
    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Current.SidebarEnabled = false;
        App.Settings.Save();
        HideSelf();
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tool) return;
        Collapse();                                        // 先把边条收起来，别挡着工具窗口
        try { ToolPaletteWindow.ShowTool(tool); } catch (Exception ex) { Log("打开工具失败: " + ex.Message); }
    }

    // ── 收起状态：点一下展开 / 按住拖动 ──────────────────────

    private void Strip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_appWindow is null) return;
        _dragStart = PointerScreenPoint(e);              // ⚠️ 触摸时 GetCursorPos 不跟手，必须用指针事件换算
        _dragOrigin = _appWindow.Position;
        _pressed = true;
        _dragging = false;
        _idle.Stop();
        if (sender is UIElement u) u.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Strip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed || _appWindow is null) return;
        var pt = PointerScreenPoint(e);

        var dx = pt.X - _dragStart.X;
        var dy = pt.Y - _dragStart.Y;
        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) < 8) return;   // 手还没动够，先当点击

        _dragging = true;
        _appWindow.Move(new PointInt32(_dragOrigin.X + dx, _dragOrigin.Y + dy));
        e.Handled = true;
    }

    /// <summary>
    /// 指针位置 → 屏幕像素坐标（鼠标 / 触摸 / 笔通用）。
    /// ⚠️ 不能只用 GetCursorPos：手指在屏幕上拖时鼠标光标并不跟着走（触摸是独立指针），
    /// 那样算出来的位移一直是 0 → 触屏上拖不动窗口。
    /// 用 起始窗口位置 + 起始屏幕点 做绝对位移，避免"窗口跟着自己动"的反馈跑飞。
    /// </summary>
    private POINT PointerScreenPoint(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(null).Position;        // 相对窗口客户区的 DIP
        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = hwnd != IntPtr.Zero ? GetDpiForWindow(hwnd) / 96.0 : 1.0;
        var pt = new POINT((int)Math.Round(p.X * scale), (int)Math.Round(p.Y * scale));
        if (hwnd != IntPtr.Zero) ClientToScreen(hwnd, ref pt);
        return pt;
    }

    private void Strip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        if (sender is UIElement u) u.ReleasePointerCapture(e.Pointer);

        if (_dragging)
        {
            _dragging = false;
            DockToNearestEdge();
        }
        else
        {
            Expand();                                      // 没动 → 当成点了一下
        }

        e.Handled = true;
    }

    /// <summary>松手时：看窗口中心离哪条边最近就贴哪条边，沿边的位置按松手处记下来。</summary>
    private void DockToNearestEdge()
    {
        if (_appWindow is null) return;
        try
        {
            var work = DisplayArea.Primary.WorkArea;
            var size = _appWindow.Size;
            var pos = _appWindow.Position;
            double cx = pos.X + size.Width / 2.0;
            double cy = pos.Y + size.Height / 2.0;

            var dl = Math.Abs(cx - work.X);
            var dr = Math.Abs(work.X + work.Width - cx);
            var dt = Math.Abs(cy - work.Y);
            var db = Math.Abs(work.Y + work.Height - cy);
            var min = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
            var edge = min == dl ? "left" : min == dr ? "right" : min == dt ? "top" : "bottom";

            if (edge != Edge) Log("换边: " + edge);
            App.Settings.Current.SidebarEdge = edge;

            // 沿边位置存成 0~1 的比例，这样收起/展开尺寸不一样时也能对得上
            var flat = edge is "top" or "bottom";
            var alongStart = flat ? work.X : work.Y;
            var alongLen = flat ? work.Width : work.Height;
            var at = flat ? pos.X : pos.Y;
            var myLen = flat ? size.Width : size.Height;
            var free = Math.Max(0, alongLen - myLen);
            App.Settings.Current.SidebarPosRatio =
                free <= 0 ? 0.5 : Math.Clamp((at - alongStart) / (double)free, 0, 1);

            App.Settings.Save();
            SnapToSetting();
        }
        catch (Exception ex)
        {
            Log("换边失败: " + ex.Message);
        }
    }

    // ── 动画 ─────────────────────────────────────────────────

    /// <summary>展开时从贴的那条边平滑滑进来（不做回弹，回弹看着怪）。</summary>
    private void PlaySlideIn()
    {
        try
        {
            var v = ElementCompositionPreview.GetElementVisual(Panel);
            var compositor = v.Compositor;

            var from = IsFlat
                ? new Vector3(0, Edge == "top" ? -56f : 56f, 0)
                : new Vector3(Edge == "left" ? -56f : 56f, 0, 0);

            v.Offset = from;
            v.Opacity = 0.4f;

            var slide = compositor.CreateVector3KeyFrameAnimation();
            slide.InsertKeyFrame(1f, Vector3.Zero,
                compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
            slide.Duration = TimeSpan.FromMilliseconds(190);
            v.StartAnimation("Offset", slide);

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(1f, 1f);
            fade.Duration = TimeSpan.FromMilliseconds(190);
            v.StartAnimation("Opacity", fade);
        }
        catch (Exception ex)
        {
            Log("滑入动画失败: " + ex.Message);
        }
    }

    // ── 排版 / 位置 / 尺寸 ───────────────────────────────────

    /// <summary>按贴的边决定排版：贴左右 = 一列；贴上/下 = 两行（工具一行、按钮一行）。</summary>
    private void ApplyEdgeLayout()
    {
        try
        {
            var edge = Edge;
            var flat = edge is "top" or "bottom";

            // 收起状态的抓手：竖条时竖着、横条时横着
            GripStack.Orientation = flat ? Orientation.Horizontal : Orientation.Vertical;
            Grip.Width = flat ? 48 : 5;
            Grip.Height = flat ? 5 : 48;

            // 展开面板：永远竖着排（贴上下边时就是两行）
            ExpandedStack.Orientation = Orientation.Vertical;
            TitleRow.Visibility = flat ? Visibility.Collapsed : Visibility.Visible;

            ToolStack.Orientation = flat ? Orientation.Horizontal : Orientation.Vertical;
            ToolStack.Spacing = flat ? 8 : 3;
            ToolStack.HorizontalAlignment = flat ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;

            Sep.Margin = flat ? new Thickness(10, 4, 10, 4) : new Thickness(2, 3, 2, 3);

            FooterStack.Orientation = flat ? Orientation.Horizontal : Orientation.Vertical;
            FooterStack.Spacing = flat ? 8 : 2;
            FooterStack.HorizontalAlignment = flat ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;

            // 贴上下边时给按钮留出宽度，别挤成一坨
            foreach (var b in ToolButtons())
            {
                b.MinWidth = flat ? 64 : 0;
                b.MinHeight = flat ? 48 : 52;
                b.Padding = flat ? new Thickness(4, 6, 4, 6) : new Thickness(0, 8, 0, 8);
            }
            foreach (var b in FooterButtons())
            {
                b.MinWidth = flat ? 104 : 0;
                // 横条时矮一点，不然两行加起来超出面板高度，下面一排会被裁掉
                b.MinHeight = flat ? 32 : 44;
                b.Padding = flat ? new Thickness(6, 2, 6, 2) : new Thickness(8, 4, 8, 4);
                b.HorizontalContentAlignment = flat ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            }

            FoldIcon.Glyph = edge switch
            {
                "left" => "\uE76C",      // 往左收
                "top" => "\uE70D",       // 往下收
                "bottom" => "\uE70E",    // 往上收
                _ => "\uE76B",           // 往右收
            };
        }
        catch (Exception ex)
        {
            Log("排版失败: " + ex.Message);
        }
    }

    private Button[] ToolButtons() =>
        new Button[] { SidePick, SideTimer, SideStopwatch, SideClock };

    private Button[] FooterButtons() =>
        new Button[] { FoldButton, ResetButton, HideButton };

    private double Scale()
    {
        try
        {
            var dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch { return 1.0; }
    }

    /// <summary>当前状态下窗口该多大（dip → 物理像素）。</summary>
    private SizeInt32 PlannedSize()
    {
        var scale = Scale();
        int dipW, dipH;
        if (_expanded)
        {
            if (IsFlat)
            {
                // 上/下边：竖着两行 —— 第一行工具、第二行按钮
                dipW = PanelLengthFlatDip;
                dipH = PanelThicknessFlatDip;
            }
            else
            {
                dipW = PanelThicknessDip;
                dipH = PanelLengthDip;
            }
        }
        else
        {
            dipW = IsFlat ? CollapsedLengthDip : CollapsedThicknessDip;
            dipH = IsFlat ? CollapsedThicknessDip : CollapsedLengthDip;
        }

        return new SizeInt32((int)Math.Round(dipW * scale), (int)Math.Round(dipH * scale));
    }

    private void ApplySize()
    {
        if (_appWindow is null) return;
        try
        {
            _appWindow.Resize(PlannedSize());
        }
        catch (Exception ex)
        {
            Log("改尺寸失败: " + ex.Message);
        }
    }

    /// <summary>贴到设置里那条边；沿边的位置按 SidebarPosRatio（&lt;0 = 居中）。</summary>
    private void MoveToEdge()
    {
        if (_appWindow is null) return;
        try
        {
            var work = DisplayArea.Primary.WorkArea;
            // ⚠️ 用自己的目标尺寸算，别读 _appWindow.Size —— Resize 刚调完它还没更新，会按老尺寸贴边
            var size = PlannedSize();
            var edge = Edge;
            var flat = edge is "top" or "bottom";

            var alongLen = flat ? work.Width : work.Height;
            var myLen = flat ? size.Width : size.Height;
            var free = Math.Max(0, alongLen - myLen);

            var ratio = App.Settings.Current.SidebarPosRatio;
            var offset = ratio < 0 ? free / 2.0 : Math.Clamp(ratio * free, 0, free);

            int x, y;
            switch (edge)
            {
                case "left":
                    x = work.X;
                    y = (int)Math.Round(work.Y + offset);
                    break;
                case "top":
                    x = (int)Math.Round(work.X + offset);
                    y = work.Y;
                    break;
                case "bottom":
                    x = (int)Math.Round(work.X + offset);
                    y = work.Y + work.Height - size.Height;
                    break;
                default:
                    x = work.X + work.Width - size.Width;
                    y = (int)Math.Round(work.Y + offset);
                    break;
            }

            // 移动有时不精确，量一下再纠偏几次
            var target = new PointInt32(x, y);
            for (var i = 0; i < 4; i++)
            {
                _appWindow.Move(target);
                var now = _appWindow.Position;
                var dx = x - now.X;
                var dy = y - now.Y;
                if (Math.Abs(dx) <= 2 && Math.Abs(dy) <= 2) break;
                target = new PointInt32(target.X + dx, target.Y + dy);
            }
        }
        catch (Exception ex)
        {
            Log("贴边失败: " + ex.Message);
        }
    }

    // ── P/Invoke ─────────────────────────────────────────────

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y) { X = x; Y = y; }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private static string LogPath => System.IO.Path.Combine(SettingsStore.Dir, "sidebar.log");

    private static void Log(string message)
    {
        try
        {
            System.IO.Directory.CreateDirectory(SettingsStore.Dir);
            System.IO.File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { }
    }
}
