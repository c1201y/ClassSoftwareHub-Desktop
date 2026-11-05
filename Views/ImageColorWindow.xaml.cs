using System;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Pages.Tools;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 图片取色浮窗（2026-11-04）。
///
/// <para>
/// 侧边栏「取色」那颗按钮开的就是这扇窗。以前它是 <c>Kind = Page</c>，点一下会把整个主界面
/// 拉出来再跳到「内置工具 → 图片取色」；讲课时从课件里取个配色，为这件事调出整个应用太重。
/// 现在跟哈希校验、查阅单词一致：单开一扇小窗，不碰主界面。
/// </para>
///
/// <para>
/// ⚠️ 内容不是重画的一份界面，就是 <see cref="ImageColorToolPage"/> 本体（<c>embedded: true</c>）。
///    两个入口共用同一份取色算法与版式，只在紧凑参数上有差别 —— 见那个构造函数的注释。
/// </para>
///
/// <para>
/// ⚠️ 关掉 = **真销毁**（跟哈希校验窗一致）：这扇窗没有"留着复用"的价值，
///    真关最省事，也不会在退出链路上留一扇没关的窗把进程吊住。
/// </para>
/// </summary>
public sealed partial class ImageColorWindow : Window
{
    private const int WidthDip = 520;

    /// <summary>
    /// 没选图片时的高度：32 标题栏 + 10 上边距 + 130 拖放区 + 16 下边距 ≈ 188，
    /// 再留一点余量。原来定 260，实测底下空出一大截（2026-11-04 截图确认）。
    /// </summary>
    private const int EmptyHeightDip = 215;

    /// <summary>出配色之后的高度：拖放区 + 种子色 + 7 档色块（4 列 2 行）+ HSL + 操作行。</summary>
    private const int PaletteHeightDip = 660;

    private static ImageColorWindow? _instance;

    private AppWindow? _appWindow;
    private ImageColorToolPage? _page;

    private ImageColorWindow()
    {
        InitializeComponent();
        Configure();
    }

    // ── 对外 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 摊开这扇窗；已经开着就调到前面（不重建、不丢已经取好的配色）。
    /// <para>
    /// ⚠️ 方法名 <c>Show</c> 是约定：<c>ToolSidebarWindow</c> 打开「专用窗口」类模块时
    ///    按名字反射调用它（见 <c>Data/SidebarModule.WindowType</c>），改名会静默失效。
    /// </para>
    /// </summary>
    public static void Show()
    {
        try
        {
            _instance ??= new ImageColorWindow();
            _instance.BringUp();
        }
        catch (Exception ex)
        {
            AppLog.Exception("image-color", ex, "打开图片取色窗口");
        }
    }

    /// <summary>退出应用时真正销毁（由 MainWindow 的退出链路调用；没开过就什么都不做）。</summary>
    public static void CloseForExit()
    {
        var w = _instance;
        _instance = null;
        try { w?.Close(); } catch { }
    }

    // ── 窗口配置 ─────────────────────────────────────────────────────────

    private void Configure()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "图片取色";

            if (_appWindow.Presenter is OverlappedPresenter p)
            {
                p.IsResizable = true;
                p.IsMaximizable = false;
                p.IsMinimizable = false;
            }

            // 标题栏只留「关闭」：小浮窗最小化没有意义、最大化已被 presenter 关掉
            WindowChrome.HideMinimizeMaximizeButtons(hwnd);

            ModernWindow.ApplyTitleBar(this, TitleBarHost, Root);

            // 跟应用主题（⛔ 不传 external：分体只作用于侧边栏与常用工具浮窗）
            BackdropHost.Apply(this, Root, App.Settings.Current.Backdrop);
            ThemeHost.Apply(Root);

            // 内容：整页本体，切紧凑版式，并把本窗句柄交过去（选图对话框要用）
            _page = new ImageColorToolPage(embedded: true)
            {
                HostWindowHandle = hwnd,
                ContentStateChanged = OnContentStateChanged,
            };
            ContentHost.Content = _page;

            ApplyHeight(EmptyHeightDip, center: true);

            Closed += (_, _) =>
            {
                _instance = null;
                _page = null;
            };
        }
        catch (Exception ex)
        {
            AppLog.Exception("image-color", ex, "图片取色窗口初始化");
        }
    }

    /// <summary>页面告诉我们"有图了 / 又空了"，窗口跟着长高或收短。</summary>
    private void OnContentStateChanged(bool hasPalette)
    {
        // 没取到图（比如格式不支持）时也会走 false，这时窗口停在空态高度，用户能看到报错
        ApplyHeight(hasPalette ? PaletteHeightDip : EmptyHeightDip, center: false);
    }

    /// <summary>
    /// 设置窗口高度（宽度固定）。
    /// <para>
    /// <paramref name="center"/> = true 时居中摆放（首次出现用）；
    /// false = 保持当前位置，只在底边出屏时往上顶 —— 用户可能已经把窗拖到顺手的位置，
    /// 一出图就跳回屏幕中间会让人烦。
    /// </para>
    /// </summary>
    private void ApplyHeight(int heightDip, bool center)
    {
        if (_appWindow is null) return;
        try
        {
            var scale = Scale();
            var work = DisplayArea.Primary.WorkArea;

            var maxW = work.Width / scale - 40;
            var maxH = work.Height / scale - 60;

            var w = Math.Clamp(WidthDip, 420, Math.Max(420, maxW));
            // 下限 200：空态高度 215 要能真的用上 —— 原来卡 260 会把它抬回去（2026-11-04 实测）
            var h = Math.Clamp(heightDip, 200, Math.Max(200, maxH));

            _appWindow.ResizeClient(new SizeInt32(
                (int)Math.Round(w * scale), (int)Math.Round(h * scale)));

            var size = _appWindow.Size;

            if (center)
            {
                _appWindow.Move(new PointInt32(
                    work.X + Math.Max(0, (work.Width - size.Width) / 2),
                    work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
                return;
            }

            var pos = _appWindow.Position;
            var bottom = pos.Y + size.Height;
            var limit = work.Y + work.Height - 8;
            var top = bottom > limit ? Math.Max(work.Y + 8, limit - size.Height) : pos.Y;

            if (top != pos.Y) _appWindow.Move(new PointInt32(pos.X, top));

            AppLog.Step("image-color", $"窗口高度按内容调整：{h:0} dip");
        }
        catch (Exception ex)
        {
            AppLog.Warn("image-color", "图片取色窗尺寸设置失败：" + ex.Message);
        }
    }

    private void BringUp()
    {
        try
        {
            Activate();
            var hwnd = WindowNative.GetWindowHandle(this);
            ShowWindow(hwnd, SW_SHOW);
            SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            AppLog.Exception("image-color", ex, "激活图片取色窗口");
        }
    }

    // ── Win32 ────────────────────────────────────────────────────────────

    private double Scale()
    {
        try
        {
            var dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch { return 1.0; }
    }

    private const int SW_SHOW = 5;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}
