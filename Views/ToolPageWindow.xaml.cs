using System;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 通用「工具页」窗口：把主界面里的某一页单独装进一扇窗（2026-10-30）。
///
/// <para>
/// 目前用于侧边栏的「编码 / 哈希转换」—— 原来它走 <c>Kind = Page</c>，点一下会把主界面
/// 拉出来再翻页；为了转个 Base64 或核一个哈希值，这个代价太大。
/// </para>
///
/// <para>
/// ⚠️ 只承载**不依赖 ShellPage / Frame 外部状态**的页面（编码页符合：它自己在 OnNavigatedTo
///    里读参数，不读 App.MainWindow）。要装别的页面前先核一遍这一点，
///    否则会出现"窗口里能显示、但按钮跳转落空"的半残状态。
/// </para>
///
/// <para>
/// ⚠️ 关掉 = 真销毁（与学科工具窗一致）：这类窗口没有"留着复用"的价值，
///    真关最省事，也不会在退出链路上留一个没关的窗口把进程吊住。
/// </para>
/// </summary>
public sealed partial class ToolPageWindow : Window
{
    /// <summary>窗口默认大小（dip）。编码页是三栏 998 宽的版式，给窄了会把它压变形。</summary>
    private const int DefaultWidthDip = 1040;
    private const int DefaultHeightDip = 760;

    private static ToolPageWindow? _instance;

    private AppWindow? _appWindow;

    /// <summary>当前装着哪一页。同一个页面再次点侧边栏时只把它调到前面，不重建（否则已算好的哈希值会丢）。</summary>
    private Type? _currentPage;

    private ToolPageWindow()
    {
        InitializeComponent();
        Configure();
    }

    /// <summary>
    /// 打开（或切换）这扇窗。
    /// <paramref name="pageType"/> 是页面类型，<paramref name="title"/> 显示在标题栏。
    /// </summary>
    public static void Show(Type pageType, string title)
    {
        try
        {
            _instance ??= new ToolPageWindow();
            _instance.Open(pageType, title);
        }
        catch (Exception ex)
        {
            AppLog.Exception("tool-page-window", ex, "打开工具页窗口");
        }
    }

    /// <summary>退出应用时真正销毁（没开过就什么都不做）。</summary>
    public static void CloseForExit()
    {
        var w = _instance;
        _instance = null;
        try { w?.Close(); } catch { }
    }

    private void Open(Type pageType, string title)
    {
        try
        {
            if (_appWindow is not null && !string.IsNullOrWhiteSpace(title)) _appWindow.Title = title;
            TitleText.Text = title;

            // 同一页就不重新导航：用户可能已经拖进文件、算好了哈希
            if (_currentPage != pageType || ContentFrame.Content is null)
            {
                _currentPage = pageType;
                ContentFrame.Navigate(pageType);
            }

            BringUp();
        }
        catch (Exception ex)
        {
            AppLog.Exception("tool-page-window", ex, "切换工具页");
        }
    }

    private void Configure()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));

            if (_appWindow.Presenter is OverlappedPresenter p)
            {
                p.IsResizable = true;
                p.IsMaximizable = true;
                p.IsMinimizable = true;
            }

            ModernWindow.ApplyTitleBar(this, TitleBarHost, Root);

            // 跟应用主题（⛔ 不传 external：分体只作用于侧边栏与常用工具浮窗）
            BackdropHost.Apply(this, Root, App.Settings.Current.Backdrop);
            ThemeHost.Apply(Root);

            ApplySize();
            CenterOnScreen();

            Closed += (_, _) => _instance = null;
        }
        catch (Exception ex)
        {
            AppLog.Exception("tool-page-window", ex, "工具页窗口初始化");
        }
    }

    private void ApplySize()
    {
        if (_appWindow is null) return;
        try
        {
            var scale = Scale();
            var work = DisplayArea.Primary.WorkArea;

            var maxW = work.Width / scale - 40;
            var maxH = work.Height / scale - 60;

            var w = Math.Clamp(DefaultWidthDip, 760, Math.Max(760, maxW));
            var h = Math.Clamp(DefaultHeightDip, 520, Math.Max(520, maxH));

            _appWindow.ResizeClient(new SizeInt32(
                (int)Math.Round(w * scale), (int)Math.Round(h * scale)));
        }
        catch (Exception ex)
        {
            AppLog.Warn("tool-page-window", "工具页窗尺寸设置失败：" + ex.Message);
        }
    }

    private void CenterOnScreen()
    {
        if (_appWindow is null) return;
        try
        {
            var work = DisplayArea.Primary.WorkArea;
            var size = _appWindow.Size;
            _appWindow.Move(new PointInt32(
                work.X + Math.Max(0, (work.Width - size.Width) / 2),
                work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
        }
        catch (Exception ex)
        {
            AppLog.Warn("tool-page-window", "工具页窗定位失败：" + ex.Message);
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
            AppLog.Exception("tool-page-window", ex, "激活工具页窗口");
        }
    }

    private double Scale()
    {
        try
        {
            var dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch { return 1.0; }
    }

    // ── Win32 ────────────────────────────────────────────────────────────

    private const int SW_SHOW = 5;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}
