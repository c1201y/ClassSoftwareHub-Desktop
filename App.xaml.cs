using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Input;                 
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace ClassSoftwareHub.Desktop;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    
    
    
    
    
    
    
    
    
    
    
    public static bool IsExiting { get; set; }

    public static SettingsStore Settings { get; } = new();
    public static ITelemetryService Telemetry { get; private set; } = new NoopTelemetryService(Settings);

    
    public static Core.ContentStore Content { get; } = new();

    
    
    
    
    
    
    
    public static WebViewRuntimeService WebRuntime { get; } = new();

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    

    private static MenuFlyout? _textEditMenu;
    private static TextBox? _textMenuTarget;
    
    private static FrameworkElement? _contentRoot;

    
    private static bool _rightButtonDown;
    
    private static long _menuOpenedTicks;
    private static Windows.Foundation.Point _menuOpenedAt;

    
    private static FlyoutBase? _openFlyout;
    
    private static readonly HashSet<FlyoutBase> _guardedFlyouts = new();
    
    private static long _suppressReopenUntilTicks;

    
    
    
    
    
    private void SetupTextEditContextMenu()
    {
        try
        {
            var menu = new MenuFlyout();
            menu.Opening += TextEditMenu_Opening;
            menu.Items.Add(MakeTextMenuItem("剪切", "cut", "\uE8C6", "Ctrl+X"));
            menu.Items.Add(MakeTextMenuItem("复制", "copy", "\uE8C8", "Ctrl+C"));
            menu.Items.Add(MakeTextMenuItem("粘贴", "paste", "\uE77F", "Ctrl+V"));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MakeTextMenuItem("全选", "selectall", "\uE8B3", "Ctrl+A"));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MakeTextMenuItem("撤销", "undo", "\uE7A7", "Ctrl+Z"));
            _textEditMenu = menu;

            
            
            
            
            
            
            

            Core.AppLog.Info("ui", "文本框右键菜单：兜底菜单已就绪（正常走系统自带的那个）");
        }
        catch (Exception ex)
        {
            Core.AppLog.Error("ui", $"文本框右键菜单构建失败：{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    private MenuFlyoutItem MakeTextMenuItem(string text, string tag, string glyph, string accel)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            Tag = tag,
            Icon = new FontIcon { Glyph = glyph },
        };

        
        
        item.KeyboardAcceleratorTextOverride = accel;
        item.Click += TextEditMenu_Click;
        return item;
    }

    private void TextEditMenu_Opening(object? sender, object e)
    {
        if (sender is not MenuFlyout flyout) return;

        var tb = flyout.Target as TextBox;
        _textMenuTarget = tb;
        if (tb is null) return;

        var hasSelection = tb.SelectionLength > 0;

        foreach (var item in flyout.Items)
        {
            if (item is not MenuFlyoutItem mi || mi.Tag is not string tag) continue;

            mi.IsEnabled = tag switch
            {
                "cut" => hasSelection && !tb.IsReadOnly,
                "copy" => hasSelection,
                "paste" => !tb.IsReadOnly,
                "selectall" => tb.Text.Length > 0,
                "undo" => tb.CanUndo,
                _ => true,
            };
        }
    }

    private async void TextEditMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem mi || mi.Tag is not string tag) return;
        if (_textMenuTarget is not { } tb) return;

        
        
        
        switch (tag)
        {
            case "cut":
                if (tb.SelectionLength == 0 || tb.IsReadOnly) break;
                CopyToClipboard(tb.SelectedText);
                tb.SelectedText = "";            
                break;

            case "copy":
                if (tb.SelectionLength == 0) break;
                CopyToClipboard(tb.SelectedText);
                break;

            case "paste":
                if (tb.IsReadOnly) break;
                await PasteFromClipboardAsync(tb);
                break;

            case "selectall":
                tb.SelectAll();
                break;

            case "undo":
                tb.Undo();
                break;
        }

        
        tb.Focus(FocusState.Programmatic);
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private void OnRootRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_contentRoot is null) return;

        try
        {
            
            if (TryOpenTextMenu(e.GetPosition(_contentRoot), "触摸长按")) e.Handled = true;
        }
        catch (Exception ex)
        {
            Core.AppLog.Error("ui", "文本框右键菜单弹出失败（RightTapped）：" + ex.Message);
        }
    }

    
    
    
    
    
    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_contentRoot is null) return;

        try
        {
            _rightButtonDown = e.GetCurrentPoint(_contentRoot).Properties.IsRightButtonPressed;
        }
        catch
        {
            _rightButtonDown = false;      
        }

        try
        {
            if (_rightButtonDown)
                _suppressReopenUntilTicks = 0;      
            else
                DismissTextMenuBecauseClickAway();
        }
        catch {  }
    }

    
    
    
    
    
    
    
    private static void DismissTextMenuBecauseClickAway()
    {
        var flyout = _openFlyout;
        if (flyout is null) return;

        _suppressReopenUntilTicks = Environment.TickCount64 + 600;

        if (!flyout.IsOpen) return;      

        try { flyout.Hide(); }
        catch {  }
    }

    
    
    
    
    private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_rightButtonDown) return;
        _rightButtonDown = false;

        if (_contentRoot is null) return;

        try
        {
            var pt = e.GetCurrentPoint(_contentRoot);
            if (pt.Properties.PointerUpdateKind != PointerUpdateKind.RightButtonReleased) return;

            TryOpenTextMenu(pt.Position, "鼠标");
        }
        catch (Exception ex)
        {
            Core.AppLog.Error("ui", "文本框右键菜单弹出失败（指针）：" + ex.Message);
        }
    }

    
    
    
    
    
    
    
    
    
    
    
    private bool TryOpenTextMenu(Windows.Foundation.Point pt, string source)
    {
        if (_contentRoot is null) return false;

        
        var now = Environment.TickCount64;
        if (now - _menuOpenedTicks < 400 &&
            Math.Abs(pt.X - _menuOpenedAt.X) < 8 &&
            Math.Abs(pt.Y - _menuOpenedAt.Y) < 8)
        {
            return false;
        }

        var tb = HitTestTextBox(_contentRoot, pt);
        if (tb is null)
        {
            
            Core.AppLog.Info("ui", $"右键菜单：来源={source} 点={pt.X:F0},{pt.Y:F0} 命中=（无）");
            return false;
        }

        Core.AppLog.Info("ui",
            $"右键菜单：来源={source} 点={pt.X:F0},{pt.Y:F0} 命中={tb.Name} "
            + $"自带菜单={tb.ContextFlyout?.GetType().Name ?? "（null）"}");

        try
        {
            
            var tl = tb.TransformToVisual(_contentRoot).TransformPoint(new Windows.Foundation.Point(0, 0));
            var pos = new Windows.Foundation.Point(pt.X - tl.X, pt.Y - tl.Y);

            
            
            
            
            
            
            
            var native = tb.ContextFlyout;

            if (native is null)
            {
                native = _textEditMenu;          
                _textMenuTarget = tb;
            }
            else
            {
                
                
                try { tb.Focus(FocusState.Pointer); } catch {  }
            }

            if (native is null) return false;

            _menuOpenedTicks = now;
            _menuOpenedAt = pt;

            AttachFlyoutGuard(native);        

            
            
            
            
            
            var toShow = native;
            if (!_contentRoot.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        toShow.ShowAt(tb, new FlyoutShowOptions { Position = pos });
                        _openFlyout = toShow;      
                    }
                    catch (Exception ex)
                    {
                        Core.AppLog.Error("ui", "文本框右键菜单弹出失败：" + ex.Message);
                    }
                }))
            {
                Core.AppLog.Warning("ui", "文本框右键菜单：DispatcherQueue 满了，这次没弹");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Core.AppLog.Error("ui", "文本框右键菜单弹出失败：" + ex.Message);
            return false;
        }
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private static void AttachFlyoutGuard(FlyoutBase flyout)
    {
        if (!_guardedFlyouts.Add(flyout)) return;      

        try
        {
            flyout.Opening += (_, _) =>
            {
                
                if (Environment.TickCount64 >= _suppressReopenUntilTicks) return;

                var queue = _contentRoot?.DispatcherQueue;
                if (queue is null) return;

                queue.TryEnqueue(() =>
                {
                    try
                    {
                        if (!flyout.IsOpen) return;
                        flyout.Hide();
                        Core.AppLog.Info("ui", "右键菜单：点别处之后平台又把它弹回来了，已收起");
                    }
                    catch
                    {
                        
                    }
                });
            };

            flyout.Closed += (_, _) =>
            {
                if (ReferenceEquals(_openFlyout, flyout)) _openFlyout = null;
            };
        }
        catch (Exception ex)
        {
            Core.AppLog.Error("ui", "右键菜单：关闭守卫挂载失败：" + ex.Message);
        }
    }

    
    
    
    
    private static TextBox? HitTestTextBox(DependencyObject node, Windows.Foundation.Point pt)
    {
        TextBox? best = null;
        var bestArea = double.MaxValue;

        void Walk(DependencyObject n)
        {
            if (n is TextBox tb && _contentRoot is not null)
            {
                try
                {
                    var tl = tb.TransformToVisual(_contentRoot).TransformPoint(new Windows.Foundation.Point(0, 0));
                    var rect = new Windows.Foundation.Rect(tl.X, tl.Y, tb.ActualWidth, tb.ActualHeight);

                    if (rect.Contains(pt))
                    {
                        var area = tb.ActualWidth * tb.ActualHeight;
                        if (area < bestArea)
                        {
                            bestArea = area;
                            best = tb;
                        }
                    }
                }
                catch
                {
                    
                }
            }

            var count = VisualTreeHelper.GetChildrenCount(n);
            for (var i = 0; i < count; i++) Walk(VisualTreeHelper.GetChild(n, i));
        }

        Walk(node);
        return best;
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            var dp = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            dp.SetText(text);
            Clipboard.SetContent(dp);
        }
        catch
        {
            
        }
    }

    private static async Task PasteFromClipboardAsync(TextBox tb)
    {
        try
        {
            var view = Clipboard.GetContent();
            if (view is null || !view.Contains(StandardDataFormats.Text)) return;

            var text = await view.GetTextAsync();
            if (string.IsNullOrEmpty(text)) return;

            
            tb.SelectedText = text;
        }
        catch
        {
        }
    }

    public App()
    {
        
        
        
        
        
        
        
        
        
        
        
        
        var argv = Environment.GetCommandLineArgs();
        for (var i = 0; i < argv.Length - 1; i++)
        {
            if (!argv[i].Equals(Services.LeftoverCleaner.ApplySwitch, StringComparison.OrdinalIgnoreCase))
                continue;

            Services.LeftoverCleaner.ApplyManifestElevated(argv[i + 1]);
            Environment.Exit(0);
            return;
        }

        
        
        
        for (var i = 0; i < argv.Length - 1; i++)
        {
            if (!argv[i].Equals(Services.StartupItems.AdminSwitch, StringComparison.OrdinalIgnoreCase))
                continue;

            Services.StartupItems.ApplyElevated(argv[i + 1]);
            Environment.Exit(0);
            return;
        }

        
        
        
        
        
        for (var i = 0; i < argv.Length - 1; i++)
        {
            if (!argv[i].Equals(Services.PageFileService.AdminSwitch, StringComparison.OrdinalIgnoreCase))
                continue;

            Services.PageFileService.ApplyElevated(argv[i + 1]);
            Environment.Exit(0);
            return;
        }

        
        
        
        
        for (var i = 0; i < argv.Length - 1; i++)
        {
            if (!argv[i].Equals(Services.SystemRepair.AdminSwitch, StringComparison.OrdinalIgnoreCase))
                continue;

            Services.SystemRepair.RunElevated(argv[i + 1]);
            Environment.Exit(0);
            return;
        }

        
        
        
        for (var i = 0; i < argv.Length - 1; i++)
        {
            var isFull = argv[i].Equals(Services.LeftoverDoctor.FullSwitch, StringComparison.OrdinalIgnoreCase);
            var isScan = argv[i].Equals(Services.LeftoverDoctor.ScanSwitch, StringComparison.OrdinalIgnoreCase);
            if (!isFull && !isScan) continue;

            
            
            
            
            
            
            var code = Task.Run(() => Services.LeftoverDoctor.RunAsync(argv[i + 1], isScan))
                           .GetAwaiter().GetResult();

            Environment.Exit(code);
            return;
        }

        InitializeComponent();
        UnhandledException += OnUnhandledException;

        
        
        
        
        
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        
        Core.AppLog.ContextProvider = CurrentScene;

        
        
        

        
        
        var mutex = new Mutex(initiallyOwned: true, Core.ShellConfig.MutexName, out var isFirstInstance);

        if (isFirstInstance)
        {
            _instanceMutex = mutex;
            TryCreateActivateSignal();
            return;
        }

        
        
        
        
        
        
        
        
        
        
        mutex.Dispose();

        
        
        try
        {
            if (Services.AppLinks.ExtractFromCommandLine() is { Length: > 0 } incoming)
            {
                Services.AppLinks.Queue(incoming);
                Core.AppLog.Info("links", "已把链接转交给正在运行的实例：" + incoming);
            }
        }
        catch (Exception ex)
        {
            Core.AppLog.Info("links", "转交链接失败: " + ex.Message);
        }

        if (WakeUpExistingInstance())
        {
            Environment.Exit(0);
            return;
        }

        
        _instanceMutex = new Mutex(initiallyOwned: true, Core.ShellConfig.MutexName, out _);
        TryCreateActivateSignal();
    }

    private static Mutex? _instanceMutex;

    
    private static EventWaitHandle? _activateSignal;

    
    
    
    
    
    
    
    
    private static string? _startupLink;

    
    public static string? TakeStartupLink()
    {
        var link = _startupLink;
        _startupLink = null;
        return link;
    }

    
    
    
    
    
    
    
    
    
    private static bool _oobePending;

    
    
    
    
    
    internal static bool ShellDeferredForOobe { get; private set; }

    
    private static void TryCreateActivateSignal()
    {
        try
        {
            _activateSignal = new EventWaitHandle(
                initialState: false, EventResetMode.AutoReset, Core.ShellConfig.ActivateEventName);
        }
        catch (Exception ex)
        {
            _activateSignal = null;
            Core.AppLog.Info("single", "叫醒事件建不出来: " + ex.Message);
        }
    }

    
    
    
    
    private static bool WakeUpExistingInstance()
    {
        var name = Core.ShellConfig.ActivateEventName;
        var knocked = false;

        
        for (var attempt = 0; attempt < 30 && !knocked; attempt++)
        {
            try
            {
                using var handle = EventWaitHandle.OpenExisting(name);
                handle.Set();
                knocked = true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(100);
            }
            catch (Exception ex)
            {
                
                Core.AppLog.Info("single", "叫醒事件打不开: " + ex.Message);
                return true;
            }
        }

        if (!knocked) return true;

        
        Thread.Sleep(1200);

        try
        {
            using var probe = new Mutex(initiallyOwned: false, Core.ShellConfig.MutexName);
            return !probe.WaitOne(0);
        }
        catch
        {
            return true;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        
        
        
        var hadSettingsFile = System.IO.File.Exists(Settings.FilePath);

        Settings.Load();

#if DEBUG
        
        
        var previewOobe = Environment.GetCommandLineArgs().Any(a =>
            a.Equals("--oobe-preview", StringComparison.OrdinalIgnoreCase));
        if (previewOobe)
        {
            _oobePending = true;
        }
        else
#endif
        
        
        if (!hadSettingsFile || Settings.Current.OobeState == OobeStates.Pending)
        {
            Settings.Current.OobeState = OobeStates.Pending;
            Settings.Save();                 
            _oobePending = true;
        }

        
        
        
        
        SetupTextEditContextMenu();

        
        
        
        
        
        
        
        try
        {
            Services.LegacyCleanup.Run();
            Services.LogHome.MigrateLegacy();
            Services.LogHome.Ensure();
            Services.CacheHome.Ensure();
        }
        catch {  }

        Telemetry = new NoopTelemetryService(Settings);
        Telemetry.Track("app_launch", new Dictionary<string, object?>
        {
            ["shellVersion"] = Core.ShellConfig.ShellVersion,
            ["osBuild"] = Environment.OSVersion.Version.Build
        });

        
        
        
        
        
        
        
        
        
        
        
        
        
        StartActivateListener();

        
        
        var startupHidesWindow = Environment.GetCommandLineArgs().Any(a =>
            a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--palette", StringComparison.OrdinalIgnoreCase));

        
        
        
        var deferForOobe = _oobePending && !startupHidesWindow;

        ShellDeferredForOobe = deferForOobe;

        try
        {
            MainWindow = new MainWindow();
        }
        catch (Exception ex)
        {
            Core.AppLog.Info("startup", "主窗口构造失败: " + ex);
        }

        if (!deferForOobe)
        {
            try
            {
                MainWindow?.Activate();
            }
            catch (Exception ex)
            {
                Core.AppLog.Info("startup", "主窗口 Activate 失败: " + ex);
            }
        }
        else
        {
            Core.AppLog.Info("startup", "本次是新装首启：主窗口先不显示，等初次配置走完");
        }

        
        
        
        
        try
        {
            Services.AppLinks.RegisterProtocol();
            _startupLink = Services.AppLinks.ExtractFromCommandLine();
            if (_startupLink is { Length: > 0 }) Core.AppLog.Info("links", "启动参数里带着链接：" + _startupLink);
        }
        catch (Exception ex)
        {
            Core.AppLog.Info("links", "启动时准备应用链接失败: " + ex.Message);
        }

        
        
        
        
        
        try
        {
            if (MainWindow?.Content is FrameworkElement contentRoot)
            {
                _contentRoot = contentRoot;      

                
                contentRoot.AddHandler(UIElement.RightTappedEvent,
                                       new RightTappedEventHandler(OnRootRightTapped), true);

                
                contentRoot.AddHandler(UIElement.PointerPressedEvent,
                                       new PointerEventHandler(OnRootPointerPressed), true);
                contentRoot.AddHandler(UIElement.PointerReleasedEvent,
                                       new PointerEventHandler(OnRootPointerReleased), true);

                Core.AppLog.Info("ui", "文本框右键菜单：已挂右键钩子（鼠标=按下/抬起，触摸=长按）");
            }
            else
            {
                Core.AppLog.Warning("ui", "文本框右键菜单：MainWindow.Content 不是 UIElement，兜底钩子没挂上");
            }
        }
        catch (Exception ex)
        {
            Core.AppLog.Error("ui", "文本框右键菜单兜底挂载失败：" + ex.Message);
        }

        
        
        if (MainWindow is null)
            Core.AppLog.Info("startup", "主窗口未能建立，应用将停在「只有侧边栏」的状态");

        
        
        
        
        
        
        if (!startupHidesWindow && !deferForOobe) MainWindow?.ArmStartupVisibleWatchdog();

        
        
        
        Data.EasiNoteGuard.Start();
        Data.ProcessGuard.Start();

        
        
        
        
        Data.AutoShutdown.Start();

        
        
        try
        {
            Services.PptTouch.Start();
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("ppttouch", ex, "启动轻点翻页");
        }

        
        
        
        
        
        Services.VirtualKeyboardRetire.Run();

        
        
        Services.Updating.InstallerCleanup.Start();

        
        
        Services.CacheHome.Start();

        
        
        Services.LogHome.Start();

        
        
        
        
        Services.MirrorSign.LogKeyStatus();

        
        
        try
        {
            Services.AnnounceService.Start();
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("announce", ex, "启动喊话轮询");
        }

        
        try
        {
            Services.AnnounceLanService.Start();
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("announce-lan", ex, "启动局域网通道");
        }

        
        
        if (deferForOobe)
        {
#if DEBUG
            if (previewOobe) Views.WelcomeWindow.ShowPreview(EndOobeAndShowMainWindow);
            else
#endif
            Views.WelcomeWindow.ShowForFirstRun(EndOobeAndShowMainWindow);
        }
    }

    
    
    
    
    
    
    
    
    private static void EndOobeAndShowMainWindow()
    {
        Core.AppLog.Info("oobe", "初次配置结束，显示主窗口");

        
        try
        {
            MainWindow?.StartShell();
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("oobe", ex, "向导结束后初始化 Shell 失败");
        }

        try
        {
            MainWindow?.Activate();
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn("oobe", "初次配置结束后激活主窗口失败：" + ex.Message);
        }

        try
        {
            MainWindow?.ArmStartupVisibleWatchdog();
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn("oobe", "主窗口可见性兜底安装失败：" + ex.Message);
        }
    }

    
    
    
    
    
    
    
    
    private static void StartActivateListener()
    {
        var signal = _activateSignal;
        if (signal is null) return;

        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is null) return;

        var listener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!signal.WaitOne()) break;
                }
                catch
                {
                    break;
                }

                
                Core.AppLog.Info("single", "收到唤醒请求，把主窗口叫出来");

                queue.TryEnqueue(() =>
                {
                    var win = MainWindow;

                    
                    
                    
                    
                    if (win is null)
                    {
                        Core.AppLog.Info("single", "主窗口不存在，唤醒请求无人处理（见同日的 [startup] 记录）");
                        return;
                    }

                    try
                    {
                        win.ShowFromTray();
                    }
                    catch (Exception ex)
                    {
                        Core.AppLog.Info("single", "叫醒主窗口失败: " + ex.Message);
                    }

                    
                    
                    
                    try
                    {
                        var link = Services.AppLinks.TryTakeQueued();
                        if (link is { Length: > 0 })
                        {
                            Core.AppLog.Step("single", "唤醒请求带着链接：" + link);
                            _ = win.HandleAppUriAsync(link);
                        }
                    }
                    catch (Exception ex)
                    {
                        Core.AppLog.Warn("links", "处理唤醒带来的链接失败：" + ex.Message);
                    }
                });
            }
        })
        {
            IsBackground = true,
            Name = "csh-activate-listener",
        };

        listener.Start();
    }

    
    
    
    
    
    private static string CurrentScene()
    {
        try
        {
            var s = "page=" + Pages.ShellPage.CurrentTag;
            if (MainWindow?.AppWindow is { } win) s += $" window={win.Size.Width}x{win.Size.Height}";
            return s;
        }
        catch { return ""; }
    }

    
    
    
    
    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        try
        {
            var tail = $"CLR 未处理异常（即将终止={e.IsTerminating}）";
            if (e.ExceptionObject is Exception ex) Core.AppLog.Exception("crash", ex, tail);
            else Core.AppLog.Error("crash", tail + "：" + e.ExceptionObject);
        }
        catch { }
    }

    
    
    
    
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try { Core.AppLog.Exception("crash", e.Exception, "后台任务未观察异常（已吞，不升级为进程级）"); }
        catch { }
        try { e.SetObserved(); } catch { }
    }

    
    
    
    
    
    
    
    
    private static void OnProcessExit(object? sender, EventArgs e)
    {
        try
        {
            if (!Services.LogHome.Established) return;
            Core.AppLog.Info("exit", $"进程正常终止（退出码 {Environment.ExitCode}）");
        }
        catch { }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        
        
        
        var layoutCycle = e.Exception is Microsoft.UI.Xaml.LayoutCycleException;

        try
        {
            Telemetry.TrackException(e.Exception, "xaml_unhandled");

            
            
            Core.AppLog.Exception("crash", e.Exception,
                layoutCycle ? "XAML 布局循环（可忽略，不致命）" : "XAML 未处理异常");
        }
        catch {  }

        
        
        
        
#if DEBUG
        e.Handled = layoutCycle;
#else
        e.Handled = true;
#endif
    }
}
