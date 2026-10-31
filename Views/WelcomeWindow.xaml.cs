using System;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Views.Oobe;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 初次配置（OOBE，需求 1:22，2026-10-31）。
///
/// <para>
/// 第三版：照 ClassIsland 的欢迎向导**重写逻辑**，不搬它的控件。窗口自己只剩两件事 ——
/// 一个装页面的 Frame，和"什么时候前进/后退/收尾"的调度；每一步是一个独立 Page
/// （见 Views/Oobe），步骤表、当前位置、草稿都在 <see cref="OobeSession"/> 里。
/// Frame 缓存页面实例，往回翻时用户刚填的值还在。
/// </para>
///
/// <para>
/// 什么时候弹：只有**新装**（<c>settings.json</c> 不存在）那一次启动，以及上次配到一半退出。
/// 判定与置位都在 <c>App.OnLaunched</c>，本窗只负责显示与收值 —— 判定逻辑不要挪进来，
/// 那边必须在 <c>Settings.Load()</c> 之前取"文件在不在"，晚了就被 Load 里那次 Save 抹掉。
/// </para>
///
/// <para>
/// ⛔ 升级上来的老用户**永远不弹**：他们的存档里没有 <c>OobeState</c> 字段，读出来是
/// <see cref="OobeStates.Unknown"/>，走"不弹"分支。别改成"字段为假就弹"。
/// </para>
///
/// <para>
/// 收值时机：点「完成」和点窗口的 × 都要落定，按草稿写进设置。
/// 不提供「跳过」（2026-10-31 Nick 定）：初次配置是必选的，跳过会静默丢掉用户
/// 前面选好的主题等项 —— "OOBE 选了浅色、进主界面还是深色"就是这么来的。
/// </para>
/// </summary>
public sealed partial class WelcomeWindow : Window
{
    private const int WidthDip = 800;
    private const int HeightDip = 600;

    private static WelcomeWindow? _instance;

    /// <summary>配置结束后要做的事（= 把主窗口亮出来）。由 <see cref="ShowForFirstRun"/> 设进来。</summary>
    private static Action? _onDone;

    /// <summary>调试预览（<c>--oobe-preview</c>）：不读写 OobeState、不应用设置。</summary>
    private static bool _preview;

    private AppWindow? _appWindow;

    private OobeSession? _session;

    /// <summary>已经让第一页进场（窗口显示一次就够，Activated 会反复触发）。</summary>
    private bool _started;

    /// <summary>已经在收尾（防止「完成」里 Close 又被 Closed 处理一次）。</summary>
    private bool _finished;

    private bool _closed;

    private WelcomeWindow()
    {
        InitializeComponent();
        Configure();

        _session = OobeSession.Attach(this, OobeDraft.FromSettings());

        // 底部导航条跟着当前页刷新（上一步是否可用、主按钮叫什么）。
        // ⚠️ 要先挂事件再 Prepare：Prepare 里那次导航就是第一页，晚了按钮文案会停在默认值。
        StepFrame.Navigated += (_, _) => UpdateNav();

        // 第一页在这里就建好（含开场控件），窗口显示出来时第一帧就有内容。
        // 动画不在这里播：那个等下面 StartOnce 的 Activated。
        _session.Prepare();

        // 导航必须等窗口真的显示出来：构造期间播动画，页面会直接跳到终值，
        // 用户看到的是"一闪就到位"，等于没做。Loaded 与 Activated 谁先到都行，用守卫去重。
        Root.Loaded += (_, _) => StartOnce();
        Activated += (_, _) => StartOnce();
    }

    // ── 底部导航条 ───────────────────────────────────────────────────────

    /// <summary>按当前页刷新底部按钮：第 1 步只留「开始」，其余页在左、右各一颗。</summary>
    internal void UpdateNav()
    {
        var index = _session?.Index ?? 0;

        BackNavButton.Visibility = index > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 完成页自己管轮播：到最后一屏主按钮就变成「完成」
        NextNavButton.Content = StepFrame.Content is OobeFinishPage finish
            ? finish.PrimaryText
            : index == 0 ? "开始" : "下一步";

        // 页面可以拦住下一步（协议未勾选）；其余页一律放行
        NextNavButton.IsEnabled = StepFrame.Content is not IOobeStepGuard guard || guard.CanAdvance;
    }

    private void NavBack_Click(object sender, RoutedEventArgs e)
    {
        if (StepFrame.Content is OobeFinishPage finish) finish.StepBack();
        else _session?.Back();
    }

    private void NavNext_Click(object sender, RoutedEventArgs e)
    {
        if (StepFrame.Content is IOobeStepGuard guard && !guard.CanAdvance) return;
        if (StepFrame.Content is OobeFinishPage finish) finish.StepForward();
        else _session?.Next();
    }

    /// <summary>页面换主题时改这一层的 RequestedTheme，整棵子树跟着重取 ThemeResource。</summary>
    internal FrameworkElement ThemeRoot => Root;

    /// <summary>本窗句柄（目录选择器之类的 owner）。</summary>
    internal IntPtr WindowHandle => WindowNative.GetWindowHandle(this);

    /// <summary>背景材质预览：只作用于本窗，收尾时才写进设置。</summary>
    internal void PreviewBackdrop(string kind)
    {
        try { BackdropHost.Apply(this, Root, kind); }
        catch (Exception ex) { AppLog.Warn("oobe", "背景预览失败：" + ex.Message); }
    }

    // ── 对外 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 该弹就弹；<paramref name="onDone"/> 在收尾（完成 / 关窗）后调用一次 ——
    /// 新装首启时主窗口就是靠它才露面的。
    /// </summary>
    public static void ShowForFirstRun(Action onDone)
    {
        _preview = false;
        if (App.Settings.Current.OobeState == OobeStates.Done || _instance is not null)
        {
            onDone();
            return;
        }

        ShowCore(onDone);
    }

#if DEBUG
    /// <summary>
    /// 仅调试构建：强制预览一次向导，**不读写 OobeState、不应用设置**。
    /// 用法：<c>ClassSoftwareHub.exe --oobe-preview</c>（见 App.OnLaunched）。
    /// 发布构建里这个方法不存在。
    /// </summary>
    public static void ShowPreview(Action onDone)
    {
        _preview = true;
        ShowCore(onDone);
    }
#endif

    private static void ShowCore(Action onDone)
    {
        _onDone = onDone;

        try
        {
            _instance = new WelcomeWindow();
            _instance.Activate();
            AppLog.Info("oobe", _preview ? "初次配置窗口已弹出（预览）" : "初次配置窗口已弹出");
        }
        catch (Exception ex)
        {
            AppLog.Exception("oobe", ex, "初次配置窗口创建失败");

            // ⛔ 建不出来不能就这么挂着：不改状态的话，之后**每次**启动都会再试一遍、
            //    每次都失败、每次都在日志里留一坨异常。直接按"已处理"记账，让用户进主界面。
            if (!_preview)
            {
                App.Settings.Current.OobeState = OobeStates.Done;
                App.Settings.Save();
            }

            _instance = null;
            onDone();
        }
    }

    // ── 初始化 ───────────────────────────────────────────────────────────

    private void Configure()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "欢迎使用ClassSoftwareHub-Desktop";

            if (_appWindow.Presenter is OverlappedPresenter p)
            {
                // 固定尺寸：这是引导流程，不是工作窗口，缩放没有意义，还会把分步内容挤花
                p.IsResizable = false;
                p.IsMaximizable = false;
                p.IsMinimizable = false;
            }

            // 标题栏只留关闭：最小化/最大化已被 presenter 关掉，留着两颗是死按钮
            WindowChrome.HideMinimizeMaximizeButtons(hwnd);

            ModernWindow.ApplyTitleBar(this, TitleBarHost, Root);

            BackdropHost.Apply(this, Root, App.Settings.Current.Backdrop);
            ThemeHost.Apply(Root);

            ApplySize();
            CenterOnScreen();

            Closed += (_, _) =>
            {
                _closed = true;
                _instance = null;
                FinishOobe();
            };
        }
        catch (Exception ex)
        {
            AppLog.Exception("oobe", ex, "初次配置窗口初始化");
        }
    }

    private void ApplySize()
    {
        if (_appWindow is null) return;
        try
        {
            var scale = Scale();
            var work = DisplayArea.Primary.WorkArea;

            // 教室机器常见 1366×768 / 125% 缩放：600dip 会接近工作区上限，这里按实际工作区收
            var maxW = Math.Max(700, work.Width / scale - 40);
            var maxH = Math.Max(480, work.Height / scale - 40);

            _appWindow.ResizeClient(new SizeInt32(
                (int)Math.Round(Math.Clamp(WidthDip, 700, maxW) * scale),
                (int)Math.Round(Math.Clamp(HeightDip, 480, maxH) * scale)));
        }
        catch (Exception ex)
        {
            AppLog.Warn("oobe", "初次配置窗口尺寸设置失败：" + ex.Message);
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
            AppLog.Warn("oobe", "初次配置窗口定位失败：" + ex.Message);
        }
    }

    // ── 换页（由 OobeSession 调） ─────────────────────────────────────────

    private void StartOnce()
    {
        if (_started || _closed) return;
        _started = true;
        _session?.Play();
    }

    /// <summary>
    /// 前进/后退都走这里：<paramref name="forward"/> 决定页面从右边还是左边滑进来。
    /// 首屏（<paramref name="animate"/> = false）不播转场 —— 开场动画自己会把画面立起来。
    /// </summary>
    internal void NavigateToPage(Type page, bool forward, bool animate)
    {
        if (_closed) return;

        try
        {
            NavigationTransitionInfo transition = animate
                ? new SlideNavigationTransitionInfo
                {
                    Effect = forward
                        ? SlideNavigationTransitionEffect.FromRight
                        : SlideNavigationTransitionEffect.FromLeft,
                }
                : new SuppressNavigationTransitionInfo();

            StepFrame.Navigate(page, null, transition);
        }
        catch (Exception ex)
        {
            AppLog.Exception("oobe", ex, "向导换页失败：" + page.Name);
        }
    }

    // ── 收尾 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 收尾：把草稿写进设置、标记初次配置走完。
    /// 幂等 —— Closed 与「完成」都会走到这里，第二次直接返回。
    /// </summary>
    internal void FinishOobe()
    {
        if (_finished) return;
        _finished = true;

        if (_preview)
        {
            AppLog.Info("oobe", "预览模式：不写入任何设置");
        }
        else
        {
            if (_session is not null) ApplySettings(_session.Draft);

            App.Settings.Current.OobeState = OobeStates.Done;
            App.Settings.Save();

            AppLog.Info("oobe", "初次配置完成，已按选择写入设置");
        }

        OobeSession.Detach();

        try { Close(); } catch { /* 已经从 × 进来时窗口已关 */ }

        // 主窗口就是在这里才出现的（新装首启时它一直没显示过）。
        // ⚠️ 放在 Close 之后：先收掉向导，再亮主界面，屏幕上不会同时存在两扇窗。
        try { _onDone?.Invoke(); }
        catch (Exception ex) { AppLog.Warn("oobe", "收尾回调失败：" + ex.Message); }
    }

    /// <summary>
    /// 完成页的入口：先收尾（写设置 + 亮主窗口），再跳页。
    /// ⚠️ 顺序不能反 —— 主窗口还没显示时 Shell 的 Frame 还没导航过任何页面，先跳会被吞掉。
    /// </summary>
    internal void FinishAndNavigate(string tag)
    {
        FinishOobe();

        try { App.MainWindow?.Shell.NavigateTo(tag); }
        catch (Exception ex) { AppLog.Warn("oobe", "跳转失败：" + tag + "：" + ex.Message); }
    }

    private static void ApplySettings(OobeDraft draft)
    {
        var s = App.Settings.Current;
        var shell = App.MainWindow;

        // 分体外观与背景材质必须**先写**：SetTheme 内部会按 Backdrop 重算材质，
        // 并通过 ThemeHost.Notify 让侧边栏 / 浮窗按 SplitTheme + ExternalTheme 取色。
        s.Backdrop = NormalizeChoice(draft.Backdrop, "mica", "mica", "acrylic", "solid");
        s.SplitTheme = draft.SplitTheme;
        s.ExternalTheme = NormalizeChoice(draft.ExternalTheme, "system", "system", "light", "dark");

        // 外观走主窗口的 SetTheme：它内部会 Save、重算背景材质、通知侧边栏与浮窗跟着换。
        // ⛔ 别在这儿直接写 s.Theme —— 少那两步，用户会看到"主窗口换了、侧边栏还是旧的"。
        // ⚠️ 主窗口这时已经建好（只是还没显示）。万一它没建出来，也要把存档写对：
        //    否则用户选浅色、进主界面还是深色，下次启动也纠正不回来（2026-10-31 反馈）。
        var theme = string.IsNullOrWhiteSpace(draft.Theme) ? "system" : draft.Theme.Trim().ToLowerInvariant();
        if (shell is not null)
        {
            shell.SetTheme(theme);
        }
        else
        {
            s.Theme = theme;
            ThemeHost.Notify();
        }
        AppLog.Info("oobe", "外观写入：" + theme);

        // 常用工具浮窗置顶有独立入口：它要同步通知已经开着的浮窗。
        if (shell is not null) shell.SetPaletteOnTop(draft.PaletteOnTop);
        else s.PaletteOnTop = draft.PaletteOnTop;

        s.SidebarEnabled = draft.SidebarEnabled;
        // 贴边与常驻必须在 ApplySetting() 之前写进设置 —— 它按设置重建侧边栏实例。
        s.SidebarPinned = draft.SidebarPinned;
        s.SidebarLongLabels = draft.SidebarLongLabels;
        s.SidebarMode = draft.SidebarMode == "free" ? "free" : "dock";
        s.SidebarEdge = NormalizeChoice(draft.SidebarEdge, "right", "left", "right", "both");
        s.SidebarFreeEdge = NormalizeChoice(draft.SidebarFreeEdge, "right", "left", "right", "top", "bottom");
        s.SidebarDensity = draft.SidebarDensity == "compact" ? "compact" : "standard";
        s.SidebarExpandedDensity = NormalizeChoice(draft.SidebarExpandedDensity, "same", "same", "standard", "compact");
        s.SidebarColumns = draft.SidebarColumns == 3 ? 3 : 1;
        s.SidebarHeightRatio = Math.Clamp(draft.SidebarHeightRatio, 0.45, 0.95);
        s.SidebarHeightAuto = draft.SidebarHeightAuto;
        s.SidebarCollapsedGap = Math.Clamp(draft.SidebarCollapsedGap, 0, 24);

        s.ShotAutoSave = draft.ShotAutoSave;
        s.ShotSaveDir = (draft.ShotSaveDir ?? "").Trim();

        // 启动三件套。SetAutoStart 会写 HKCU 的 Run 项，SetMinimizeOnStart 会重算自启命令行；
        // 没开自启时"最小化启动"没有意义，一并清掉，免得存档里留一个永不到达的 true。
        var autoStart = draft.AutoStart;
        shell?.SetAutoStart(autoStart);
        shell?.SetMinimizeOnStart(autoStart && draft.MinimizeOnStart);
        shell?.SetCloseToTray(draft.CloseToTray);

        s.GithubDownloadRoute = GithubRoutes.Normalize(draft.GithubRoute);
        GithubRoute.InvalidateCache();          // 刚换的线路立刻生效，不用等 8 分钟的探测缓存过期

        s.AutoCheckUpdate = draft.AutoCheckUpdate;
        s.HubAiEnabled = draft.HubAiEnabled;
        s.UserDisplayName = (draft.UserDisplayName ?? "").Trim();
        s.CacheKeepCount = Math.Clamp(draft.CacheKeepCount, 1, 10);

        App.Settings.Save();

        // 侧边栏开关切换后要立刻生效（平时只有侧边栏布局页做这一步）。
        // ⚠️ 主窗口这时还没显示，ApplySetting 会照常把侧边栏摆出来 —— 所以放在 Save 之后、
        //    回调（Activate 主窗口）之前，用户第一次看见桌面时它已经在正确的位置上。
        try { ToolSidebarWindow.ApplySetting(); }
        catch (Exception ex) { AppLog.Warn("oobe", "侧边栏开关应用失败：" + ex.Message); }
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

    /// <summary>下拉值归一：认不出的值落到默认档，别把草稿里的脏值写进存档。</summary>
    private static string NormalizeChoice(string? value, string fallback, params string[] allowed)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        return Array.IndexOf(allowed, v) >= 0 ? v : fallback;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}
