using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Views;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.UI;
using WinRT;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly SettingsStore _settings = App.Settings;
    private readonly BridgeService _bridge;
    private readonly WebViewRuntimeService _runtime = new();
    private readonly DispatcherQueueTimer _loadTimer;

    private AppWindow? _appWindow;
    private bool _webReady;
    private bool _navigatedOk;
    private bool _themeFromWeb;
    private bool _firstActivated;
    private readonly bool _startMinimized;
    private readonly bool _startPalette;

    private TrayIcon? _tray;
    private TrayIcon? _trayTools;      // 第二个托盘图标：常用工具（左键直接开工具窗口）
    private bool _exitRequested;

    /// <summary>托盘气泡被点的时候要干什么（不同的通知点进去该去不同的地方）。</summary>
    private Action? _balloonAction;

    /// <summary>当前正开着的下载进度弹窗 + 它盯着的那条任务（同时只有一个弹窗）。</summary>
    private ContentDialog? _downloadDialog;
    private string? _downloadDialogTaskId;

    /// <summary>网页上报的「可拖动矩形」（物理像素），仅在开启原生 caption 区域时使用。</summary>
    private readonly List<RectInt32> _webCaptionRects = new();

    public MainWindow()
    {
        InitializeComponent();

        Title = ShellConfig.WindowTitle;

        _loadTimer = DispatcherQueue.CreateTimer();
        _loadTimer.Interval = TimeSpan.FromSeconds(ShellConfig.LoadTimeoutSeconds);
        _loadTimer.IsRepeating = false;
        _loadTimer.Tick += (_, _) => ShowNetworkError("连接超时");

        _bridge = new BridgeService(this, _settings);

        ConfigureWindow();
        ConfigureTitleBar();
        ApplyTheme(_settings.Current.Theme);
        ApplyBackdrop(_settings.Current.Backdrop);
        LoadLoadingIcon();

        Closed += OnWindowClosed;

        // 托盘图标：关窗口收托盘、开机最小化收托盘、托盘菜单（工具浮窗 / 退出）都靠它
        InitTray();

        // 下载跑完（成功 / 失败）→ 弹系统通知。谁在盯着弹窗的那条不弹，交给弹窗自己收尾。
        DownloadManager.Current.Finished += OnDownloadFinished;

        // ===== 原生界面 =====
        // 软件内容来自内容包（开发时读站点工程 dist/content，正式版走远端 manifest）
        App.Content.Load();
        NativeShell.Init();
        NativeShell.SetIcon(LoadingIcon.Source);
        SetTitleBar(NativeShell.TitleBarElement);

        // 后台同步内容包（远端发布过 content/manifest.json 才会真的下载；没有就继续用本机数据）
        _ = SyncContentAsync();

        // 开机自启 + 「开机最小化」：注册表里会带 --minimized，启动时收进任务栏
        _startMinimized = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        // 直接开工具浮窗（可以拿它建个桌面快捷方式：ClassSoftwareHub.exe --palette）
        _startPalette = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--palette", StringComparison.OrdinalIgnoreCase));
        Activated += OnFirstActivated;

        _ = BootAsync();
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_firstActivated) return;
        _firstActivated = true;
        Activated -= OnFirstActivated;
        if (_startPalette)
        {
            // --palette：主窗口不露脸，托盘 + 工具浮窗直接摆出来
            HideToTray();
            Views.ToolPaletteWindow.ShowTool();
        }
        else if (_startMinimized)
        {
            // 「开机最小化启动」= 直接收进托盘（不占任务栏），想用的时候从托盘图标点出来
            HideToTray();
        }

        // 启动时查一次更新：**不强制**，有新版就问用户（主窗口没露脸时发系统通知）
        _ = CheckUpdateOnStartupAsync();
    }

    private async Task CheckUpdateOnStartupAsync()    {
        try
        {
            if (!_settings.Current.AutoCheckUpdate) return;

            var service = Services.Updating.UpdateService.CreateDefault();
            if (!service.Source.IsConfigured) return;

            var channel = Services.Updating.UpdateChannels.Parse(_settings.Current.UpdateChannel);
            var result = await service.CheckAsync(channel, ShellConfig.ShellVersion);
            if (result is not { HasUpdate: true, Release: { } release }) return;

            // 这种启动方式主窗口是藏着的（--minimized / --palette / 直接收进托盘）
            // → 弹对话框没人看，改发一条系统通知，点通知再把界面叫出来
            if (!IsWindowVisible(WindowNative.GetWindowHandle(this)))
            {
                NotifyUpdateAvailable(release);
                return;
            }

            var root = (Content as FrameworkElement)?.XamlRoot;
            if (root is null) return;

            // 让用户自己决定；选「稍后」就安静放过，下次启动还会再问一次
            if (!await Services.Updating.UpdateFlow.AskAsync(root, release)) return;
            await Services.Updating.UpdateFlow.RunAsync(root, service, release);
        }
        catch
        {
            // 启动时查更新失败就安静放过，别影响正常使用
        }
    }

    /// <summary>主窗口没露脸时，用系统通知提醒"有新版本"（点通知 = 打开主界面并重新走一次检查）。</summary>
    private void NotifyUpdateAvailable(Services.Updating.UpdateRelease release)
    {
        ShowBalloon($"发现新版本 {release.Tag}", "当前不是最新版，点这里看看要不要更新。",
            () => { ShowFromTray(); _ = CheckUpdateManualAsync(); });
    }

    /// <summary>下载跑完的提示：完成报一声「好了」，失败也报一声（别让人以为还在下）。</summary>
    private void OnDownloadFinished(DownloadTask task)
    {
        // 进度弹窗正开着它 → 弹窗自己会弹「下载完成 / 下载失败」，别再叠一条系统通知
        if (task.Id == _downloadDialogTaskId) return;

        if (task.State == DownloadState.Completed)
        {
            ShowBalloon("下载完成", $"{task.FileName}\n已保存到：{DownloadService.DefaultDir}",
                () => { ShowFromTray(); Shell.NavigateTo("downloads"); });
        }
        else if (task.State == DownloadState.Failed)
        {
            ShowBalloon("下载失败", $"{task.Title} 没能下载完：{task.Error}",
                () => { ShowFromTray(); Shell.NavigateTo("downloads"); });
        }
    }

    /// <summary>
    /// 弹一条系统通知（托盘气泡），并记住"点它该干什么"。
    /// 不用 Windows 的 AppNotification 是因为免安装（unpackaged）下它要额外注册一套 AUMID/COM
    /// 激活器，而托盘气泡本来就在用（发现新版本那条），零依赖、装了就能用。
    /// </summary>
    private void ShowBalloon(string title, string text, Action? onClick)
    {
        _balloonAction = onClick;
        try { _tray?.ShowBalloon(title, text); }
        catch { }
    }

    /// <summary>
    /// 启动后台同步内容包：远端有 content/manifest.json 就把变化的文件拖到本地缓存
    /// （%LOCALAPPDATA%\ClassSoftwareHub\content），下次启动 ContentStore 就优先用它，
    /// 不再依赖开发目录。远端还没发布时什么都不做。
    /// </summary>
    private async Task SyncContentAsync()
    {
        try
        {
            var before = App.Content.SourceKind;
            var result = await Services.ContentUpdater.SyncAsync();

            if (!result.Updated && before == "cache") return;

            // 有更新（或本来用的是开发目录）→ 重新读一遍，让内容源切到本地缓存
            var oldSource = App.Content.Source;
            var oldCount = App.Content.Apps.Count;
            App.Content.Load();

            var changed = App.Content.Source != oldSource || App.Content.Apps.Count != oldCount;
            if (changed)
                Debug.WriteLine($"[content] {result.Message}；{oldSource} → {App.Content.Source}");
        }
        catch
        {
            // 内容同步失败不打扰用户：本机数据照样能用
        }
    }

    private SubmitWindow? _submitWindow;

    public SubmitWindow? SubmitWindowRef => _submitWindow;

    /// <summary>原生外壳（首页卡片等要从这里跳导航）。</summary>
    public Pages.ShellPage Shell => NativeShell;

    /// <summary>
    /// ⚠️ 遗留路径：「提交软件」的网页版小窗口（Views/SubmitWindow）。提交页早已原生化（Pages/SubmitPage），
    /// 现在没有入口调用这里（OpenSubmitWindow 无人调用），留着当兜底；缺 WebView2 时给替代方案。
    /// </summary>
    public async void OpenSubmitWindow()
    {
        if (_submitWindow is not null)
        {
            _submitWindow.Activate();
            return;
        }

        // 兜底：这是唯一需要 WebView2 的地方。系统里没有运行时 → 给两条退路，别让它白屏报错。
        if (!_runtime.Probe())
        {
            await ShowRuntimeMissingDialogAsync(ShellConfig.SubmitPageUrl);
            return;
        }

        _submitWindow = new SubmitWindow();
        _submitWindow.Closed += (_, _) => _submitWindow = null;
        _submitWindow.Activate();
    }

    // ============================================================
    // 窗口 & 标题栏
    // ============================================================

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appUserModelId);

    private const int WM_SETICON = 0x0080;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hinst, string name, uint type, int cx, int cy, uint load);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private bool _iconApplied;

    /// <summary>图标文件：优先用内嵌的 AppIcon.ico（多尺寸），取不到就退回 exe 自己。</summary>
    private static string IconPath()
    {
        var ico = EmbeddedAssets.ExtractToCache("AppIcon.ico", "AppIcon.ico");
        if (!string.IsNullOrEmpty(ico) && System.IO.File.Exists(ico)) return ico;
        return Environment.ProcessPath ?? "";
    }

    /// <summary>「常用工具」托盘图标用的图标：扳手那个（跟主界面图标区分开）。</summary>
    private static string ToolIconPath()
    {
        var ico = EmbeddedAssets.ExtractToCache("ToolIcon.ico", "ToolIcon.ico");
        if (!string.IsNullOrEmpty(ico) && System.IO.File.Exists(ico)) return ico;
        return IconPath();
    }

    /// <summary>
    /// 打窗口图标。除了 AppWindow.SetIcon 之外再补一发老式的 WM_SETICON ——
    /// 有的环境下 SetIcon 不生效，任务栏 / Alt-Tab / Win+Tab 就一直是系统默认图标。
    /// </summary>
    private void ApplyWindowIcon(IntPtr hwnd)
    {
        var icon = IconPath();
        if (icon.Length == 0) return;

        try { _appWindow?.SetIcon(icon); } catch { }

        try
        {
            var big = LoadImage(IntPtr.Zero, icon, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
            var small = LoadImage(IntPtr.Zero, icon, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
            if (big != IntPtr.Zero) SendMessage(hwnd, WM_SETICON, new IntPtr(1), big);
            if (small != IntPtr.Zero) SendMessage(hwnd, WM_SETICON, IntPtr.Zero, small);
        }
        catch { }
    }

    private void ConfigureWindow()
    {
        // 给进程一个显式的任务栏身份：任务栏按这个 id 取图标，
        // 避免 Windows 拿旧缓存（上次那个"只有 256 一张图、缩不来"的 ico）继续显示通用图标。
        try { SetCurrentProcessExplicitAppUserModelID("TinyNick.ClassSoftwareHub.Desktop"); }
        catch { /* 设不上不影响使用 */ }
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[shell] AppWindow 获取失败: " + ex.Message);
        }

        if (_appWindow is null) return;

        _appWindow.Title = ShellConfig.WindowTitle;

        try
        {
            ApplyWindowIcon(WindowNative.GetWindowHandle(this));
        }
        catch { }

        // 窗口显示之后再补一次：有的环境下窗口一显示图标会被系统重置回默认
        try
        {
            var hwndForIcon = WindowNative.GetWindowHandle(this);
            Activated += (_, _) =>
            {
                if (_iconApplied) return;
                _iconApplied = true;
                ApplyWindowIcon(hwndForIcon);
            };
        }
        catch { }

        try
        {
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = true;
                presenter.IsMaximizable = true;
                presenter.IsMinimizable = true;
                presenter.PreferredMinimumWidth = 900;
                presenter.PreferredMinimumHeight = 600;
                presenter.IsAlwaysOnTop = _settings.Current.AlwaysOnTop;
            }
        }
        catch { }

        try
        {
            var w = Math.Max(900, _settings.Current.WindowWidth);
            var h = Math.Max(600, _settings.Current.WindowHeight);
            _appWindow.Resize(new SizeInt32(w, h));
        }
        catch { }

        _appWindow.Closing += (_, _) => SaveWindowState();

        // 点右上角 × 默认收进托盘（可在设置 / 内置工具页关掉）；托盘挂不上就直接退，别把用户困在后台
        _appWindow.Closing += (_, args) =>
        {
            if (_exitRequested || !_settings.Current.CloseToTray || _tray?.IsReady != true) return;
            args.Cancel = true;
            HideToTray();
        };
        _appWindow.Changed += (_, args) =>
        {
            if (args.DidSizeChange || args.DidPresenterChange)
            {
                SendCaptionInsets();
                RequestTitlebarRegions();
            }
        };
    }

    /// <summary>
    /// 混合标题栏：网页自带 win-titlebar 就是标题栏；这里保留系统按钮（右上角）。
    /// 不调用 SetTitleBar —— 拖动由「网页判断 + 桥接消息」实现（规格书要求的路径）。
    /// </summary>
    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;

        if (_appWindow is null) return;
        try
        {
            var bar = _appWindow.TitleBar;
            bar.ExtendsContentIntoTitleBar = true;
            bar.PreferredHeightOption = TitleBarHeightOption.Tall;   // 48px
            bar.ButtonBackgroundColor = Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[shell] 标题栏定制不可用: " + ex.Message);
        }

        UpdateCaptionButtonColors();
    }

    private void UpdateCaptionButtonColors()
    {
        if (_appWindow is null) return;
        var dark = RootGrid.ActualTheme == ElementTheme.Dark;
        try
        {
            var bar = _appWindow.TitleBar;
            bar.ButtonForegroundColor = dark ? Colors.White : Color.FromArgb(255, 30, 30, 30);
            bar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
            bar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0);
            bar.ButtonPressedForegroundColor = dark ? Color.FromArgb(255, 200, 200, 200) : Color.FromArgb(255, 90, 90, 90);
            bar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(160, 255, 255, 255) : Color.FromArgb(140, 0, 0, 0);
            bar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0);
        }
        catch { }
    }

    private double GetScale()
    {
        try
        {
            var s = RootGrid.XamlRoot?.RasterizationScale ?? 0;
            return s > 0 ? s : 1.0;
        }
        catch { return 1.0; }
    }

    private double GetCaptionInsetCss()
    {
        if (_appWindow is null) return 138;
        try { return Math.Round(_appWindow.TitleBar.RightInset / GetScale()); }
        catch { return 138; }
    }

    private void SendCaptionInsets()
    {
        if (_appWindow is null) return;
        try
        {
            var bar = _appWindow.TitleBar;
            var scale = GetScale();
            _bridge.Send("shell.captionInset", new
            {
                value = Math.Round(bar.RightInset / scale),
                height = Math.Round(bar.Height / scale),
                left = Math.Round(bar.LeftInset / scale),
                scale
            });
        }
        catch { }
    }

    private void RequestTitlebarRegions()
        => _ = ExecuteScriptAsync("window.cshShell && window.cshShell.post('titlebarUpdate');");

    // ============================================================
    // 拖动 / 最大化
    // ============================================================

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;

    /// <summary>
    /// 网页判定「这里是空白拖动区」后调用：
    /// ReleaseCapture() 释放鼠标捕获，再 SendMessage(WM_NCLBUTTONDOWN, HTCAPTION) 把事件重定向到非客户区，
    /// 交给系统的窗口拖动机制（含贴边、双击最大化）。
    /// </summary>
    public void BeginWindowDrag()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero) return;
            ReleaseCapture();
            SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[shell] 拖动失败: " + ex.Message);
        }
    }

    /// <summary>双击空白区：按 Presenter 状态在 Maximize / Restore 间切换。</summary>
    public void ToggleWindowMaximize() => HandleWindowCommand("toggleMaximize");

    /// <summary>
    /// 网页算好的「可拖动矩形」。
    /// 默认不注册成原生 caption 区域（按规格书走网页→原生消息），
    /// 但保留开关 settings.NativeCaptionRegions 以便对比手感。
    /// </summary>
    public void ApplyTitleBarRegions(JsonObject payload)
    {
        _webCaptionRects.Clear();

        if (_settings.Current.NativeCaptionRegions)
        {
            var scale = GetScale();
            if (payload["caption"] is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is not JsonObject o) continue;
                    var x = Num(o, "x");
                    var y = Num(o, "y");
                    var w = Num(o, "w");
                    var h = Num(o, "h");
                    if (w < 8 || h < 8) continue;
                    _webCaptionRects.Add(new RectInt32(
                        (int)Math.Round(x * scale),
                        (int)Math.Round(y * scale),
                        (int)Math.Round(w * scale),
                        (int)Math.Round(h * scale)));
                }
            }
        }

        ApplyCaptionRegions();
    }

    private static double Num(JsonObject o, string key)
    {
        try { return o[key]?.GetValue<double>() ?? 0; }
        catch { return 0; }
    }

    /// <summary>网页上报主题：原生跟着走（系统按钮颜色对齐）。</summary>
    public void OnWebThemeReported(string theme)
    {
        if (string.IsNullOrWhiteSpace(theme)) return;
        var normalized = theme.Trim().ToLowerInvariant();
        if (normalized is not ("light" or "dark")) return;

        _themeFromWeb = true;
        var elementTheme = normalized == "dark" ? ElementTheme.Dark : ElementTheme.Light;
        if (RootGrid.RequestedTheme != elementTheme)
        {
            RootGrid.RequestedTheme = elementTheme;
            if (_settings.Current.Backdrop == "solid")
                RootGrid.Background = new SolidColorBrush(FallbackSolidColor());
            else
                ApplyBackdrop(_settings.Current.Backdrop);
            UpdateCaptionButtonColors();
        }
    }

    private void SaveWindowState()
    {
        try
        {
            if (_appWindow is null) return;
            if (_appWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Maximized) return;
            var size = _appWindow.Size;
            _settings.Current.WindowWidth = size.Width;
            _settings.Current.WindowHeight = size.Height;
            _settings.Save();
        }
        catch { }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _loadTimer.Stop();
        _exitRequested = true;
        DownloadManager.Current.Finished -= OnDownloadFinished;
        try { _settings.Save(); } catch { }
        try { Web.Close(); } catch { }
        try { _tray?.Dispose(); } catch { }
        _tray = null;
        try { _trayTools?.Dispose(); } catch { }
        _trayTools = null;
    }

    private void LoadLoadingIcon()
    {
        try
        {
            var path = EmbeddedAssets.ExtractToCache("AppIcon-512.png", "AppIcon-512.png");
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            LoadingIcon.Source = new BitmapImage(new Uri(path));
        }
        catch { }
    }

    // ============================================================
    // caption 区域（原生侧，可选）
    //   网页上报的标题栏拖动区 → 注册成原生 caption 区域。
    //   默认关闭（拖动走「网页判断 + 桥接消息」的规格书路径），
    //   想对比手感可打开 settings.NativeCaptionRegions。
    // ============================================================

    private static RectInt32 ToPhysical(double x, double y, double w, double h, double scale) => new(
        (int)Math.Round(x * scale),
        (int)Math.Round(y * scale),
        (int)Math.Round(w * scale),
        (int)Math.Round(h * scale));

    private void ApplyCaptionRegions()
    {
        if (_appWindow is null) return;
        try
        {
            var source = InputNonClientPointerSource.GetForWindowId(_appWindow.Id);
            if (_webCaptionRects.Count > 0)
                source.SetRegionRects(NonClientRegionKind.Caption, _webCaptionRects.ToArray());
            else
                source.ClearRegionRects(NonClientRegionKind.Caption);
        }
        catch (Exception ex)
        {
            // 老系统不支持：拖动完全走网页端消息兜底
            Debug.WriteLine("[shell] caption 区域注册失败（改用网页端拖动）: " + ex.Message);
        }
    }

    // ============================================================
    // 外观：背景（亚克力 / Mica / 纯色）与主题
    // ============================================================

    public void SetBackdrop(string kind)
    {
        ApplyBackdrop(kind);
        _settings.Save();
    }

    /// <summary>
    /// 窗口背景：用 WinUI 自带的 SystemBackdrop（框架自己管 composition target，**不会静默失效**）。
    /// ⚠️ 不要改回手动 new MicaController/DesktopAcrylicController + AddSystemBackdropTarget ——
    /// 那套在启动早期挂载会静默失败（不报错但窗口死黑）。
    /// </summary>
    private void ApplyBackdrop(string kind)
    {
        kind = string.IsNullOrWhiteSpace(kind) ? "acrylic" : kind.Trim().ToLowerInvariant();
        var applied = false;

        try
        {
            SystemBackdrop = null;
            RootGrid.Background = null;

            if (kind == "mica")
            {
                if (MicaController.IsSupported())
                {
                    // MicaKind.Base = 普通云母（BaseAlt 在深色下会偏色发脏，别用）
                    SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
                    applied = true;
                }
            }
            else if (kind != "solid")
            {
                if (DesktopAcrylicController.IsSupported())
                {
                    SystemBackdrop = new DesktopAcrylicBackdrop();
                    applied = true;
                }
            }

            // 要的效果不支持 → 退到另一种，再不行纯色（保留用户选择，只上报实际生效值）
            if (!applied && kind != "solid")
            {
                if (MicaController.IsSupported())
                {
                    SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
                    kind = "mica";
                    applied = true;
                }
                else if (DesktopAcrylicController.IsSupported())
                {
                    SystemBackdrop = new DesktopAcrylicBackdrop();
                    kind = "acrylic";
                    applied = true;
                }
            }

            if (!applied)
            {
                SystemBackdrop = null;
                kind = "solid";
                RootGrid.Background = new SolidColorBrush(FallbackSolidColor());
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[shell] 背景设置失败，降级纯色: " + ex.Message);
            SystemBackdrop = null;
            kind = "solid";
            RootGrid.Background = new SolidColorBrush(FallbackSolidColor());
        }

        _settings.Current.Backdrop = kind;
        _bridge.Send("shell.backdropChanged", new { value = kind });
    }

    private Color FallbackSolidColor()
        => RootGrid.ActualTheme == ElementTheme.Dark
            ? Color.FromArgb(255, 32, 32, 32)
            : Color.FromArgb(255, 243, 243, 243);

    public void SetTheme(string theme)
    {
        ApplyTheme(theme, notifyWeb: true);
        _settings.Save();
    }

    private void ApplyTheme(string theme, bool notifyWeb = false)
    {
        theme = string.IsNullOrWhiteSpace(theme) ? "system" : theme.Trim().ToLowerInvariant();
        RootGrid.RequestedTheme = theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        _settings.Current.Theme = theme;
        Services.ThemeHost.Notify();                     // 侧边栏 / 浮窗 / 截图窗也跟着换（它们各自登记过）

        if (_settings.Current.Backdrop == "solid")
            RootGrid.Background = new SolidColorBrush(FallbackSolidColor());
        else
            ApplyBackdrop(_settings.Current.Backdrop);   // 主题变了 → tint 颜色跟着重算

        UpdateCaptionButtonColors();
        Services.ThemeBrush.Probe(RootGrid, "MainWindow.ApplyTheme(" + theme + ")");
        _bridge.Send("shell.themeChanged", new { value = theme, actual = RootGrid.ActualTheme.ToString() });

        if (notifyWeb)
            _bridge.Send("shell.applyWebTheme", new { value = theme });
    }

    public void SetWebTransparent(bool transparent)
    {
        _settings.Current.WebTransparent = transparent;
        _bridge.Send("shell.webTransparent", new { value = transparent });
        _settings.Save();
    }

    public void SetTitleBarColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return;
        try { _settings.Current.TitleBarTint = hex.Trim(); } catch { }
    }

    public void SetWebZoom(double zoom)
    {
        _settings.Current.WebZoom = Math.Clamp(zoom, 0.5, 3.0);
        _ = ExecuteScriptAsync(
            $"document.documentElement.style.zoom = '{_settings.Current.WebZoom.ToString(System.Globalization.CultureInfo.InvariantCulture)}';");
        _settings.Save();
    }

    // ============================================================
    // 启动 / WebView2 初始化
    // ============================================================

    private Task BootAsync()
    {
        // 原生界面完全不依赖 WebView2 —— 所以缺了也照常进主界面。
        // 只有应用内网页浮层（WebSheet）需要它，那边会给「去安装 / 用浏览器打开」的兜底。
        _runtime.Probe();
        RuntimePanel.Visibility = Visibility.Collapsed;

        // 记一笔"这台电脑运行过的版本"（更新页的「历史版本」读的就是这份记录）
        Services.VersionHistory.Record();

        // 原生界面优先：启动时不加载任何网页，所以这里不再初始化 WebView2。
        // 需要网页的地方只有应用内网页浮层（MainWindow 的 WebSheet），
        // 它自己按需创建 WebView2 —— 启动更快，也少两个浏览器进程。
        return Task.CompletedTask;
    }

    /// <summary>WebView2 装配：注入外壳脚本、拒绝权限、文档请求禁缓存、桥接消息。</summary>
    private void WireWebView(WebView2 web)
    {
        var core = web.CoreWebView2;
        if (core is null) return;

        web.DefaultBackgroundColor = Colors.Transparent;

        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = true;          // 开发期方便调试
        core.Settings.IsSwipeNavigationEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;

        var shim = EmbeddedAssets.ReadText("shell-shim.js");
        if (!string.IsNullOrEmpty(shim))
            _ = core.AddScriptToExecuteOnDocumentCreatedAsync(shim);

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Document);
        core.WebResourceRequested += (_, e) =>
        {
            try
            {
                e.Request.Headers.SetHeader("Cache-Control", "no-cache, no-store, must-revalidate");
                e.Request.Headers.SetHeader("Pragma", "no-cache");
            }
            catch { }
        };

        core.PermissionRequested += (_, e) =>
        {
            e.State = CoreWebView2PermissionState.Deny;
            e.Handled = true;
        };

        core.NewWindowRequested += OnNewWindowRequested;

        core.NavigationStarting += (_, _) => _navigatedOk = false;

        core.NavigationCompleted += (_, e) =>
        {
            _loadTimer.Stop();
            if (!e.IsSuccess)
            {
                ShowNetworkError(DescribeWebError(e.WebErrorStatus));
                return;
            }

            _navigatedOk = true;
            OnMainNavigationCompleted(core);
        };

        core.DocumentTitleChanged += (_, _) =>
        {
            try
            {
                var t = core.DocumentTitle;
                if (!string.IsNullOrWhiteSpace(t))
                    Title = t + " — " + ShellConfig.AppName;
            }
            catch { }
        };

        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
                ShowNetworkError("网页进程已退出");
            else
                ShowNetworkError("网页进程异常（" + e.ProcessFailedKind + "）");
        };

        core.WebMessageReceived += (_, e) =>
        {
            // 网页发的是 JSON 对象 → 用 WebMessageAsJson；万一发字符串再退回 TryGetWebMessageAsString
            string? json = null;
            try { json = e.WebMessageAsJson; }
            catch
            {
                try { json = e.TryGetWebMessageAsString(); }
                catch { }
            }
            if (string.IsNullOrEmpty(json)) return;

            _bridge.HandleWebMessage(json);
        };
    }

    /// <summary>新窗口请求（target=_blank 等）→ 交系统默认浏览器。</summary>
    /// <remarks>应用内标签页方案暂缓（Nick 觉得样式不够好，待重新设计后再启用）。</remarks>
    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternal(e.Uri);
    }

    private void NavigateToSite() => NavigateTo(ShellConfig.SiteUrl);

    /// <summary>打开指定网页（现在只有应用内网页浮层 WebSheet 会用到）。</summary>
    private void NavigateTo(string url)
    {
        if (!_webReady || Web.CoreWebView2 is null) return;
        _navigatedOk = false;
        ErrorPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Visible;
        LoadingRing.IsActive = true;
        LoadingText.Text = "正在加载…";
        try
        {
            Web.CoreWebView2.Navigate(url);
            _loadTimer.Start();
        }
        catch (Exception ex)
        {
            ShowNetworkError(ex.Message);
        }
    }

    private void OnMainNavigationCompleted(CoreWebView2 core)
    {
        _loadTimer.Stop();
        ErrorPanel.Visibility = Visibility.Collapsed;

        _bridge.Send("shell.webTransparent", new { value = _settings.Current.WebTransparent });
        SendCaptionInsets();
        FadeInWeb();

        _bridge.Send("shell.navigated", new { url = core.Source, title = core.DocumentTitle });
    }

    /// <summary>加载完成：淡入网页，淡出加载层（避免白屏闪烁）。</summary>
    private void FadeInWeb()
    {
        try
        {
            var da = new DoubleAnimation
            {
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(260)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(da, Web);
            Storyboard.SetTargetProperty(da, "Opacity");
            var sb = new Storyboard();
            sb.Children.Add(da);
            sb.Begin();

            var fadeOut = new DoubleAnimation
            {
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(180)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fadeOut, LoadingPanel);
            Storyboard.SetTargetProperty(fadeOut, "Opacity");
            var sb2 = new Storyboard();
            sb2.Children.Add(fadeOut);
            sb2.Completed += (_, _) =>
            {
                LoadingRing.IsActive = false;
                LoadingPanel.Visibility = Visibility.Collapsed;
                LoadingPanel.Opacity = 1;
            };
            sb2.Begin();
        }
        catch
        {
            Web.Opacity = 1;
            LoadingRing.IsActive = false;
            LoadingPanel.Visibility = Visibility.Collapsed;
        }
    }

    private static string DescribeWebError(CoreWebView2WebErrorStatus status) => status switch
    {
        CoreWebView2WebErrorStatus.HostNameNotResolved => "无法解析域名（可能是断网或 DNS 问题）",
        CoreWebView2WebErrorStatus.ServerUnreachable => "无法连接到服务器",
        CoreWebView2WebErrorStatus.Timeout => "连接超时",
        CoreWebView2WebErrorStatus.ConnectionAborted => "连接被中断",
        CoreWebView2WebErrorStatus.ConnectionReset => "连接被重置",
        CoreWebView2WebErrorStatus.Disconnected => "网络已断开",
        CoreWebView2WebErrorStatus.CannotConnect => "无法建立连接",
        _ => status.ToString()
    };

    private void ShowNetworkError(string? tip = null)
    {
        _loadTimer.Stop();
        LoadingRing.IsActive = false;
        LoadingPanel.Visibility = Visibility.Collapsed;
        ErrorTitle.Text = ShellConfig.NetworkErrorMessage;
        ErrorTip.Text = tip ?? string.Empty;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    // ============================================================
    // WebView2 缺失：弹窗三选项
    // ============================================================

    private void HandleMissingRuntime()
    {
        var choice = _settings.Current.WebView2MissingChoice;
        if (choice == "browser")
        {
            OpenExternal(ShellConfig.SiteUrl);
            ExitApp();
            return;
        }

        if (choice == "install")
            OpenExternal(ShellConfig.WebView2DownloadUrl);

        ErrorPanel.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = false;
        LoadingPanel.Visibility = Visibility.Collapsed;
        RuntimePanel.Visibility = Visibility.Visible;
        if (NoPromptCheck is not null && choice.Length > 0)
            NoPromptCheck.IsChecked = true;
    }

    private void InstallRuntime_Click(object sender, RoutedEventArgs e)
    {
        if (NoPromptCheck?.IsChecked == true) _settings.Current.WebView2MissingChoice = "install";
        _settings.Save();
        OpenExternal(ShellConfig.WebView2DownloadUrl);
    }

    private void OpenInBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (NoPromptCheck?.IsChecked == true) _settings.Current.WebView2MissingChoice = "browser";
        _settings.Save();
        OpenExternal(ShellConfig.SiteUrl);
        ExitApp();
    }

    private void ExitApp_Click(object sender, RoutedEventArgs e) => ExitApp();

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_webReady)
        {
            _ = BootAsync();
            return;
        }
        NavigateToSite();
    }

    // ============================================================
    // 桥接动作
    // ============================================================

    public void HandleWindowCommand(string action)
    {
        if (_appWindow?.Presenter is not OverlappedPresenter presenter) return;
        switch (action)
        {
            case "minimize":
                presenter.Minimize();
                break;
            case "maximize":
                presenter.Maximize();
                break;
            case "restore":
                presenter.Restore();
                break;
            case "toggleMaximize":
                if (presenter.State == OverlappedPresenterState.Maximized) presenter.Restore();
                else presenter.Maximize();
                break;
            case "close":
                Close();
                break;
        }
        _bridge.Send("shell.state", BuildStatePayload());
    }

    public void SetAlwaysOnTop(bool value)
    {
        _settings.Current.AlwaysOnTop = value;
        if (_appWindow?.Presenter is OverlappedPresenter p) p.IsAlwaysOnTop = value;
        _settings.Save();
        _bridge.Send("shell.state", BuildStatePayload());
    }

    public void SetTelemetryEnabled(bool value)
    {
        _settings.Current.TelemetryEnabled = value;
        _settings.Current.TelemetryAsked = true;
        _settings.Save();
    }

    public void SetAutoStart(bool value)
    {
        _settings.Current.AutoStart = value;
        ApplyAutoStart();
        _settings.Save();
        _bridge.Send("shell.state", BuildStatePayload());
    }

    /// <summary>开机最小化启动（依赖开机自启：不打开自启时不会被用到）。</summary>
    public void SetMinimizeOnStart(bool value)
    {
        _settings.Current.MinimizeOnStart = value;
        ApplyAutoStart();
        _settings.Save();
        _bridge.Send("shell.state", BuildStatePayload());
    }

    /// <summary>点关闭时收进托盘（true）/ 直接退出（false）。</summary>
    public void SetCloseToTray(bool value)
    {
        _settings.Current.CloseToTray = value;
        _settings.Save();
    }

    /// <summary>工具浮窗是否始终置顶。</summary>
    public void SetPaletteOnTop(bool value)
    {
        _settings.Current.PaletteOnTop = value;
        _settings.Save();
        Views.ToolPaletteWindow.ApplyOnTopSetting();
    }

    // ============================================================
    // 托盘图标：主窗口藏起来、工具浮窗、退出都从这儿走
    // ============================================================

    private void InitTray()
    {
        try
        {
            // 托盘图标一：主界面（左键 = 主窗口显示/收起）
            _tray = new TrayIcon(IconPath());
            _tray.LeftClick += ToggleMainWindow;
            _tray.CommandInvoked += OnTrayCommand;
            _tray.BalloonClicked += OnBalloonClicked;
            _tray.MenuItems.Add(new TrayMenuItem { Text = "打开主界面", Command = "show", IsDefault = true });
            _tray.MenuItems.Add(new TrayMenuItem { Text = "常用工具", Command = "palette" });
            _tray.MenuItems.Add(new TrayMenuItem { Text = "工具侧边栏", Command = "sidebar" });
            _tray.MenuItems.Add(new TrayMenuItem { Separator = true });
            _tray.MenuItems.Add(new TrayMenuItem { Text = "检查更新", Command = "update" });
            _tray.MenuItems.Add(new TrayMenuItem { Separator = true });
            _tray.MenuItems.Add(new TrayMenuItem { Text = "退出", Command = "exit" });

            if (!_tray.Setup(ShellConfig.WindowTitle))
                Debug.WriteLine("[tray] 托盘图标没挂上（挂不上时关闭窗口仍然直接退出）");

            // 托盘图标二：常用工具（左键 = 直接开工具窗口，不用先进主界面）—— 学校电脑是触屏，少点几下
            _trayTools = new TrayIcon(ToolIconPath());
            _trayTools.LeftClick += () => Views.ToolPaletteWindow.TogglePalette();
            _trayTools.CommandInvoked += OnTrayCommand;
            _trayTools.MenuItems.Add(new TrayMenuItem { Text = "打开常用工具", Command = "palette", IsDefault = true });
            _trayTools.MenuItems.Add(new TrayMenuItem { Text = "打开主界面", Command = "show" });
            _trayTools.MenuItems.Add(new TrayMenuItem { Separator = true });
            _trayTools.MenuItems.Add(new TrayMenuItem { Text = "退出", Command = "exit" });

            if (!_trayTools.Setup("常用工具"))
                Debug.WriteLine("[tray] 第二个托盘图标没挂上");

            // 屏幕右边那条工具侧边栏（全屏放 PPT 时也够得着工具）
            Views.ToolSidebarWindow.ApplySetting();

            // 记着"上一次在用的窗口"（点了侧边栏之后前台就变成我们自己了，"关前台应用"得知道原本是谁）
            Services.TeachingActions.StartFocusWatcher();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[tray] 初始化失败: " + ex.Message);
        }
    }

    /// <summary>通知被点开：按"这条通知是关于什么的"分流（更新 / 下载完成 / 下载失败）。</summary>
    private void OnBalloonClicked()
    {
        var action = _balloonAction;
        _balloonAction = null;

        if (action is not null)
        {
            try { action(); } catch { }
            return;
        }

        // 没登记动作的（老路子）：把界面叫出来 + 顺手查一次更新
        ShowFromTray();
        _ = CheckUpdateManualAsync();
    }

    private void OnTrayCommand(string command)
    {
        switch (command)
        {
            case "show":
                ShowFromTray();
                break;
            case "palette":
                Views.ToolPaletteWindow.ShowTool();
                break;
            case "sidebar":
                Views.ToolSidebarWindow.ShowSidebar();
                break;
            case "update":
                ShowFromTray();
                _ = CheckUpdateManualAsync();
                break;
            case "exit":
                ExitApp();
                break;
        }
    }

    /// <summary>左键点托盘图标：主窗口显示/收起来回切。</summary>
    public void ToggleMainWindow()
    {
        try
        {
            if (IsWindowVisible(WindowNative.GetWindowHandle(this))) HideToTray();
            else ShowFromTray();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[tray] 切换窗口失败: " + ex.Message);
        }
    }

    /// <summary>主窗口藏进托盘（任务栏上不留最小化按钮）。</summary>
    public void HideToTray()
    {
        try { ShowWindow(WindowNative.GetWindowHandle(this), SW_HIDE); } catch { }
        // 藏起来没人看的时候把内存还给系统（教学机 8G，不能白白占着）
        Services.MemoryTrimmer.TrimLater(1200);
    }

    /// <summary>从托盘把主窗口叫回来。</summary>
    public void ShowFromTray()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            ShowWindow(hwnd, SW_SHOW);
            if (_appWindow?.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized)
                p.Restore();
            Activate();
            SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[tray] 显示窗口失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 工具浮窗里的「详细设置」：把主界面叫出来，并直接跳到对应的工具页
    /// （浮窗太小，放不下那些自定义项 —— 但用户想细调时得有条路进去）。
    /// </summary>
    public void OpenToolSettings(Type pageType)
    {
        try
        {
            ShowFromTray();
            Shell.NavigateToTool(pageType);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[palette] 打开工具设置失败: " + ex.Message);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    /// <summary>托盘菜单「检查更新」：查到新版就走更新流程，没有就明确说一声。</summary>
    private async Task CheckUpdateManualAsync()
    {
        try
        {
            var service = Services.Updating.UpdateService.CreateDefault();
            if (!service.Source.IsConfigured) return;

            var channel = Services.Updating.UpdateChannels.Parse(_settings.Current.UpdateChannel);
            var result = await service.CheckAsync(channel, ShellConfig.ShellVersion);
            var root = (Content as FrameworkElement)?.XamlRoot;
            if (root is null) return;

            if (result is { HasUpdate: true, Release: { } release })
            {
                // 一样先问，绝不替用户做主
                if (!await Services.Updating.UpdateFlow.AskAsync(root, release)) return;
                await Services.Updating.UpdateFlow.RunAsync(root, service, release);
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = "已经是最新版本",
                Content = $"当前：dv{ShellConfig.ShellVersion}\n通道：{Services.Updating.UpdateChannels.ToDisplay(channel)}",
                CloseButtonText = "好",
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[update] 检查更新失败: " + ex.Message);
        }
    }

    /// <summary>把自启项写进注册表 Run；开着「开机最小化」时附带 --minimized 参数。</summary>
    private void ApplyAutoStart()
    {
        try
        {
            const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            const string valueName = "ClassSoftwareHubDesktop";
            using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            if (key is null) return;

            if (_settings.Current.AutoStart)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                var cmd = _settings.Current.MinimizeOnStart ? $"\"{exe}\" --minimized" : $"\"{exe}\"";
                key.SetValue(valueName, cmd);
            }
            else if (key.GetValue(valueName) is not null)
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[shell] 开机自启设置失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 把链接交给系统浏览器处理（只有「必须用外部程序」的场景才该走这里）。
    ///
    /// ⚠️ 必须限协议。这里最终是 `Process.Start(UseShellExecute = true)`，等于把 URL 直接交给
    /// shell 解析 —— 放行任意协议的话，一个 `file:///C:/Windows/...` 或者攻击者自定义注册的协议
    /// 就能拉起本机程序。而 URL 的来源（软件条目的 website、网页浮层里的链接）并不都是我们自己写的，
    /// 所以这里只留真正需要的三个，其余一律拒绝并记日志。
    /// </summary>
    public void OpenExternal(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;

        if (!AllowedExternalSchemes.Contains(uri.Scheme))
        {
            Debug.WriteLine($"[shell] 拒绝打开非白名单协议的外部链接: {uri.Scheme}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[shell] 打开外部链接失败: " + ex.Message);
        }
    }

    /// <summary>允许交给系统的协议：网页、微软商店。其余（file / 自定义协议等）不放行。</summary>
    private static readonly HashSet<string> AllowedExternalSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http",
        "https",
        "ms-windows-store",
    };

    /// <summary>
    /// 打开 Microsoft Store 链接：先转成商店协议拉起身上的「微软商店」应用；
    /// 转不出来（不是商店链接）就当普通网页开在浮层里；商店应用不在再退回浏览器。
    /// </summary>
    public void OpenStore(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            OpenExternal(url);
            return;
        }

        var storeUri = Core.StoreLink.ToStoreUri(url);
        if (storeUri is null)
        {
            ShowWebSheet(url);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(storeUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[shell] 打开商店失败，改开网页: " + ex.Message);
            OpenExternal(url);
        }
    }

    /// <summary>
    /// 用系统的默认关联打开一个本地文件（刚下载完的安装包之类）。
    /// ⚠️ 故意不加 shell 参数：安装包会按自己的清单弹 UAC，我们不该替它提权。
    /// </summary>
    public void OpenFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[shell] 打开文件失败: " + ex.Message);
        }
    }

    /// <summary>在资源管理器里定位到某个文件。</summary>
    public void RevealFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[shell] 定位文件失败: " + ex.Message);
        }
    }

    // ============================================================
    // 应用内网页浮层（官网 / 网页版）：由下往上淡入的「窗中窗」
    // （「提交软件」是原生页 Pages/SubmitPage；遗留的网页版小窗口 Views/SubmitWindow 没人调用）
    // ============================================================

    private bool _sheetReady;
    private string _sheetUrl = "";

    /// <summary>
    /// 在应用内浮层里打开一个网页（http/https）。缺 WebView2 时退化成「去安装 / 用浏览器打开」。
    /// </summary>
    public async void ShowWebSheet(string? url, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var href = url.Trim();

        if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            OpenExternal(href);   // ms-windows-store: 这类协议只能交给系统
            return;
        }

        if (!_runtime.IsAvailable && !_runtime.Probe())
        {
            await ShowRuntimeMissingDialogAsync(href);
            return;
        }

        _sheetUrl = href;
        if (!string.IsNullOrWhiteSpace(title))
        {
            WebSheetTitle.Text = title!;
        }
        else
        {
            try { WebSheetTitle.Text = new Uri(href).Host; } catch { WebSheetTitle.Text = "网页"; }
        }

        WebSheetRing.IsActive = true;
        WebSheet.Visibility = Visibility.Visible;
        PlaySheetAnimation(show: true);

        try
        {
            if (!_sheetReady)
            {
                var env = await _runtime.GetEnvironmentAsync();
                await SheetWeb.EnsureCoreWebView2Async(env);

                var core = SheetWeb.CoreWebView2;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = true;
                core.NavigationStarting += (_, _) => WebSheetRing.IsActive = true;
                core.NavigationCompleted += (_, _) => WebSheetRing.IsActive = false;
                core.DocumentTitleChanged += (_, _) =>
                {
                    var t = core.DocumentTitle;
                    if (!string.IsNullOrWhiteSpace(t)) WebSheetTitle.Text = t;
                };
                core.NewWindowRequested += SheetNewWindowRequested;
                _sheetReady = true;
            }

            SheetWeb.CoreWebView2.Navigate(href);
        }
        catch (Exception ex)
        {
            WebSheetRing.IsActive = false;
            WebSheetTitle.Text = "网页打不开：" + ex.Message;
        }
    }

    /// <summary>浮层里点了需要新窗口的链接 → 能 http 就原地跳，其它协议交给系统。</summary>
    private void SheetNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        try
        {
            if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                sender.Navigate(e.Uri);
            else
                OpenExternal(e.Uri);
        }
        catch { }
    }

    /// <summary>显示/收起浮层：位移 + 透明度一起动（由下往上淡入）。</summary>
    private void PlaySheetAnimation(bool show)
    {
        var ease = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn };

        var fade = new DoubleAnimation
        {
            From = show ? 0 : 1,
            To = show ? 1 : 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(show ? 220 : 160)),
            EasingFunction = ease,
        };
        Storyboard.SetTarget(fade, WebSheetCard);
        Storyboard.SetTargetProperty(fade, "Opacity");

        // ⚠️ TranslateTransform.Y 属于「依赖动画」→ 必须 EnableDependentAnimation
        var slide = new DoubleAnimation
        {
            From = show ? 150 : 0,
            To = show ? 0 : 150,
            Duration = new Duration(TimeSpan.FromMilliseconds(show ? 320 : 190)),
            EasingFunction = ease,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(slide, WebSheetShift);
        Storyboard.SetTargetProperty(slide, "Y");

        var story = new Storyboard();
        story.Children.Add(fade);
        story.Children.Add(slide);
        if (!show)
            story.Completed += (_, _) => WebSheet.Visibility = Visibility.Collapsed;

        story.Begin();
    }

    private void CloseWebSheet() => PlaySheetAnimation(show: false);

    private void WebSheetClose_Click(object sender, RoutedEventArgs e) => CloseWebSheet();

    private void WebSheetScrim_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => CloseWebSheet();

    private void WebSheetExternal_Click(object sender, RoutedEventArgs e)
    {
        var url = _sheetUrl;
        try { url = SheetWeb?.CoreWebView2?.Source ?? url; } catch { }
        OpenExternal(url);
    }

    // ============================================================
    // 原生下载（不跳浏览器）
    // ============================================================

    /// <summary>
    /// 原生下载一个文件。
    ///
    /// ⚠️ 下载本身跑在 <see cref="DownloadManager"/> 里，**跟这个弹窗解绑**：
    ///   · 点「看看别的」把弹窗收掉，下载在后台接着跑（以前收掉就等于取消，半个 G 的包只能干等）；
    ///   · 想反悔就按「取消下载」，或者去左侧「任务进行」页取消；
    ///   · 下完/失败会弹系统通知，通知点开直接跳到「任务进行」。
    /// 弹窗还开着的时候跑完了，更省事：直接在这儿弹「下载完成」。
    /// </summary>
    public async void DownloadFile(string? url, string? suggestedName = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        var task = DownloadManager.Current.Start(url, suggestedName, suggestedName);
        _downloadDialogTaskId = task.Id;   // 这条由本方法负责收尾，别让系统通知重复报一次

        var root = (Content as FrameworkElement)?.XamlRoot;
        if (root is null)
        {
            _downloadDialogTaskId = null;   // 弹不了框：任务照跑，收尾交给系统通知
            return;
        }

        // 同一时刻只允许一个 ContentDialog：上一条下载的弹窗先收掉（它自己的下载转后台继续）
        if (_downloadDialog is { } previous)
        {
            try { previous.Hide(); } catch { }
            await Task.Yield();
        }

        var bar = new ProgressBar { Minimum = 0, Maximum = 100, IsIndeterminate = true };
        var status = new TextBlock { Text = "正在连接…", FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        var hint = new TextBlock
        {
            Text = $"保存到：{DownloadService.DefaultDir}\n急着用别的就先点「看看别的」，下载会转到后台继续。",
            FontSize = 11.5,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        };
        var panel = new StackPanel { Spacing = 10, MinWidth = 340 };
        panel.Children.Add(bar);
        panel.Children.Add(status);
        panel.Children.Add(hint);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "下载 " + task.Title,
            Content = panel,
            PrimaryButtonText = "看看别的",     // 只是把弹窗收掉，下载继续
            CloseButtonText = "取消下载",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.CloseButtonClick += (_, _) => DownloadManager.Current.Cancel(task.Id);

        void Sync()
        {
            bar.IsIndeterminate = task.BarIndeterminate;
            bar.Value = task.BarValue;
            status.Text = task.StatusText;
        }

        void OnTaskChanged(object? _, PropertyChangedEventArgs __)
        {
            Sync();
            // 跑完了（成功/失败/取消）→ 把进度框收掉，下面接着弹结果
            if (task.IsFinished)
            {
                try { dialog.Hide(); } catch { }
            }
        }

        task.PropertyChanged += OnTaskChanged;
        Sync();

        if (task.IsRunning)
        {
            _downloadDialog = dialog;
            try { await dialog.ShowAsync(); }
            catch { }
            finally
            {
                task.PropertyChanged -= OnTaskChanged;
                if (ReferenceEquals(_downloadDialog, dialog)) _downloadDialog = null;
            }
        }
        else
        {
            // 极端情况：还没来得及弹就被下完了（只在"上一张弹窗还停着"那种空档里可能发生）
            task.PropertyChanged -= OnTaskChanged;
        }

        // 从这儿往后是**收尾**：完成 / 失败由下面自己弹，不再叠一条系统通知
        _downloadDialogTaskId = null;

        // 「看看别的」→ 任务还在跑：放手让它下，收尾交给系统通知
        if (task.IsRunning) return;
        if (task.State == DownloadState.Canceled) return;    // 自己取消的，不用报错

        if (task.State == DownloadState.Failed)
        {
            await new ContentDialog
            {
                XamlRoot = root,
                Title = "下载失败",
                Content = new TextBlock
                {
                    Text = $"{task.Title}\n\n{task.Error}\n\n可以在左侧「任务进行」里点「重试」。",
                    TextWrapping = TextWrapping.Wrap,
                },
                CloseButtonText = "知道了",
            }.ShowAsync();
            return;
        }

        var done = new ContentDialog
        {
            XamlRoot = root,
            Title = "下载完成",
            Content = new TextBlock
            {
                Text = $"{task.FileName}\n{DownloadProgress.Size(task.Bytes)}\n\n{task.Path}",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "立即打开",
            SecondaryButtonText = "打开所在文件夹",
            CloseButtonText = "完成",
            DefaultButton = ContentDialogButton.Primary,
        };

        switch (await done.ShowAsync())
        {
            case ContentDialogResult.Primary:
                OpenFile(task.Path);
                break;
            case ContentDialogResult.Secondary:
                RevealFile(task.Path);
                break;
        }
    }

    /// <summary>缺 WebView2 时的统一提示（官网浮层、提交软件都用它）。</summary>
    private async Task ShowRuntimeMissingDialogAsync(string? fallbackUrl = null)
    {
        var root = (Content as FrameworkElement)?.XamlRoot;
        var target = string.IsNullOrWhiteSpace(fallbackUrl) ? ShellConfig.SiteUrl : fallbackUrl!;

        if (root is null)
        {
            OpenExternal(target);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "缺少 WebView2 运行时",
            Content = new TextBlock
            {
                Text = "这个页面需要系统里的 WebView2 运行时（微软 Edge 内核组件，免费）。\n\n" +
                       "可以现在去装；也可以直接用浏览器打开——内容是一样的。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "去安装 WebView2",
            SecondaryButtonText = "用浏览器打开",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                OpenExternal(ShellConfig.WebView2DownloadUrl);
                break;
            case ContentDialogResult.Secondary:
                OpenExternal(target);
                break;
        }
    }

    private void ExitApp()
    {
        _exitRequested = true;
        try { _settings.Save(); } catch { }
        try { _tray?.Dispose(); } catch { }
        _tray = null;
        try { _trayTools?.Dispose(); } catch { }
        _trayTools = null;
        try { Application.Current.Exit(); }
        catch { Environment.Exit(0); }
    }

    private async Task ExecuteScriptAsync(string script)
    {
        try
        {
            if (_webReady && Web.CoreWebView2 is not null)
                await Web.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch { }
    }

    // ============================================================
    // 给网页的信息
    // ============================================================

    public object BuildInfoPayload() => new
    {
        appName = ShellConfig.AppName,
        appVersion = ShellConfig.ShellVersion,
        shell = "winui3",
        shellVersion = ShellConfig.ShellVersion,
        siteVersionTarget = ShellConfig.SiteVersionTarget,
        os = Environment.OSVersion.VersionString,
        osBuild = Environment.OSVersion.Version.Build,
        isWin11 = Environment.OSVersion.Version.Build >= 22000,
        webView2 = _runtime.Version ?? string.Empty,
        theme = _settings.Current.Theme,
        actualTheme = RootGrid.ActualTheme.ToString(),
        backdrop = _settings.Current.Backdrop,
        alwaysOnTop = _settings.Current.AlwaysOnTop,
        autoStart = _settings.Current.AutoStart,
        telemetryEnabled = _settings.Current.TelemetryEnabled,
        webTransparent = _settings.Current.WebTransparent,
        zoom = _settings.Current.WebZoom,
        titleBarHeight = ShellConfig.TitleBarHeight,
        themeFromWeb = _themeFromWeb,
        siteUrl = ShellConfig.SiteUrl
    };

    public object BuildStatePayload() => new
    {
        theme = _settings.Current.Theme,
        actualTheme = RootGrid.ActualTheme.ToString(),
        backdrop = _settings.Current.Backdrop,
        alwaysOnTop = _settings.Current.AlwaysOnTop,
        autoStart = _settings.Current.AutoStart,
        telemetryEnabled = _settings.Current.TelemetryEnabled,
        webTransparent = _settings.Current.WebTransparent,
        zoom = _settings.Current.WebZoom,
        isMaximized = _appWindow?.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Maximized,
        navigated = _navigatedOk,
    };
}
