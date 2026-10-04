using System;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 「在 Q 群中反馈」的图文流程窗：提醒浮窗里点完「复制」就摊开这一扇，五步走完 = 反馈已经提交到群相册。
///
/// = **标准窗口 + 自绘标题栏**（跟 <see cref="ToolPaletteWindow"/> 一个套路）：
/// 拖动 / 触屏拖动 / 阴影 / 圆角都交给 Windows，我们只管标题栏长什么样。
///
/// ⚠️ 这扇窗**故意留在任务栏和 Alt+Tab 里**（跟项目里其它辅助窗相反）——
///    用户要一边看它一边切到 QQ 里操作，切走了得能 Alt+Tab 摸回来。
///
/// ⚠️ 四张截图走**内嵌资源**（<c>Assets\feedback\qq-step-*.png</c>），跟反馈页那两张卡片图标同一条路，
///    单文件发布下也在（见 <see cref="EmbeddedAssets.ExtractToCache"/>）。
///
/// ⚠️ <c>ExtendsContentIntoTitleBar</c> 会把窗口圆角按回 Default（实测 Win11 上是直角），
///    必须显式要一次 <see cref="WindowChrome.SetRounded"/>（同 ToolPaletteWindow）。
/// </summary>
public sealed partial class QqFeedbackGuideWindow : Window
{
    private const int GuideWidthDip = 700;
    private const int GuideHeightDip = 720;

    private static QqFeedbackGuideWindow? _instance;

    private AppWindow? _appWindow;

    /// <summary>要复制的那段反馈信息（标题 + 正文），由反馈页 / 提醒浮窗传进来。</summary>
    private string _copyText = "";

    private bool _imagesLoaded;
    private bool _visible;

    private QqFeedbackGuideWindow()
    {
        InitializeComponent();
        Configure();
    }

    public static bool IsVisible => _instance?._visible == true;

    /// <summary>摊开这扇窗（已经开着就只更新要复制的文本，再把它调到前面）。</summary>
    public static void Show(string copyText)
    {
        try
        {
            _instance ??= new QqFeedbackGuideWindow();
            _instance._copyText = copyText;
            _instance.EnsureImages();
            _instance.BringUp();
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("Q群反馈流程窗显示失败: " + ex.Message);
        }
    }

    public static void CloseIfOpen() => _instance?.HideSelf();

    /// <summary>
    /// 退出应用时**真正销毁**实例（<see cref="CloseIfOpen"/> 只是藏起来）。
    /// ⛔ 同 <see cref="ToolPaletteWindow.CloseForExit"/>：Application.Exit() 会漏窗口，
    ///    没关掉的窗口会让进程退不掉（2026-10-04 实测）。
    /// </summary>
    public static void CloseForExit()
    {
        var w = _instance;
        _instance = null;
        try { w?.Close(); } catch { }
        Core.AppLog.Info("exit", "QQ反馈窗实例已请求 Close");
    }

    // ── 窗口本身 ─────────────────────────────────────────────────────────

    private void Configure()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "在 Q 群中反馈";

            if (_appWindow.Presenter is OverlappedPresenter p)
            {
                p.SetBorderAndTitleBar(true, true);      // 框架留着（拖动 / 阴影 / 圆角靠系统），标题栏内容自己画
                p.IsResizable = false;                   // 版式是按固定宽度排的，能拉宽反而会散
                p.IsMaximizable = false;
                p.IsMinimizable = true;                  // 用户可能想把它按下去、先专心在 QQ 里操作
            }

            ConfigureTitleBar();
            BackdropHost.Apply(this, Root, App.Settings.Current.Backdrop);
            ThemeHost.Apply(Root);

            ApplySize();
            CenterOnScreen();

            Root.ActualThemeChanged += (_, _) => UpdateCaptionButtonColors();

            // ⚠️ 走 handledEventsToo：焦点多半在里层 ScrollViewer 上，普通订阅收不到 Esc
            Root.AddHandler(UIElement.KeyDownEvent,
                new KeyEventHandler((_, e) =>
                {
                    if (e.Key == Windows.System.VirtualKey.Escape) HideSelf();
                }), true);

            // 关掉 = 收起来，别真销毁（下次 Show 直接复用，省掉重新解图那一趟）
            // ⛔ 应用正在退出时必须放行 —— 否则本窗会拦下 Application.Exit() 的关窗、把整条退出流程
            //    掐断，进程留在任务管理器里（同 ToolPaletteWindow，2026-10-04 实测）。
            _appWindow.Closing += (_, args) =>
            {
                Core.AppLog.Info("exit", $"QQ反馈窗 Closing: IsExiting={App.IsExiting}");
                if (App.IsExiting) return;
                args.Cancel = true;
                HideSelf();
            };
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("Q群反馈流程窗初始化失败: " + ex.Message);
        }
    }

    /// <summary>自绘标题栏：外观我们自己的（不是系统那根），拖动还是系统管（含触屏）。</summary>
    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        if (_appWindow is null) return;
        try
        {
            var bar = _appWindow.TitleBar;
            bar.ExtendsContentIntoTitleBar = true;
            bar.PreferredHeightOption = TitleBarHeightOption.Standard;    // 32px，跟 AppTitleBar 一样高
            bar.ButtonBackgroundColor = Colors.Transparent;               // 按钮底透明，跟面板底色融为一体
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[qq-guide] 标题栏定制不可用: " + ex.Message);
        }

        UpdateCaptionButtonColors();

        // ⚠️ 上一句 ExtendsContentIntoTitleBar 会把窗口圆角偏好按回 Default —— 实测渲染出来是直角。
        WindowChrome.SetRounded(WindowNative.GetWindowHandle(this), rounded: true);
    }

    /// <summary>右上角系统按钮的配色跟着深浅色走（照搬「常用工具」窗那套）。</summary>
    private void UpdateCaptionButtonColors()
    {
        if (_appWindow is null) return;
        var dark = Root.ActualTheme == ElementTheme.Dark;
        try
        {
            var bar = _appWindow.TitleBar;
            bar.ButtonForegroundColor = dark ? Colors.White : Windows.UI.Color.FromArgb(255, 30, 30, 30);
            bar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
            bar.ButtonHoverBackgroundColor = dark
                ? Windows.UI.Color.FromArgb(26, 255, 255, 255)
                : Windows.UI.Color.FromArgb(20, 0, 0, 0);
            bar.ButtonPressedForegroundColor = dark
                ? Windows.UI.Color.FromArgb(255, 200, 200, 200)
                : Windows.UI.Color.FromArgb(255, 90, 90, 90);
            bar.ButtonInactiveForegroundColor = dark
                ? Windows.UI.Color.FromArgb(160, 255, 255, 255)
                : Windows.UI.Color.FromArgb(140, 0, 0, 0);
            bar.ButtonPressedBackgroundColor = dark
                ? Windows.UI.Color.FromArgb(40, 255, 255, 255)
                : Windows.UI.Color.FromArgb(30, 0, 0, 0);
        }
        catch { }
    }

    /// <summary>
    /// 按逻辑像素（dip）定**客户区**大小，并夹进当前屏幕 ——
    /// 教室机器上还有 1366×768，700×720 的固定尺寸会在那种屏上长出屏幕外。
    /// </summary>
    private void ApplySize()
    {
        if (_appWindow is null) return;
        try
        {
            var scale = Scale();
            var work = DisplayArea.Primary.WorkArea;

            // 屏幕尺寸也是物理像素，除回 dip；再留出标题栏 + 一点边距
            var maxW = work.Width / scale - 60;
            var maxH = work.Height / scale - 100;

            var w = (int)Math.Round(Math.Clamp(GuideWidthDip, 420, Math.Max(420, maxW)));
            var h = (int)Math.Round(Math.Clamp(GuideHeightDip, 380, Math.Max(380, maxH)));

            _appWindow.ResizeClient(new SizeInt32(
                (int)Math.Round(w * scale), (int)Math.Round(h * scale)));
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[qq-guide] 尺寸设置失败: " + ex.Message);
        }
    }

    /// <summary>摆在主屏正中（跟「常用工具」窗同一个默认位置；这扇窗不做位置记忆）。</summary>
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
            Debug.WriteLine("[qq-guide] 定位失败: " + ex.Message);
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

    // ── 显示 / 收起 ───────────────────────────────────────────────────────

    private void BringUp()
    {
        _visible = true;
        try
        {
            Activate();
            var hwnd = WindowNative.GetWindowHandle(this);
            ShowWindow(hwnd, SW_SHOW);
            SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("Q群反馈流程窗激活失败: " + ex.Message);
        }
    }

    private void HideSelf()
    {
        _visible = false;
        try { ShowWindow(WindowNative.GetWindowHandle(this), SW_HIDE); } catch { }
    }

    // ── 截图 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 四张操作截图从内嵌资源解到缓存目录再挂上去。**只解一次** ——
    /// 每次 Show 都重解一遍纯属白读几 MB 的 IO。
    /// </summary>
    private void EnsureImages()
    {
        if (_imagesLoaded) return;
        _imagesLoaded = true;

        Bind(StepImage1, "qq-step-album-panel.png");
        Bind(StepImage2, "qq-step-create-album.png");
        Bind(StepImage3, "qq-step-upload-button.png");
        Bind(StepImage4, "qq-step-upload-pick.png");
    }

    private static void Bind(Microsoft.UI.Xaml.Controls.Image target, string fileName)
    {
        try
        {
            var path = EmbeddedAssets.ExtractToCache(fileName, fileName);
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
            target.Source = new BitmapImage(new Uri(path));
        }
        catch (Exception ex)
        {
            // 图读不到就空着，别把整扇窗搞崩（同 FeedbackPage.LoadIcon 的处理）
            Debug.WriteLine("[qq-guide] 截图加载失败 " + fileName + ": " + ex.Message);
        }
    }

    // ── 交互 ─────────────────────────────────────────────────────────────

    /// <summary>剪贴板被别的东西覆盖了（在 QQ 里聊了几句就可能）→ 让她不用回反馈页也能再来一次。</summary>
    private void CopyAgain_Click(object sender, RoutedEventArgs e)
    {
        if (_copyText.Length == 0) return;
        if (QqFeedback.Copy(_copyText))
            (sender as Microsoft.UI.Xaml.Controls.Button)!.Content = "已复制到剪贴板";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => HideSelf();

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape) HideSelf();
    }

    // ── Win32 ────────────────────────────────────────────────────────────

    private const int SW_SHOW = 5;
    private const int SW_HIDE = 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
