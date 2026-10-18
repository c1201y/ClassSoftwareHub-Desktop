using System;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 学科工具窗 —— 独立 AppWindow，把函数图像 / 方程求解 / 化学方程式配平三件工具单拎出来。
///
/// <para>
/// 来由（Nick 2026-10-18）：这三件工具是"用完就走"的，但原来只能先进主界面再导航过去；
/// 用户只想算一道题时，打开整个应用太重。这扇窗让侧边栏点一下就直接用，
/// 主窗口可以一直收在托盘里。
/// </para>
///
/// <para>
/// ⚠️ 三个工具页都是纯 <see cref="Page"/>，不依赖 <c>ShellPage</c> / <c>Frame</c> / <c>App.MainWindow</c>
/// （已逐页核过），所以能直接塞进本窗右侧的 Frame 里。
/// </para>
///
/// <para>
/// ⚠️ 关掉 = **真销毁**（不像工具浮窗那种"收起来"）：学科窗没有留着复用的价值，
/// 真关最省事，也不会在退出链路上留下没关掉的窗口把进程吊住（§V）。
/// </para>
/// </summary>
public sealed partial class SubjectToolsWindow : Window
{
    private const int DefaultWidthDip = 1180;
    private const int DefaultHeightDip = 780;

    /// <summary>三件工具（顺序 = 左侧紧凑栏从上到下的顺序）。</summary>
    private static readonly (string Id, string Name, Type Page)[] Tools =
    {
        ("function-plot", "函数图像", typeof(Pages.FunctionPlotPage)),
        ("equation", "方程求解", typeof(Pages.Tools.EquationPage)),
        ("chemistry", "化学方程式配平", typeof(Pages.Tools.ChemistryPage)),
    };

    private static SubjectToolsWindow? _instance;

    private AppWindow? _appWindow;

    /// <summary>
    /// 程序化改选中项时的闸门。**初值就是 true**：
    /// NavigationView 在 Loaded 时会自己选中第一项，那一次不能当成"用户切换了工具"
    /// （否则会把上次用的工具覆盖成函数图像）。真正的选中在 Root.Loaded 里做。
    /// </summary>
    private bool _suppress = true;

    /// <summary>本次希望停在哪个工具（ShowTool 传入 / 存档里上次用的那个）。</summary>
    private string? _wantId;

    private SubjectToolsWindow()
    {
        InitializeComponent();
        Configure();

        // NavigationView 加载完会自选第一项（LegalWindow 的 SelectorBar 也踩过同样的坑）⇒
        // 这时按真正想的那个工具再拨一次；之前那次已经被 _suppress 挡掉了。
        Root.Loaded += (_, _) =>
        {
            _suppress = false;
            SelectTool(_wantId);
        };
    }

    // ── 对外 ─────────────────────────────────────────────────────────────

    /// <summary>摊开这扇窗；已经开着就切到指定工具，再把它调到前面。</summary>
    /// <param name="toolId">function-plot / equation / chemistry；null 或认不出 = 上次用的那个。</param>
    public static void ShowTool(string? toolId)
    {
        try
        {
            _instance ??= new SubjectToolsWindow();

            var id = Normalize(toolId) ?? Normalize(App.Settings.Current.SubjectToolsLastTool) ?? Tools[0].Id;
            _instance._wantId = id;
            _instance.SelectTool(id);
            _instance.BringUp();
        }
        catch (Exception ex)
        {
            AppLog.Exception("subject-tools", ex, "打开学科工具窗口");
        }
    }

    /// <summary>退出应用时真正销毁（见 <see cref="MainWindow"/> 的 FinishExit）。</summary>
    public static void CloseForExit()
    {
        var w = _instance;
        _instance = null;
        try { w?.Close(); } catch { }
        AppLog.Info("subject-tools", "学科工具窗口实例已请求 Close");
    }

    // ── 选中工具 ─────────────────────────────────────────────────────────

    /// <summary>把左侧栏拨到指定工具并导航过去；会把这次选择记进存档（下次打开停在这儿）。</summary>
    private void SelectTool(string? toolId)
    {
        var id = Normalize(toolId) ?? Tools[0].Id;
        var index = Array.FindIndex(Tools, t => t.Id == id);
        if (index < 0) index = 0;

        var was = _suppress;
        _suppress = true;
        try
        {
            Nav.SelectedItem = Nav.MenuItems[index];
        }
        finally
        {
            _suppress = was;
        }

        NavigateTo(Tools[index].Page);
        Remember(Tools[index].Id);
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppress) return;
        if (args.SelectedItem is not NavigationViewItem { Tag: string id }) return;

        var index = Array.FindIndex(Tools, t => t.Id == id);
        if (index < 0) return;

        NavigateTo(Tools[index].Page);
        Remember(Tools[index].Id);
    }

    private void NavigateTo(Type page)
    {
        // 已经停在这一页就别再 Navigate 一次（会白重建一遍页面、把用户刚输入的内容清掉）
        if (ContentFrame.Content?.GetType() == page) return;
        ContentFrame.Navigate(page);
    }

    private static void Remember(string id)
    {
        try
        {
            if (App.Settings.Current.SubjectToolsLastTool == id) return;
            App.Settings.Current.SubjectToolsLastTool = id;
            App.Settings.Save();
        }
        catch (Exception ex)
        {
            AppLog.Warning("subject-tools", "记录上次使用的工具失败：" + ex.Message);
        }
    }

    private static string? Normalize(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var key = id.Trim();
        return Array.Exists(Tools, t => t.Id == key) ? key : null;
    }

    // ── 窗口本身 ─────────────────────────────────────────────────────────

    private void Configure()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "学科工具";

            if (_appWindow.Presenter is OverlappedPresenter p)
            {
                p.SetBorderAndTitleBar(true, true);   // 系统标题栏（拖动 / 阴影 / 圆角交给系统）
                p.IsResizable = true;
                p.IsMaximizable = true;
                p.IsMinimizable = true;
            }

            BackdropHost.Apply(this, Root, App.Settings.Current.Backdrop);
            ThemeHost.Apply(Root);

            ApplySize();
            CenterOnScreen();

            // 真关掉 ⇒ 清掉单例，下次 Show 重新建
            Closed += (_, _) => { _instance = null; };
        }
        catch (Exception ex)
        {
            AppLog.Exception("subject-tools", ex, "学科工具窗口初始化");
        }
    }

    /// <summary>按逻辑像素（dip）定客户区大小，并夹进当前屏幕（小屏上别长出屏幕外）。</summary>
    private void ApplySize()
    {
        if (_appWindow is null) return;
        try
        {
            var scale = Scale();
            var work = DisplayArea.Primary.WorkArea;

            var maxW = work.Width / scale - 40;
            var maxH = work.Height / scale - 60;

            // 函数图像那页是三栏铺满（左 272 + 中 ≥300 + 右 332），太窄会把它压变形
            var w = Math.Clamp(DefaultWidthDip, 960, Math.Max(960, maxW));
            var h = Math.Clamp(DefaultHeightDip, 560, Math.Max(560, maxH));

            _appWindow.ResizeClient(new SizeInt32(
                (int)Math.Round(w * scale), (int)Math.Round(h * scale)));
        }
        catch (Exception ex)
        {
            AppLog.Warn("subject-tools", "学科工具窗尺寸设置失败：" + ex.Message);
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
            AppLog.Warn("subject-tools", "学科工具窗定位失败：" + ex.Message);
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
            AppLog.Exception("subject-tools", ex, "激活学科工具窗口");
        }
    }

    // ── Win32 ────────────────────────────────────────────────────────────

    private const int SW_SHOW = 5;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
