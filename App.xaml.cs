using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;

namespace ClassSoftwareHub.Desktop;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    /// <summary>
    /// 「应用正在退出」的全局标记（2026-10-04 修）。
    ///
    /// ⛔ 为什么必须有：工具浮窗（<c>ToolPaletteWindow</c>）和 Q 群反馈窗（<c>QqFeedbackGuideWindow</c>）
    ///    都把 <c>AppWindow.Closing</c> 拦下来当"收起来"用（<c>args.Cancel = true</c>），这是它们自己的
    ///    正常语义。但 WinUI 的 <c>Application.Exit()</c> 是**逐个关窗**的，碰到被取消的就中止整条退出
    ///    流程 —— 结果：只要这两个窗里任意一个开着，托盘菜单「退出」就会变成
    ///    「主窗关了、托盘图标摘了、**进程却一直赖在任务管理器里**」。
    ///    实测复现：正常启动 → 托盘菜单开「常用工具」→ 托盘菜单「退出」→ 进程 15 分钟不退。
    ///    退出流程一开始就把它置 true，那两个窗口的 Closing 看到它就放行，不再拦。
    /// </summary>
    public static bool IsExiting { get; set; }

    public static SettingsStore Settings { get; } = new();
    public static ITelemetryService Telemetry { get; private set; } = new NoopTelemetryService(Settings);

    /// <summary>软件内容（原生界面用）。启动时 Load 一次，内含容错解析与问题清单。</summary>
    public static Core.ContentStore Content { get; } = new();

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;

        // 单实例。这个命名的互斥体同时也是安装程序 [Setup] AppMutex 用的名字 —— 装/升级时 Inno 靠它
        // 判断"应用还在跑"，所以进程活着期间必须一直持有，不能释放。
        var mutex = new Mutex(initiallyOwned: true, Core.ShellConfig.MutexName, out var isFirstInstance);

        if (isFirstInstance)
        {
            _instanceMutex = mutex;
            TryCreateActivateSignal();
            return;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  已经有一个实例在跑 —— 来的是"用户又点了一次图标"
        //
        //  ⛔ 这里以前就一句 Environment.Exit(0)：界面、提示、日志全无。而默认关闭窗口是
        //     **收进托盘**（CloseToTray 默认 true），进程并没有退出，所以用户看到的现象是
        //     "把软件关掉之后点桌面图标打不开，点三四次一点反应都没有，只能去任务管理器结束进程"
        //     （2026-09-30 用户实测反馈）。托盘图标如果又被系统折进溢出区，就彻底没有入口了。
        //
        //  现在改成：先敲门，让那个实例把主窗口叫出来；它确实还活着我们才退。
        // ═══════════════════════════════════════════════════════════════════════
        mutex.Dispose();

        if (WakeUpExistingInstance())
        {
            Environment.Exit(0);
            return;
        }

        // 敲门期间对方正好退出了（互斥体空了出来）→ 由本进程接管，照常启动
        _instanceMutex = new Mutex(initiallyOwned: true, Core.ShellConfig.MutexName, out _);
        TryCreateActivateSignal();
    }

    private static Mutex? _instanceMutex;

    /// <summary>第一个实例监听用的"叫醒"事件。</summary>
    private static EventWaitHandle? _activateSignal;

    /// <summary>建"叫醒"事件。建不出来（极罕见）只是丢掉这条兜底路径，不影响启动。</summary>
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
            Services.ScreenCapture.Log("[single] 叫醒事件建不出来: " + ex.Message);
        }
    }

    /// <summary>
    /// 敲一下"叫醒"事件，请已经在跑的那个实例把主窗口亮出来。
    /// 返回 <c>true</c> = 对方还活着（本实例该退）；<c>false</c> = 对方在这期间退出了（互斥体已空出来，本实例接管）。
    /// </summary>
    private static bool WakeUpExistingInstance()
    {
        var name = Core.ShellConfig.ActivateEventName;
        var knocked = false;

        // 第一个实例理论上可能还卡在启动途中（事件还没建），给几秒。正常情况第一次就成功。
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
                // 打不开又不是"不存在"（多半是权限）→ 保守当作对方活着，别冒险双开
                Services.ScreenCapture.Log("[single] 叫醒事件打不开: " + ex.Message);
                return true;
            }
        }

        if (!knocked) return true;

        // 给第一个实例一点时间把窗口亮出来；同时看它是不是正在退出 —— 互斥体一空出来就归我们。
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
        Settings.Load();

        // 数据目录分区：日志 → logs\，内嵌解包的图片/图标缓存 → cache\（旧版全堆在根上）。
        // 尽早搬 —— 后面任何模块一写日志就落到新位置了。
        try
        {
            Core.AppLog.MigrateLegacyFiles();
            Services.EmbeddedAssets.MigrateLegacyCache();
        }
        catch { /* 迁移失败不影响启动，文件留在原地 */ }

        Telemetry = new NoopTelemetryService(Settings);
        Telemetry.Track("app_launch", new Dictionary<string, object?>
        {
            ["shellVersion"] = Core.ShellConfig.ShellVersion,
            ["osBuild"] = Environment.OSVersion.Version.Build
        });

        // ⚠️⚠️ 顺序别改回去（2026-10-02）。
        //
        //  MainWindow 这个静态属性要等整个构造函数**返回**才被赋值，所以「建窗」这一段
        //  天然存在一个空档：窗口已经在建/已经显示，而 App.MainWindow 还是 null。
        //  唤醒监听线程原来排在 Activate() 之后 —— 空档期间来的敲门没人接，
        //  第二个实例等 1.2 秒后自己退掉，用户看到的是「点了一点反应都没有」，日志里一句话不留。
        //  所以改成：**先铺唤醒通道 → 再建窗 → 再激活**，每一步单独兜异常、单独留痕。
        //
        //  同日还修了另一半：工具侧边栏原来在 InitTray()（主窗口构造函数中途）就
        //  Activate 出来，抢在主窗口之前出现在屏幕上并置顶，主窗口那声 Activate 反倒成了配角。
        //  现在挪到主窗口首帧之后（MainWindow.OnFirstActivated → StartToolSidebar）。
        //  2026-10-02 有用户在 Win10 19045 上报的正是「有侧边栏、没主界面、任务栏没图标、
        //  点桌面图标没反应」这一组症状。
        StartActivateListener();

        try
        {
            MainWindow = new MainWindow();
        }
        catch (Exception ex)
        {
            Services.ScreenCapture.Log("[startup] 主窗口构造失败: " + ex);
        }

        try
        {
            MainWindow?.Activate();
        }
        catch (Exception ex)
        {
            Services.ScreenCapture.Log("[startup] 主窗口 Activate 失败: " + ex);
        }

        // 走到这儿 MainWindow 还是 null，说明应用会停在「只有侧边栏、没有主界面」的状态。
        // 这条日志就是给那个场景留的指纹 —— 没有它，日志里只剩下一句"收到唤醒请求"。
        if (MainWindow is null)
            Services.ScreenCapture.Log("[startup] 主窗口未能建立，应用将停在「只有侧边栏」的状态");

        // 兜底：Activate() 返回 ≠ 窗口已经显示出来。隔一拍核一次可见性，
        //   不可见就再亮一次并把结果写进日志 —— 这条链路以后出问题能一眼定位。
        //   ⚠️ --minimized / --palette 是**故意**藏主窗口的，这两种启动方式不装兜底，
        //      否则刚藏起来就被自己叫回来。
        var startupHidesWindow = Environment.GetCommandLineArgs().Any(a =>
            a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--palette", StringComparison.OrdinalIgnoreCase));

        if (!startupHidesWindow) MainWindow?.ArmStartupVisibleWatchdog();

        // 两个实验性功能的到点巡检。⚠️ 都靠本进程内的定时器 —— 本程序没在跑就不会查杀
        //    （2026-09-28 Nick 确认按这个来，不往系统里装计划任务）。
        //    「白板专杀」与「程序专杀」各跑各的定时器、各存各的配置，刻意不合并（Nick 明确要求白板独立）。
        Data.EasiNoteGuard.Start();
        Data.ProcessGuard.Start();

        // 虚拟键盘（实验性功能）：总开关是单一的 —— 关着的时候 Start() 第一句就 return，
        // 触摸钩子、UIA 探测、注册表接管一个都不会上电（见 VirtualKeyboardService）。
        Services.VirtualKeyboard.VirtualKeyboardService.Start();

        // 更新安装包自动清理：updates 目录只留最近 N 个（默认 3），更早的删掉。
        // 走后台线程，不沾首帧；新版本装完后的第一次启动正好把旧包收掉。
        Services.Updating.InstallerCleanup.Start();
    }

    /// <summary>
    /// 起一条后台线程守"叫醒"事件：用户又点了一次桌面图标 → 第二个实例 Set 这个事件 →
    /// 这里回到 UI 线程把主窗口叫出来（<see cref="MainWindow.ShowFromTray"/> 对"已可见"的窗口
    /// 也会重新激活并抢前台，所以"程序开着但被压在后面"时点图标同样有效）。
    ///
    /// ⚠️ <c>IsBackground = true</c>：这条线程绝不能拦住进程退出。
    /// ⚠️ 事件是 AutoReset 的 —— 第二次点击如果发生在处理途中，信号不会丢。
    /// </summary>
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

                // 落一条日志：这条链路出问题时，日志是唯一能分辨"根本没收到"还是"收到了但窗口没出来"的依据
                Services.ScreenCapture.Log("[single] 收到唤醒请求，把主窗口叫出来");

                queue.TryEnqueue(() =>
                {
                    var win = MainWindow;

                    // ⚠️ 这两条分支必须分开（2026-10-02）：
                    //    原来只有 MainWindow?.ShowFromTray() 一句 —— 属性为 null 时 ?. 会**静默跳过**，
                    //    日志里就只剩上面那句"收到唤醒请求"，跟"叫醒成功"长得一模一样。
                    //    于是「点桌面图标没反应」这条悬案根本分不出是"敲门没人听"还是"听见了但窗口没出来"。
                    if (win is null)
                    {
                        Services.ScreenCapture.Log("[single] 主窗口不存在，唤醒请求无人处理（见同日的 [startup] 记录）");
                        return;
                    }

                    try
                    {
                        win.ShowFromTray();
                    }
                    catch (Exception ex)
                    {
                        Services.ScreenCapture.Log("[single] 叫醒主窗口失败: " + ex.Message);
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

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // ⚠️ 2026-09-29：「页面布局循环」（LayoutCycleException）不是致命错误 —— WinUI 会放弃本次布局、
        //    页面照样能用。但它会让 DEBUG 版直接 fail-fast（见下面 #if DEBUG），应用**瞬间消失**，
        //    连"当时在哪一页"都留不下。所以这一类单独兜住：只记日志、不让它把进程带走。
        var layoutCycle = e.Exception is Microsoft.UI.Xaml.LayoutCycleException;

        try
        {
            Telemetry.TrackException(e.Exception, "xaml_unhandled");
            var dir = SettingsStore.Dir;
            Directory.CreateDirectory(dir);

            // 排障信息：当时停在哪一页 + 窗口多大。
            // 「布局循环」这类问题只在特定尺寸下冒出来，没有这两个数根本无从复现。
            var where = "page=" + Pages.ShellPage.CurrentTag;
            try
            {
                if (MainWindow?.AppWindow is { } win)
                    where += $" window={win.Size.Width}x{win.Size.Height}";
            }
            catch { /* 拿不到窗口尺寸不影响记录 */ }
            if (layoutCycle) where += " ignored=1";

            Core.AppLog.Write("crash", Core.LogLevel.Error,
                $"[{where}] {e.Message}\n{e.Exception}");
        }
        catch { /* 记录失败也不影响 */ }

        // 开发期不静默退出，异常直接炸出来方便定位（VS 里能断到现场）。
        // 发布版必须兜底：老师正在上课，任何一个页面级异常（比如某个 {ThemeResource} 解析失败）
        // 都不该让整个应用消失、界面状态全丢 —— 那比"这个功能坏了"严重得多。
        // 上面已经写进 crash.log 了，事后能查；这里只负责"别死"。
#if DEBUG
        e.Handled = layoutCycle;
#else
        e.Handled = true;
#endif
    }
}
