using System.ComponentModel;
using System.Diagnostics;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Views;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
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
    private readonly WebViewRuntimeService _runtime = App.WebRuntime;

    private AppWindow? _appWindow;
    private bool _firstActivated;
    private readonly bool _startMinimized;
    private readonly bool _startPalette;

    
    
    
    
    
    
    
    
    
    private readonly string? _startWindow;

    
    private bool _shellPending;

    private TrayIcon? _tray;
    private TrayIcon? _trayTools;      

    
    
    
    
    private HotkeyService? _hotkeys;

    private bool _exitRequested;

    
    private Action? _balloonAction;

    
    private ContentDialog? _downloadDialog;
    private string? _downloadDialogTaskId;

    public MainWindow()
    {
        InitializeComponent();

        Title = ShellConfig.WindowTitle;

        ConfigureWindow();
        ConfigureTitleBar();
        ApplyTheme(_settings.Current.Theme);
        ApplyBackdrop();

        Closed += OnWindowClosed;

        
        InitTray();

        
        
        Services.License.ClientAuth.ActivationRequired += OnActivationRequired;

        
        DownloadManager.Current.Finished += OnDownloadFinished;

        
        
        
        App.Content.Load();

        
        
        
        if (App.ShellDeferredForOobe)
        {
            _shellPending = true;
        }
        else
        {
            NativeShell.Init();
        }

        NativeShell.SetIcon(LoadAppIcon());
        SetTitleBar(NativeShell.TitleBarElement);

        
        _ = SyncContentAsync();

        
        _startMinimized = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        
        _startPalette = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--palette", StringComparison.OrdinalIgnoreCase));

        
        _startWindow = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--window=", StringComparison.OrdinalIgnoreCase))
            ?["--window=".Length..]
            ?.Trim();
        Activated += OnFirstActivated;

        _ = BootAsync();

        
        
        
        
        
        Core.AppLog.Info("startup", "主窗口构造完成");
    }

    
    
    
    
    public void StartShell()
    {
        if (!_shellPending) return;
        _shellPending = false;
        NativeShell.Init();
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_firstActivated) return;
        _firstActivated = true;
        Activated -= OnFirstActivated;

        
        
        ApplyWindowRounding();

        
        
        StartToolSidebarAfterFirstFrame();

        if (_startPalette)
        {
            
            HideToTray();
            Views.ToolPaletteWindow.ShowTool();
        }
        else if (_startMinimized)
        {
            
            HideToTray();
        }

        
        if (_startWindow is { Length: > 0 } windowId) ShowSidebarWindowById(windowId);

        
        _ = CheckUpdateOnStartupAsync();

        
        
        
        
        _ = Services.LegalTextSource.RefreshAsync();

        
        
        var startupLink = App.TakeStartupLink();
        if (startupLink is { Length: > 0 }) _ = HandleAppUriAsync(startupLink);

        
        
    }

    
    
    
    
    
    
    
    
    
    private static void ShowSidebarWindowById(string id)
    {
        try
        {
            var type = Data.SidebarModules.Find(id)?.WindowType;
            if (type is null)
            {
                Core.AppLog.Warn("startup", $"--window={id} 没有对应的浮窗模块，忽略");
                return;
            }

            var show = type.GetMethod(
                "Show",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                binder: null, types: Type.EmptyTypes, modifiers: null);

            if (show is null)
            {
                Core.AppLog.Warn("startup", $"--window={id}：{type.Name} 没有 public static Show()");
                return;
            }

            show.Invoke(null, null);
            Core.AppLog.Step("startup", $"--window={id} 已打开 {type.Name}");
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn("startup", $"--window={id} 打开失败：" + ex.Message);
        }
    }

    
    
    
    
    
    
    
    
    
    
    
    private void StartToolSidebar()
    {
        try
        {
            Views.ToolSidebarWindow.ApplySetting();
            Core.AppLog.Info("startup", "工具侧边栏已挂出");
        }
        catch (Exception ex)
        {
            
            Core.AppLog.Info("startup", "工具侧边栏挂出失败: " + ex.Message);
        }
    }

    
    
    
    
    
    
    
    
    
    private static async System.Threading.Tasks.Task PrewarmWebRuntimeAsync()
    {
        try
        {
            if (!WebViewRuntimeService.IsRuntimeInstalled()) return;

            await App.WebRuntime.GetEnvironmentAsync();
            Core.AppLog.Step("startup", "WebView2 环境已预热");
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("startup", ex, "预热 WebView2");
        }
    }

    
    
    
    
    
    
    
    
    
    
    
    
    private void StartToolSidebarAfterFirstFrame()
    {
        var done = false;
        EventHandler<object>? onFrame = null;
        Microsoft.UI.Dispatching.DispatcherQueueTimer? guard = null;

        void Fire(string how)
        {
            if (done) return;
            done = true;

            if (onFrame is not null)
            {
                try { Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= onFrame; } catch { }
            }
            if (guard is not null)
            {
                try { guard.Stop(); } catch { }
            }

            Core.AppLog.Info("startup", $"主窗口首帧就绪（{how}）→ 挂工具侧边栏");
            StartToolSidebar();

            
            
            
            _ = PrewarmWebRuntimeAsync();
        }

        onFrame = (_, _) => Fire("首帧");
        try
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += onFrame;
        }
        catch
        {
            Fire("帧回调挂不上");       
            return;
        }

        try
        {
            var queue = DispatcherQueue;
            if (queue is not null)
            {
                guard = queue.CreateTimer();
                guard.Interval = TimeSpan.FromMilliseconds(2000);
                guard.IsRepeating = false;
                guard.Tick += (_, _) => Fire("超时兜底");
                guard.Start();
            }
        }
        catch { }
    }

    
    
    
    
    
    public bool IsVisibleNow()
    {
        try { return IsWindowVisible(WindowNative.GetWindowHandle(this)); }
        catch { return false; }
    }

    
    
    
    
    
    
    
    
    
    
    
    public void ArmStartupVisibleWatchdog()
    {
        try
        {
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(800);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                try
                {
                    if (IsVisibleNow()) return;

                    Core.AppLog.Info("startup", "主窗口 Activate 之后仍不可见，强制再显示一次");
                    ShowFromTray();
                    Core.AppLog.Info("startup", "可见性兜底执行完毕，当前可见=" + IsVisibleNow());
                }
                catch (Exception ex)
                {
                    Core.AppLog.Info("startup", "可见性兜底执行失败: " + ex.Message);
                }
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            Core.AppLog.Info("startup", "可见性兜底装不上: " + ex.Message);
        }
    }

    private async Task CheckUpdateOnStartupAsync()    {
        try
        {
            if (!_settings.Current.AutoCheckUpdate)
            {
                Core.AppLog.Debug("update", "跳过启动检查：设置里关掉了「自动检查更新」");
                return;
            }

            var service = Services.Updating.UpdateService.CreateDefault();
            if (!service.Source.IsConfigured)
            {
                Core.AppLog.Debug("update", "跳过启动检查：更新源未配置");
                return;
            }

            var channel = Services.Updating.UpdateChannels.Parse(_settings.Current.UpdateChannel);
            var result = await service.CheckAsync(channel, ShellConfig.ShellVersion);

            
            if (result is not { HasUpdate: true, Release: { } release })
            {
                Services.Updating.UpdateNotice.Clear();
                return;
            }

            
            
            Services.Updating.UpdateNotice.Set(release);

            
            
            if (!IsWindowVisible(WindowNative.GetWindowHandle(this)))
            {
                NotifyUpdateAvailable(release);
                return;
            }

            var root = (Content as FrameworkElement)?.XamlRoot;
            if (root is null) return;

            
            var choice = await Services.Updating.UpdateFlow.AskAsync(root, release);
            if (choice == Services.Updating.UpdateFlow.UpdateChoice.Now)
                await Services.Updating.UpdateFlow.RunAsync(root, service, release);
            else if (choice == Services.Updating.UpdateFlow.UpdateChoice.Background)
                Services.Updating.UpdateFlow.StartBackgroundDownload(service, release);
        }
        catch (Exception ex)
        {
            
            Core.AppLog.Exception("update", ex, "启动时检查更新失败（已忽略，不影响正常使用）");
        }
    }

    
    private void NotifyUpdateAvailable(Services.Updating.UpdateRelease release)
    {
        ShowBalloon($"发现新版本 {release.Tag}", "当前版本不是最新版，单击查看更新详情。",
            () => { ShowFromTray(); _ = CheckUpdateManualAsync(); });
    }

    
    private void OnDownloadFinished(DownloadTask task)
    {
        
        if (task.Id == _downloadDialogTaskId) return;

        
        
        if (task.Kind == DownloadKind.Update) return;

        
        
        
        if (task.Kind == DownloadKind.Cleanup)
        {
            if (task.State == DownloadState.Completed)
            {
                ShowBalloon("清理完成", $"{task.Title}\n已释放 {AppLog.Size(task.Bytes)}。",
                    () => { ShowFromTray(); Shell.NavigateTo("downloads"); });
            }
            else if (task.State == DownloadState.Failed)
            {
                ShowBalloon("清理未完成", $"{task.Title}：{task.Error}",
                    () => { ShowFromTray(); Shell.NavigateTo("downloads"); });
            }
            return;
        }

        if (task.State == DownloadState.Completed)
        {
            
            
            var folder = Path.GetDirectoryName(task.Path ?? "");
            if (string.IsNullOrEmpty(folder)) folder = DownloadService.SaveDir;
            ShowBalloon("下载完成", $"{task.FileName}\n已保存到：{folder}",
                () => { ShowFromTray(); Shell.NavigateTo("downloads"); });
        }
        else if (task.State == DownloadState.Failed)
        {
            ShowBalloon("下载失败", $"{task.Title} 下载未完成：{task.Error}",
                () => { ShowFromTray(); Shell.NavigateTo("downloads"); });
        }
    }

    
    
    
    
    
    private void ShowBalloon(string title, string text, Action? onClick)
    {
        _balloonAction = onClick;
        try { _tray?.ShowBalloon(title, text); }
        catch { }
    }

    

    private bool _updateToastWired;

    
    
    
    
    
    
    public void NotifyUpdateReady(string tag)
    {
        ShowUpdateReadyToast(tag);
    }

    private void ShowUpdateReadyToast(string tag)
    {
        try
        {
            var manager = Microsoft.Windows.AppNotifications.AppNotificationManager.Default;
            if (!_updateToastWired)
            {
                manager.NotificationInvoked += OnUpdateToastInvoked;
                manager.Register();
                _updateToastWired = true;
            }

            var note = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                .AddText($"ClassSoftwareHub {tag} 下载完成")
                .AddText("安装包已通过校验，随时可以安装。")
                .AddButton(new Microsoft.Windows.AppNotifications.Builder.AppNotificationButton("现在安装")
                    .AddArgument("updateAction", "install"))
                .AddButton(new Microsoft.Windows.AppNotifications.Builder.AppNotificationButton("稍后安装")
                    .AddArgument("updateAction", "defer"))
                .BuildNotification();
            manager.Show(note);
            return;
        }
        catch
        {
            
        }

        ShowBalloon($"ClassSoftwareHub {tag} 下载完成", "安装包已通过校验，打开应用即可安装。",
            () => { ShowFromTray(); Shell.NavigateTo("home"); });
    }

    private void OnUpdateToastInvoked(Microsoft.Windows.AppNotifications.AppNotificationManager sender,
        Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs args)
    {
        args.Arguments.TryGetValue("updateAction", out var action);
        if (action != "install") return;    

        
        DispatcherQueue?.TryEnqueue(() => _ = Services.Updating.UpdateFlow.InstallPendingNowAsync());
    }

    
    public void NotifyBackgroundDownloadFailed(string tag, string reason)
    {
        ShowBalloon($"ClassSoftwareHub {tag} 下载失败", $"{reason}\n可稍后在应用内重试。", null);
    }

    
    
    
    
    
    
    
    
    
    private async Task SyncContentAsync()
    {
        App.Content.SetSyncing(true);
        try
        {
            var before = App.Content.SourceKind;
            var result = await Services.ContentUpdater.SyncAsync();

            if (!result.Updated && before == "cache") return;

            
            var oldSource = App.Content.Source;
            var oldCount = App.Content.Apps.Count;
            App.Content.Load();

            var changed = App.Content.Source != oldSource || App.Content.Apps.Count != oldCount;
            if (changed)
                Core.AppLog.Info("content", $"{result.Message}；{oldSource} → {App.Content.Source}");
        }
        catch
        {
            
        }
        finally
        {
            App.Content.SetSyncing(false);
        }
    }

    private SubmitWindow? _submitWindow;

    public SubmitWindow? SubmitWindowRef => _submitWindow;

    
    public Pages.ShellPage Shell => NativeShell;

    
    
    
    
    public async void OpenSubmitWindow()
    {
        if (_submitWindow is not null)
        {
            _submitWindow.Activate();
            return;
        }

        
        if (!_runtime.Probe())
        {
            await ShowRuntimeMissingDialogAsync(ShellConfig.SubmitPageUrl);
            return;
        }

        _submitWindow = new SubmitWindow();
        _submitWindow.Closed += (_, _) => _submitWindow = null;
        _submitWindow.Activate();
    }

    
    
    

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

    
    private static string IconPath()
    {
        var ico = EmbeddedAssets.ExtractToCache("AppIcon.ico", "app-icon.ico");
        if (!string.IsNullOrEmpty(ico) && System.IO.File.Exists(ico)) return ico;
        return Environment.ProcessPath ?? "";
    }

    
    private static string ToolIconPath()
    {
        var ico = EmbeddedAssets.ExtractToCache("ToolIcon.ico", "tool-icon.ico");
        if (!string.IsNullOrEmpty(ico) && System.IO.File.Exists(ico)) return ico;
        return IconPath();
    }

    
    
    
    
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
        
        
        try { SetCurrentProcessExplicitAppUserModelID("TinyNick.ClassSoftwareHub.Desktop"); }
        catch {  }
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn("shell", "AppWindow 获取失败（窗口管理功能全部降级）：" + ex.Message);
        }

        
        InitHotkeys();

        if (_appWindow is null) return;

        _appWindow.Title = ShellConfig.WindowTitle;
        Services.DisplayCapabilities.Refresh(this);

        try
        {
            ApplyWindowIcon(WindowNative.GetWindowHandle(this));
        }
        catch { }

        
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
            LoadWindowBounds();
        }
        catch { }

        
        
        
        
        if (_settings.Current.AutoStart) ApplyAutoStart();

        _appWindow.Closing += (_, _) =>
        {
            Core.AppLog.Info("exit", "主窗 Closing#1 SaveWindowState");
            SaveWindowState(flush: true);
        };

        
        _appWindow.Closing += (_, args) =>
        {
            Core.AppLog.Info("exit", $"主窗 Closing: exitRequested={_exitRequested} "
                + $"closeToTray={_settings.Current.CloseToTray} trayReady={_tray?.IsReady}");
            if (_exitRequested || !_settings.Current.CloseToTray || _tray?.IsReady != true)
            {
                
                
                
                
                App.IsExiting = true;
                Core.AppLog.Info("exit", "主窗 Closing -> 放行（真的要走）");
                return;
            }
            args.Cancel = true;
            Core.AppLog.Info("exit", "主窗 Closing -> 取消（收进托盘）");
            HideToTray();
            ShowTrayHideHintOnce();
        };
        _appWindow.Changed += (_, args) =>
        {
            if (args.DidPositionChange) Services.DisplayCapabilities.Refresh(this);
            if (args.DidSizeChange || args.DidPositionChange || args.DidPresenterChange)
            {
                ApplyWindowRounding();   

                
                
                SaveWindowState();
            }

            
            
            
            if (args.DidPresenterChange && IsMinimized()) Services.MemoryTrimmer.TrimLater(2500);
        };

        
        
        
        Services.MemoryTrimmer.IsIdle = () => !IsWindowVisible(WindowNative.GetWindowHandle(this)) || IsMinimized();

        
        
        
        Services.MemoryTrimmer.StartWatch();
    }

    
    
    
    
    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;

        if (_appWindow is null) return;
        try
        {
            var bar = _appWindow.TitleBar;
            bar.ExtendsContentIntoTitleBar = true;
            bar.PreferredHeightOption = TitleBarHeightOption.Tall;   
            bar.ButtonBackgroundColor = Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn("shell", "标题栏定制不可用：" + ex.Message);
        }

        UpdateCaptionButtonColors();
        ApplyWindowRounding();   
    }

    
    
    
    
    
    
    
    
    
    
    private void ApplyWindowRounding()
    {
        if (_appWindow is null) return;
        try
        {
            var maximized = _appWindow.Presenter is OverlappedPresenter p
                            && p.State == OverlappedPresenterState.Maximized;
            WindowChrome.SetRounded(WindowNative.GetWindowHandle(this), !maximized);
        }
        catch { }
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

    
    private DateTime _lastBoundsSave = DateTime.MinValue;

    
    private bool _windowBoundsLoaded;

    
    
    
    private void LoadWindowBounds()
    {
        if (_appWindow is null) return;

        var s = _settings.Current;
        var w = Math.Max(900, s.WindowWidth);
        var h = Math.Max(600, s.WindowHeight);

        
        
        
        
        
        
        var maxW = GetSystemMetrics(SmCxScreen);
        var maxH = GetSystemMetrics(SmCyScreen);
        if (GetWorkArea(out var work))
        {
            maxW = work.Right - work.Left;
            maxH = work.Bottom - work.Top;
        }
        if (maxW > 0) w = Math.Min(w, maxW);
        if (maxH > 0) h = Math.Min(h, maxH);

        _pendingMaximize = s.WindowMaximized;

        
        
        
        if (!_pendingMaximize) _appWindow.Resize(new SizeInt32(w, h));

        if (s.WindowLeft != int.MinValue && s.WindowTop != int.MinValue
            && IsVisibleOnAnyScreen(s.WindowLeft, s.WindowTop, w, h))
        {
            _pendingPosition = new PointInt32(s.WindowLeft, s.WindowTop);
        }

        
        
        Activated += (_, _) => ApplyPendingPlacement();

        StartBoundsWatch();

        _windowBoundsLoaded = true;
    }

    private PointInt32? _pendingPosition;

    
    private bool _placementDeferred;
    private bool _pendingMaximize;
    private bool _placementApplied;

    
    
    
    
    
    private void StartBoundsWatch()
    {
        try
        {
            
            _boundsTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _boundsTimer.Interval = TimeSpan.FromSeconds(1.2);
            _boundsTimer.Tick += (_, _) => { try { SaveWindowState(); } catch { } };
            _boundsTimer.Start();
        }
        catch { }
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _boundsTimer;

    
    private void ApplyPendingPlacement()
    {
        if (_placementApplied || _appWindow is null) return;
        _placementApplied = true;
        try
        {
            ApplyPlacementCore();

            
            
            
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _ = Task.Run(async () =>
            {
                await Task.Delay(600);
                _ = queue.TryEnqueue(ApplyPlacementCore);
            });
        }
        catch (Exception ex) { Core.AppLog.Warn("shell", "恢复窗口位置失败：" + ex.Message); }
    }

    
    private void ApplyPlacementCore()
    {
        try
        {
            if (_appWindow is null) return;

            
            
            
            
            
            
            if (_startMinimized || _startPalette)
            {
                _placementDeferred = true;
                return;
            }

            
            
            
            
            
            
            
            
            
            if (_pendingMaximize)
            {
                if (_appWindow.Presenter is OverlappedPresenter op
                    && op.State != OverlappedPresenterState.Maximized)
                {
                    op.Maximize();
                }
                return;
            }

            if (_pendingPosition is { } p) _appWindow.Move(p);

            
            if (_appWindow.Presenter is OverlappedPresenter p2
                && p2.State != OverlappedPresenterState.Restored)
            {
                p2.Restore();
                var s = _settings.Current;
                _appWindow.Resize(new SizeInt32(Math.Max(900, s.WindowWidth), Math.Max(600, s.WindowHeight)));
                if (_pendingPosition is { } p3) _appWindow.Move(p3);
            }
        }
        catch { }
    }

    
    
    
    
    private static bool IsVisibleOnAnyScreen(int left, int top, int width, int height)
    {
        try
        {
            
            
            var pt = new NativePoint
            {
                X = left + Math.Min(200, width / 2),
                Y = top + Math.Min(120, height / 2)
            };
            return MonitorFromPoint(pt, MonitorDefaultToNull) != IntPtr.Zero;
        }
        catch
        {
            
            return true;
        }
    }

    private const uint MonitorDefaultToNull = 0;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    
    private static bool GetWorkArea(out NativeRect rect)
    {
        rect = default;
        try { return SystemParametersInfo(SpiGetWorkArea, 0, ref rect, 0); }
        catch { return false; }
    }

    private const uint SpiGetWorkArea = 0x0030;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref NativeRect data, uint winIni);

    
    
    
    
    
    
    
    
    private void SaveWindowState(bool flush = false)
    {
        try
        {
            if (_appWindow is null || !_windowBoundsLoaded) return;

            
            
            
            var hwnd = WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rc)) return;

            
            
            if (!IsWindowVisible(hwnd)) return;

            var sizeW = rc.Right - rc.Left;
            var sizeH = rc.Bottom - rc.Top;
            var screenW = GetSystemMetrics(SmCxScreen);
            var screenH = GetSystemMetrics(SmCyScreen);

            
            
            
            
            
            
            if (_appWindow.Presenter is OverlappedPresenter p
                && (p.State == OverlappedPresenterState.Maximized
                    || p.State == OverlappedPresenterState.Minimized))
            {
                if (!_settings.Current.WindowMaximized)
                {
                    _settings.Current.WindowMaximized = true;
                    _settings.Save();
                }
                return;
            }

            
            var now = DateTime.UtcNow;
            if (!flush && (now - _lastBoundsSave) < TimeSpan.FromMilliseconds(700)) return;
            _lastBoundsSave = now;

            var s = _settings.Current;
            s.WindowMaximized = false;

            
            
            
            var dirty = false;

            if (sizeW > 0 && sizeH > 0 && (sizeW != s.WindowWidth || sizeH != s.WindowHeight))
            {
                s.WindowWidth = sizeW;
                s.WindowHeight = sizeH;
                dirty = true;
            }

            if (rc.Left != s.WindowLeft || rc.Top != s.WindowTop)
            {
                s.WindowLeft = rc.Left;
                s.WindowTop = rc.Top;
                dirty = true;
            }

            if (dirty)
            {
                try { _settings.Save(); }
                catch (Exception saveEx) { Core.AppLog.Exception("shell", saveEx, "写回窗口位置"); }
            }
        }
        catch { }
    }

    

    
    private bool _activationPromptShowing;

    
    
    
    
    
    
    
    
    
    private void OnActivationRequired()
    {
        if (_activationPromptShowing) return;

        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_activationPromptShowing) return;

            
            if (Content?.XamlRoot is null) return;

            _activationPromptShowing = true;
            try
            {
                await Views.LicenseDialog.ShowAsync(Content.XamlRoot, fromGate: true);
            }
            catch (Exception ex)
            {
                AppLog.Exception("license", ex, "弹出激活引导");
            }
            finally
            {
                _activationPromptShowing = false;
            }
        });
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        Core.AppLog.Info("exit", $"主窗口 Closed (exitRequested={_exitRequested})");
        DownloadManager.Current.Finished -= OnDownloadFinished;
        try { _settings.Save(); } catch { }
        try { _tray?.Dispose(); } catch { }
        _tray = null;
        try { _trayTools?.Dispose(); } catch { }
        _trayTools = null;

        
        
        
        
        
        
        if (!_exitRequested)
        {
            _exitRequested = true;
            App.IsExiting = true;
            FinishExit();
        }
    }

    
    
    private static Microsoft.UI.Xaml.Media.ImageSource? LoadAppIcon()
    {
        try
        {
            var path = EmbeddedAssets.ExtractToCache("AppIcon-512.png", "app-icon.png");
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            return new BitmapImage(new Uri(path));
        }
        catch { return null; }
    }

    
    
    

    public void SetBackdrop(string kind)
    {
        _settings.Current.Backdrop = kind;
        ApplyBackdrop(kind);
        _settings.Save();
    }

    
    
    
    
    
    private void ApplyBackdrop(string? kind = null)
    {
        var requested = string.IsNullOrWhiteSpace(kind) ? _settings.Current.Backdrop : kind!;
        kind = string.IsNullOrWhiteSpace(requested) ? "mica" : requested.Trim().ToLowerInvariant();
        var applied = false;

        try
        {
            SystemBackdrop = null;
            RootGrid.Background = null;

            if (kind == "mica")
            {
                if (MicaController.IsSupported())
                {
                    
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
            Core.AppLog.Warn("shell", "背景设置失败，降级纯色：" + ex.Message);
            SystemBackdrop = null;
            kind = "solid";
            RootGrid.Background = new SolidColorBrush(FallbackSolidColor());
        }

    }

    private Color FallbackSolidColor()
        => RootGrid.ActualTheme == ElementTheme.Dark
            ? Color.FromArgb(255, 32, 32, 32)
            : Color.FromArgb(255, 243, 243, 243);

    public void SetTheme(string theme)
    {
        ApplyTheme(theme);
        _settings.Save();
    }

    private void ApplyTheme(string theme)
    {
        theme = string.IsNullOrWhiteSpace(theme) ? "system" : theme.Trim().ToLowerInvariant();
        RootGrid.RequestedTheme = theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        _settings.Current.Theme = theme;
        Services.ThemeHost.Notify();                     

        if (_settings.Current.Backdrop == "solid")
            RootGrid.Background = new SolidColorBrush(FallbackSolidColor());
        else
            ApplyBackdrop();                             

        UpdateCaptionButtonColors();
        Services.ThemeBrush.Probe(RootGrid, "MainWindow.ApplyTheme(" + theme + ")");

    }

    private Task BootAsync()
    {
        
        
        _runtime.Probe();
        RuntimePanel.Visibility = Visibility.Collapsed;

        
        Services.VersionHistory.Record();
        return Task.CompletedTask;
    }

    
    
    

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

    
    
    

    public void SetAlwaysOnTop(bool value)
    {
        _settings.Current.AlwaysOnTop = value;
        if (_appWindow?.Presenter is OverlappedPresenter p) p.IsAlwaysOnTop = value;
        _settings.Save();
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
    }

    
    public void SetMinimizeOnStart(bool value)
    {
        _settings.Current.MinimizeOnStart = value;
        ApplyAutoStart();
        _settings.Save();
    }

    
    public void SetCloseToTray(bool value)
    {
        _settings.Current.CloseToTray = value;
        _settings.Save();
    }

    
    public void SetPaletteOnTop(bool value)
    {
        _settings.Current.PaletteOnTop = value;
        _settings.Save();
        Views.ToolPaletteWindow.ApplyOnTopSetting();
    }

    
    
    

    private void InitTray()
    {
        try
        {
            
            _tray = new TrayIcon(IconPath());
            _tray.LeftClick += ToggleMainWindow;
            _tray.CommandInvoked += OnTrayCommand;
            _tray.BalloonClicked += OnBalloonClicked;

            
            _tray.MenuRequested = (items, invoke) => Views.TrayMenuWindow.Show(items, invoke);

            _tray.MenuItems.Add(new TrayMenuItem { Text = "打开主界面", Command = "show", IsDefault = true });
            _tray.MenuItems.Add(new TrayMenuItem { Text = "常用工具", Command = "palette" });
            _tray.MenuItems.Add(new TrayMenuItem { Text = "工具侧边栏", Command = "sidebar" });
            _tray.MenuItems.Add(new TrayMenuItem { Separator = true });
            _tray.MenuItems.Add(new TrayMenuItem { Text = "检查更新", Command = "update" });
            _tray.MenuItems.Add(new TrayMenuItem { Text = "重启应用", Command = "restart" });
            _tray.MenuItems.Add(new TrayMenuItem { Separator = true });
            _tray.MenuItems.Add(new TrayMenuItem { Text = "退出", Command = "exit" });

            if (!_tray.Setup(ShellConfig.WindowTitle))
                Core.AppLog.Warn("tray", "托盘图标没挂上（挂不上时关闭窗口仍然直接退出）");

            
            _trayTools = new TrayIcon(ToolIconPath());
            _trayTools.LeftClick += () => Views.ToolPaletteWindow.TogglePalette();
            _trayTools.CommandInvoked += OnTrayCommand;
            _trayTools.MenuRequested = (items, invoke) => Views.TrayMenuWindow.Show(items, invoke);
            _trayTools.MenuItems.Add(new TrayMenuItem { Text = "打开常用工具", Command = "palette", IsDefault = true });
            _trayTools.MenuItems.Add(new TrayMenuItem { Text = "打开主界面", Command = "show" });
            _trayTools.MenuItems.Add(new TrayMenuItem { Separator = true });
            _trayTools.MenuItems.Add(new TrayMenuItem { Text = "退出", Command = "exit" });

            if (!_trayTools.Setup("常用工具"))
                Core.AppLog.Warn("tray", "「常用工具」托盘图标没挂上");

            
            
            
            
            
            

            
            Services.TeachingActions.StartFocusWatcher();
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("tray", ex, "初始化托盘");
        }
    }

    
    private void OnBalloonClicked()
    {
        var action = _balloonAction;
        _balloonAction = null;

        if (action is not null)
        {
            try { action(); } catch { }
            return;
        }

        
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
            case "restart":
                RestartApp();
                break;
            case "exit":
                ExitApp();
                break;
        }
    }

    
    public void ToggleMainWindow()
    {
        try
        {
            if (IsWindowVisible(WindowNative.GetWindowHandle(this))) HideToTray();
            else ShowFromTray();
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("tray", ex, "托盘左键切换主窗口");
        }
    }

    
    private bool IsMinimized()
        => _appWindow?.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized;

    
    public void HideToTray()
    {
        try { ShowWindow(WindowNative.GetWindowHandle(this), SW_HIDE); } catch { }
        
        Services.MemoryTrimmer.TrimLater(1200);
    }

    
    
    
    
    
    
    
    private void ShowTrayHideHintOnce()
    {
        if (_settings.Current.TrayHideHintShown) return;

        _settings.Current.TrayHideHintShown = true;
        try { _settings.Save(); } catch { }

        ShowBalloon("程序仍在后台运行",
            "点「×」是把窗口收进托盘，程序并没有退出。点托盘图标或桌面图标都可以重新打开主界面。",
            () => ShowFromTray());
    }

    
    public void ShowFromTray()
    {
        try
        {
            
            if (_placementDeferred)
            {
                _placementDeferred = false;
                ApplyPlacementCore();
            }

            var hwnd = WindowNative.GetWindowHandle(this);
            ShowWindow(hwnd, SW_SHOW);
            if (_appWindow?.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized)
                p.Restore();
            Activate();
            SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("tray", ex, "从托盘叫回主窗口");
        }
    }

    
    
    
    
    public void OpenToolSettings(Type pageType)
    {
        try
        {
            ShowFromTray();
            Shell.NavigateToTool(pageType);
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("palette", ex, "打开工具设置页");
        }
    }

    
    
    

    
    
    
    
    private void InitHotkeys()
    {
        try
        {
            PinnedAppLauncher.EnsureIds();
            _hotkeys = new HotkeyService(this, OnGlobalHotkey, OnSidebarAppHotkey);
            _hotkeys.Apply(_settings.Current);
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("hotkey", ex, "初始化全局快捷键");
        }
    }

    
    public void RefreshHotkeys()
    {
        try { _hotkeys?.Apply(_settings.Current); }
        catch (Exception ex) { Core.AppLog.Exception("hotkey", ex, "重新注册全局快捷键"); }
    }

    
    public void SuspendHotkeys()
    {
        try { _hotkeys?.Suspend(); }
        catch (Exception ex) { Core.AppLog.Exception("hotkey", ex, "挂起全局快捷键"); }
    }

    
    public void ResumeHotkeys() => RefreshHotkeys();

    
    public IReadOnlyList<string> HotkeyConflicts => _hotkeys?.Conflicts ?? Array.Empty<string>();

    
    
    
    
    
    
    
    
    
    
    
    private void OnGlobalHotkey(HotkeyAction action)
    {
        try
        {
            Core.AppLog.Info("hotkey", $"触发：{HotkeyService.ActionName(action)}");

            switch (action)
            {
                case HotkeyAction.ShowMain:
                    ShowFromTray();
                    break;

                case HotkeyAction.Announce:
                    ShowFromTray();
                    if (Content is Pages.ShellPage announceShell) announceShell.NavigateTo("announce");
                    break;

                case HotkeyAction.PptTouch:
                    ShowFromTray();
                    if (Content is Pages.ShellPage pptShell) pptShell.NavigateTo("ppttouch");
                    break;

                case HotkeyAction.Timer:
                    Views.ToolPaletteWindow.ShowTool("timer");
                    break;
            }
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("hotkey", ex, $"处理快捷键 {action}");
        }
    }

    private void OnSidebarAppHotkey(string appId)
    {
        try
        {
            var app = PinnedAppLauncher.FindById(appId);
            if (app is null) return;
            Core.AppLog.Info("hotkey", $"触发快捷应用：{PinnedAppLauncher.DisplayName(app)}");
            if (PinnedAppLauncher.Launch(app) is { } error)
                Core.AppLog.Warn("hotkey", error);
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("hotkey", ex, "处理快捷应用组合键");
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

    
    private async Task CheckUpdateManualAsync()
    {
        try
        {
            var service = Services.Updating.UpdateService.CreateDefault();
            if (!service.Source.IsConfigured)
            {
                Core.AppLog.Info("update", "手动「检查更新」被跳过：更新源未配置");
                return;
            }

            var channel = Services.Updating.UpdateChannels.Parse(_settings.Current.UpdateChannel);
            var result = await service.CheckAsync(channel, ShellConfig.ShellVersion);

            
            if (result is { HasUpdate: true, Release: { } found }) Services.Updating.UpdateNotice.Set(found);
            else Services.Updating.UpdateNotice.Clear();

            var root = (Content as FrameworkElement)?.XamlRoot;
            if (root is null) return;

            if (result is { HasUpdate: true, Release: { } release })
            {
                
                var choice = await Services.Updating.UpdateFlow.AskAsync(root, release);
                if (choice == Services.Updating.UpdateFlow.UpdateChoice.Now)
                    await Services.Updating.UpdateFlow.RunAsync(root, service, release);
                else if (choice == Services.Updating.UpdateFlow.UpdateChoice.Background)
                    Services.Updating.UpdateFlow.StartBackgroundDownload(service, release);
                return;
            }

            var dialog = new ContentDialog
            {
                        RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
                XamlRoot = root,
                Title = "当前已是最新版本",
                Content = $"当前：dv{ShellConfig.DisplayVersion}\n通道：{Services.Updating.UpdateChannels.ToDisplay(channel)}",
                CloseButtonText = "确定",
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            
            Core.AppLog.Fail("update", ex, "手动「检查更新」失败");
        }
    }

    
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

                
                
                
                if (exe.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
                {
                    var installed = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Programs", "ClassSoftwareHub", "ClassSoftwareHub.exe");
                    if (File.Exists(installed)) exe = installed;
                }

                var cmd = _settings.Current.MinimizeOnStart ? $"\"{exe}\" --minimized" : $"\"{exe}\"";
                var current = key.GetValue(valueName) as string;
                if (!string.Equals(current, cmd, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(valueName, cmd);
                    Core.AppLog.Info("startup",
                        $"开机自启项已校正：{(string.IsNullOrEmpty(current) ? "(原本没有)" : current)} → {cmd}");
                }
            }
            else if (key.GetValue(valueName) is not null)
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn("shell", "开机自启设置失败（开关看着是开的，但注册表没写进去）：" + ex.Message);
        }
    }

    
    
    
    
    
    
    
    
    public void OpenExternal(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;

        if (!AllowedExternalSchemes.Contains(uri.Scheme))
        {
            Core.AppLog.Warn("shell", $"拒绝打开非白名单协议的外部链接：{uri.Scheme}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("shell", ex, $"打开外部链接（{uri.Scheme}）");
        }
    }

    
    private static readonly HashSet<string> AllowedExternalSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http",
        "https",
        "ms-windows-store",
    };

    
    
    
    
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
            Core.AppLog.Warn("shell", "打开商店失败，改开网页：" + ex.Message);
            OpenExternal(url);
        }
    }

    
    
    
    
    public void OpenFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("shell", ex, "用系统默认程序打开文件");
        }
    }

    
    
    
    
    public void RevealFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                return;
            }

            if (!File.Exists(path)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("shell", ex, "在资源管理器中定位文件");
        }
    }

    
    
    
    

    private bool _sheetReady;
    private string _sheetUrl = "";

    
    
    
    
    
    private bool _sheetSuspended;

    
    
    
    public async void ShowWebSheet(string? url, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var href = url.Trim();

        if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            OpenExternal(href);   
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

        
        if (_sheetSuspended)
        {
            WebViewMemory.Resume(SheetWeb, "网页浮层");
            _sheetSuspended = false;
        }

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
            WebSheetTitle.Text = "网页无法打开：" + ex.Message;
        }
    }

    
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
        {
            story.Completed += (_, _) =>
            {
                
                WebSheet.Visibility = Visibility.Collapsed;
                SuspendWebSheet();
            };
        }

        story.Begin();
    }

    
    private async void SuspendWebSheet()
    {
        if (_sheetSuspended || !_sheetReady || SheetWeb?.CoreWebView2 is null) return;

        try { _sheetSuspended = await WebViewMemory.SuspendAsync(SheetWeb, "网页浮层"); }
        catch { _sheetSuspended = false; }
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

    
    
    

    
    
    
    
    
    
    
    
    
    
    
    
    public async void DownloadFile(string? url, string? suggestedName = null, string? directory = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        var task = DownloadManager.Current.Start(url, suggestedName, suggestedName, directory);
        _downloadDialogTaskId = task.Id;   

        var root = (Content as FrameworkElement)?.XamlRoot;
        if (root is null)
        {
            _downloadDialogTaskId = null;   
            return;
        }

        
        if (_downloadDialog is { } previous)
        {
            try { previous.Hide(); } catch { }
            await Task.Yield();
        }

        var bar = new ProgressBar { Minimum = 0, Maximum = 100, IsIndeterminate = true };
        var status = new TextBlock { Text = "正在连接", FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        var hint = new TextBlock
        {
            Text = $"保存到：{directory ?? DownloadService.SaveDir}\n单击「后台继续」后，下载任务转入后台运行。",
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
            
            
            
            RequestedTheme = RootGrid.ActualTheme,
            Title = "下载 " + task.Title,
            Content = panel,
            PrimaryButtonText = "后台继续",     
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
            
            task.PropertyChanged -= OnTaskChanged;
        }

        
        _downloadDialogTaskId = null;

        
        if (task.IsRunning) return;
        if (task.State == DownloadState.Canceled) return;    

        if (task.State == DownloadState.Failed)
        {
            await new ContentDialog
            {
                        RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
                XamlRoot = root,
                Title = "下载失败",
                Content = new TextBlock
                {
                    Text = $"{task.Title}\n\n{task.Error}\n\n可在左侧「任务进行」页单击「重试」。",
                    TextWrapping = TextWrapping.Wrap,
                },
                CloseButtonText = "确定",
            }.ShowAsync();
            return;
        }

        
        
        var action = task.Action;

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock
        {
            Text = $"{task.FileName}\n{DownloadProgress.Size(task.Bytes)}",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = task.Path ?? "",
            FontSize = 11.5,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });

        if (action == PackageAction.Install)
            body.Children.Add(new TextBlock
            {
                Text = "该文件为安装包，可在此直接开始安装。",
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
        else if (action == PackageAction.Extract)
            body.Children.Add(new TextBlock
            {
                Text = "该文件为压缩包，将解压到与该文件同级的同名文件夹。",
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });

        
        
        var autoDeleteBox = new CheckBox
        {
            Content = "安装或解压完成后自动删除该文件",
            IsChecked = App.Settings.Current.AutoDeletePackage,
        };
        body.Children.Add(autoDeleteBox);

        
        CheckBox? autoOpenBox = null;
        if (action == PackageAction.Extract)
        {
            autoOpenBox = new CheckBox
            {
                Content = "解压完成后自动打开所在文件夹",
                IsChecked = App.Settings.Current.AutoOpenExtractFolder,
            };
            body.Children.Add(autoOpenBox);
        }

        var done = new ContentDialog
        {
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
            XamlRoot = root,
            Title = "下载完成",
            Content = body,
            PrimaryButtonText = action switch
            {
                PackageAction.Install => "安装",
                PackageAction.Extract => "解压",
                _ => "立即打开",
            },
            SecondaryButtonText = "打开所在文件夹",
            CloseButtonText = "完成",
            DefaultButton = ContentDialogButton.Primary,
        };

        var choice = await done.ShowAsync();

        
        var autoDelete = autoDeleteBox.IsChecked == true;
        var autoOpen = autoOpenBox?.IsChecked == true;
        var settingsChanged = false;

        if (App.Settings.Current.AutoDeletePackage != autoDelete)
        {
            App.Settings.Current.AutoDeletePackage = autoDelete;
            settingsChanged = true;
        }
        if (autoOpenBox is not null && App.Settings.Current.AutoOpenExtractFolder != autoOpen)
        {
            App.Settings.Current.AutoOpenExtractFolder = autoOpen;
            settingsChanged = true;
        }
        if (settingsChanged) App.Settings.Save();

        switch (choice)
        {
            case ContentDialogResult.Primary when action == PackageAction.None:
                OpenFile(task.Path);
                break;

            case ContentDialogResult.Primary:
                
                _ = PackageRunner.RunAsync(task);
                
                
                if (action == PackageAction.Extract) Services.Updating.UpdateFlow.GoToDownloads();
                break;

            case ContentDialogResult.Secondary:
                RevealFile(task.Path);
                break;
        }
    }

    
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
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
            XamlRoot = root,
            Title = "缺少 WebView2 运行时",
            Content = new TextBlock
            {
                Text = "本页面需要系统安装 WebView2 运行时（Microsoft Edge 内核组件，免费）。\n\n" +
                       "可立即安装，也可使用浏览器打开，内容一致。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "安装 WebView2",
            SecondaryButtonText = "浏览器打开",
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

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    public void RestartApp()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            Core.AppLog.Warn("exit", "重启应用失败：取不到自身可执行文件路径");
            return;
        }

        try
        {
            
            var psi = new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 4 >nul & start \"\" \"{exe}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            Process.Start(psi);
            Core.AppLog.Info("exit", "已安排重启：约 2 秒后拉起新实例");
        }
        catch (Exception ex)
        {
            
            Core.AppLog.Fail("exit", ex, "安排重启失败，本次不退出");
            return;
        }

        ExitApp();
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    public async System.Threading.Tasks.Task HandleAppUriAsync(string uri)
    {
        try
        {
            var target = Core.AppUri.Parse(uri);
            if (target is null)
            {
                
                
                if (uri.StartsWith(Core.AppUri.SystemSchemePrefix, StringComparison.OrdinalIgnoreCase)
                    && Uri.TryCreate(uri, UriKind.Absolute, out var systemUri))
                {
                    await Windows.System.Launcher.LaunchUriAsync(systemUri);
                }
                else
                {
                    Core.AppLog.Warn("links", "无法识别的链接：" + uri);
                }
                return;
            }

            ShowFromTray();
            Core.AppLog.Step("links", $"处理链接 {target.Kind}/{target.Target}");

            switch (target.Kind)
            {
                case Core.AppUriKind.Settings:
                    Shell?.NavigateToSettings(target.Target);
                    break;

                case Core.AppUriKind.Page:
                case Core.AppUriKind.Tool:
                    
                    
                    Shell?.NavigateTo(target.Target);
                    break;

                case Core.AppUriKind.Article:
                    
                    Shell?.NavigateToDocArticle(target.Target);
                    break;

                case Core.AppUriKind.System:
                    if (Uri.TryCreate(target.Raw, UriKind.Absolute, out var sys))
                        await Windows.System.Launcher.LaunchUriAsync(sys);
                    break;
            }
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("links", ex, "处理应用链接");
        }
    }

    public void ExitApp()
    {
        Core.AppLog.Info("exit", "ExitApp() 被调用");
        _exitRequested = true;
        
        
        
        
        
        App.IsExiting = true;
        
        
        SaveWindowState(flush: true);
        try { _settings.Save(); } catch { }
        try { _tray?.Dispose(); } catch { }
        _tray = null;
        try { _trayTools?.Dispose(); } catch { }
        _trayTools = null;
        FinishExit();
    }

    
    
    
    
    
    private void FinishExit()
    {
        
        
        
        

        
        
        
        
        
        
        try { _hotkeys?.Dispose(); } catch { }   
        Core.AppLog.Info("exit", "开始显式关闭所有窗口");
        try { Views.ToolPaletteWindow.CloseForExit(); } catch { }
        try { Views.QqFeedbackGuideWindow.CloseForExit(); } catch { }
        try { Views.ToolSidebarWindow.CloseForExit(); } catch { }
        try { Views.LogDetailWindow.CloseForExit(); } catch { }
        try { Views.LegalWindow.CloseForExit(); } catch { }
        try { Views.SubjectToolsWindow.CloseForExit(); } catch { }
        try { Views.HashCheckWindow.CloseForExit(); } catch { }
        try { Views.ImageColorWindow.CloseForExit(); } catch { }
        try { Views.QuickLookupWindow.CloseForExit(); } catch { }
        try { Close(); } catch { }
        Core.AppLog.Info("exit", "窗口已全部请求关闭");

        try { Application.Current.Exit(); }
        catch { Environment.Exit(0); }
        Core.AppLog.Info("exit", "Application.Exit() 已返回（若进程仍在 = 消息循环没退）");

        
        
        new System.Threading.Thread(() =>
        {
            System.Threading.Thread.Sleep(1500);
            Core.AppLog.Info("exit", "1.5 秒后进程仍在 -> 硬退兜底");
            Environment.Exit(0);
        })
        { IsBackground = true, Name = "csh-exit-watchdog" }.Start();
    }


}
