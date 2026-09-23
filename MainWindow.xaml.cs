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
        Activated += OnFirstActivated;

        _ = BootAsync();
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_firstActivated) return;
        _firstActivated = true;
        Activated -= OnFirstActivated;
        if (_startMinimized && _appWindow?.Presenter is OverlappedPresenter p)
            p.Minimize();

        // 启动时静默查一次更新：有新版就**强制更新**（没有"稍后"）
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

            var root = (Content as FrameworkElement)?.XamlRoot;
            if (root is null) return;
            await Services.Updating.UpdateFlow.RunAsync(root, service, release);
        }
        catch
        {
            // 启动时查更新失败就安静放过，别影响正常使用
        }
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

    /// <summary>打开「提交软件」小窗口（全站唯一用网页版的地方）。缺 WebView2 时给替代方案。</summary>
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
        try { _settings.Save(); } catch { }
        try { Web.Close(); } catch { }
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

        if (_settings.Current.Backdrop == "solid")
            RootGrid.Background = new SolidColorBrush(FallbackSolidColor());
        else
            ApplyBackdrop(_settings.Current.Backdrop);   // 主题变了 → tint 颜色跟着重算

        UpdateCaptionButtonColors();
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
        // 只有「提交软件」那个小窗口需要它，那边 OpenSubmitWindow() 会给「去安装 / 用浏览器打开」的兜底。
        _runtime.Probe();
        RuntimePanel.Visibility = Visibility.Collapsed;

        // 记一笔"这台电脑运行过的版本"（更新页的「历史版本」读的就是这份记录）
        Services.VersionHistory.Record();

        // 原生界面优先：启动时不加载任何网页，所以这里不再初始化 WebView2。
        // 需要网页的地方只有「提交软件」小窗口（Windows\SubmitWindow.xaml.cs），
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

    /// <summary>打开指定网页（原生界面下只有「提交软件」页会用到）。</summary>
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

    /// <summary>把链接交给系统浏览器处理（只有「必须用外部程序」的场景才该走这里）。</summary>
    public void OpenExternal(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[shell] 打开外部链接失败: " + ex.Message);
        }
    }

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
    // 提交软件仍然走独立小窗口（Views\SubmitWindow），不受这里影响
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
    /// 原生下载一个文件：进度对话框 → 完成后可「立即打开 / 打开所在文件夹」。
    /// 下载中允许取消（下载是可以取消的，强制只用在更新上）。
    /// </summary>
    public async void DownloadFile(string? url, string? suggestedName = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        var root = (Content as FrameworkElement)?.XamlRoot;
        if (root is null)
        {
            OpenExternal(url);   // 没有 XamlRoot 就没法弹框，退回老办法
            return;
        }

        var fileName = DownloadService.ResolveFileName(url, suggestedName);

        var bar = new ProgressBar { Minimum = 0, Maximum = 100, IsIndeterminate = true };
        var status = new TextBlock { Text = "正在连接…", FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        var hint = new TextBlock
        {
            Text = $"保存到：{DownloadService.DefaultDir}",
            FontSize = 11.5,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        };
        var panel = new StackPanel { Spacing = 10, MinWidth = 340 };
        panel.Children.Add(bar);
        panel.Children.Add(status);
        panel.Children.Add(hint);

        using var cts = new CancellationTokenSource();
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "下载 " + fileName,
            Content = panel,
            CloseButtonText = "取消",
        };
        dialog.CloseButtonClick += (_, _) => cts.Cancel();
        _ = dialog.ShowAsync();     // 不 await：我们自己控制什么时候收掉

        var progress = new Progress<DownloadProgress>(p =>
        {
            bar.IsIndeterminate = p.Indeterminate;
            if (p.Indeterminate)
            {
                status.Text = "已接收 " + p.SizeText + (p.SpeedText.Length > 0 ? "  ·  " + p.SpeedText : "");
            }
            else
            {
                bar.Value = p.Percent;
                status.Text = $"{p.Percent:0}%   {p.SizeText}" + (p.SpeedText.Length > 0 ? "  ·  " + p.SpeedText : "");
            }
        });

        DownloadedFile? file = null;
        string? error = null;
        try
        {
            file = await DownloadService.DownloadAsync(url, suggestedName, null, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 用户自己取消的，不用报错
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            try { dialog.Hide(); } catch { }
        }

        if (error is not null)
        {
            await new ContentDialog
            {
                XamlRoot = root,
                Title = "下载失败",
                Content = new TextBlock { Text = error, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "知道了",
            }.ShowAsync();
            return;
        }

        if (file is null) return;   // 取消了

        var done = new ContentDialog
        {
            XamlRoot = root,
            Title = "下载完成",
            Content = new TextBlock
            {
                Text = $"{file.FileName}\n{file.SizeText}\n\n{file.Path}",
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
                OpenFile(file.Path);
                break;
            case ContentDialogResult.Secondary:
                RevealFile(file.Path);
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
        try { _settings.Save(); } catch { }
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
