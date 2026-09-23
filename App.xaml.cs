using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;

namespace ClassSoftwareHub.Desktop;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }
    public static SettingsStore Settings { get; } = new();
    public static ITelemetryService Telemetry { get; private set; } = new NoopTelemetryService(Settings);

    /// <summary>软件内容（原生界面用）。启动时 Load 一次，内含容错解析与问题清单。</summary>
    public static Core.ContentStore Content { get; } = new();

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;

        // 单实例：第二个实例直接退出。
        // 这个命名的互斥体同时也是安装程序 [Setup] AppMutex 用的名字 —— 装/升级时 Inno 靠它
        // 判断"应用还在跑"，所以进程活着期间必须一直持有，不能释放。
        _instanceMutex = new Mutex(initiallyOwned: true, Core.ShellConfig.MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            Environment.Exit(0);
            return;
        }
    }

    private static Mutex? _instanceMutex;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Settings.Load();
        Telemetry = new NoopTelemetryService(Settings);
        Telemetry.Track("app_launch", new Dictionary<string, object?>
        {
            ["shellVersion"] = Core.ShellConfig.ShellVersion,
            ["osBuild"] = Environment.OSVersion.Version.Build
        });

        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            Telemetry.TrackException(e.Exception, "xaml_unhandled");
            var dir = SettingsStore.Dir;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {e.Message}\n{e.Exception}\n\n");
        }
        catch { /* 记录失败也不影响 */ }

        // 开发期不要静默退出，方便定位；发布后可改为 e.Handled = true 做兜底
        e.Handled = false;
    }
}
