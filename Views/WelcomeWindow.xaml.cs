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




























public sealed partial class WelcomeWindow : Window
{
    private const int WidthDip = 800;
    private const int HeightDip = 600;

    private static WelcomeWindow? _instance;

    
    private static Action? _onDone;

    
    private static bool _preview;

    private AppWindow? _appWindow;

    private OobeSession? _session;

    
    private bool _started;

    
    private bool _finished;

    private bool _closed;

    private WelcomeWindow()
    {
        InitializeComponent();
        Configure();

        _session = OobeSession.Attach(this, OobeDraft.FromSettings());

        
        
        StepFrame.Navigated += (_, _) => UpdateNav();

        
        
        _session.Prepare();

        
        
        Root.Loaded += (_, _) => StartOnce();
        Activated += (_, _) => StartOnce();
    }

    

    
    internal void UpdateNav()
    {
        var index = _session?.Index ?? 0;

        BackNavButton.Visibility = index > 0 ? Visibility.Visible : Visibility.Collapsed;

        
        NextNavButton.Content = StepFrame.Content is OobeFinishPage finish
            ? finish.PrimaryText
            : index == 0 ? "开始" : "下一步";

        
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

    
    internal FrameworkElement ThemeRoot => Root;

    
    internal IntPtr WindowHandle => WindowNative.GetWindowHandle(this);

    
    internal void PreviewBackdrop(string kind)
    {
        try { BackdropHost.Apply(this, Root, kind); }
        catch (Exception ex) { AppLog.Warn("oobe", "背景预览失败：" + ex.Message); }
    }

    

    
    
    
    
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

            
            
            if (!_preview)
            {
                App.Settings.Current.OobeState = OobeStates.Done;
                App.Settings.Save();
            }

            _instance = null;
            onDone();
        }
    }

    

    private void Configure()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "欢迎使用ClassSoftwareHub-Desktop";

            if (_appWindow.Presenter is OverlappedPresenter p)
            {
                
                p.IsResizable = false;
                p.IsMaximizable = false;
                p.IsMinimizable = false;
            }

            
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

    

    private void StartOnce()
    {
        if (_started || _closed) return;
        _started = true;
        _session?.Play();
    }

    
    
    
    
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

        try { Close(); } catch {  }

        
        
        try { _onDone?.Invoke(); }
        catch (Exception ex) { AppLog.Warn("oobe", "收尾回调失败：" + ex.Message); }
    }

    
    
    
    
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

        
        
        s.Backdrop = NormalizeChoice(draft.Backdrop, "mica", "mica", "acrylic", "solid");
        s.SplitTheme = draft.SplitTheme;
        s.ExternalTheme = NormalizeChoice(draft.ExternalTheme, "system", "system", "light", "dark");

        
        
        
        
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

        
        if (shell is not null) shell.SetPaletteOnTop(draft.PaletteOnTop);
        else s.PaletteOnTop = draft.PaletteOnTop;

        s.SidebarEnabled = draft.SidebarEnabled;
        
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

        
        
        var autoStart = draft.AutoStart;
        shell?.SetAutoStart(autoStart);
        shell?.SetMinimizeOnStart(autoStart && draft.MinimizeOnStart);
        shell?.SetCloseToTray(draft.CloseToTray);

        s.GithubDownloadRoute = GithubRoutes.Normalize(draft.GithubRoute);
        GithubRoute.InvalidateCache();          

        s.AutoCheckUpdate = draft.AutoCheckUpdate;
        s.HubAiEnabled = draft.HubAiEnabled;
        s.UserDisplayName = (draft.UserDisplayName ?? "").Trim();
        s.CacheKeepCount = Math.Clamp(draft.CacheKeepCount, 1, 10);

        App.Settings.Save();

        
        
        
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

    
    private static string NormalizeChoice(string? value, string fallback, params string[] allowed)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        return Array.IndexOf(allowed, v) >= 0 ? v : fallback;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}
