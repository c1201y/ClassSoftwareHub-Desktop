using System.Text.Json;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>可持久化的用户设置。</summary>
public sealed class AppSettings
{
    public string Backdrop { get; set; } = "acrylic";          // acrylic | mica | solid
    public string Theme { get; set; } = "system";              // system | light | dark
    public bool AlwaysOnTop { get; set; }
    public bool AutoStart { get; set; }
    /// <summary>开机启动时直接最小化（只有 AutoStart = true 时才有意义）。</summary>
    public bool MinimizeOnStart { get; set; }
    public bool TelemetryEnabled { get; set; }                 // 默认关闭
    public bool TelemetryAsked { get; set; }
    public string WebView2MissingChoice { get; set; } = "";    // "" | install | browser

    /// <summary>更新通道：stable（正式版）| insider（预览版）。默认值跟着构建走（见 ShellConfig.DefaultUpdateChannel）。</summary>
    public string UpdateChannel { get; set; } = Core.ShellConfig.DefaultUpdateChannel;

    /// <summary>用户是否在设置页亲手选过通道（没选过就按构建默认，选过就完全听用户的）。</summary>
    public bool UpdateChannelSetByUser { get; set; }

    /// <summary>上次检查更新的时间（Unix 秒；0 = 没检查过）。</summary>
    public long LastUpdateCheck { get; set; }

    /// <summary>跳过/忽略的版本（不再提示这个版本）。</summary>
    public string SkipUpdateVersion { get; set; } = "";

    /// <summary>自动检查更新（启动时静默查一次，默认开）。</summary>
    public bool AutoCheckUpdate { get; set; } = true;

    /// <summary>软件下载页的卡片视图：tile（磁贴，3 列带简介）| grid（网格，5 列紧凑）。</summary>
    public string AppCardView { get; set; } = "tile";
    public bool WebTransparent { get; set; } = true;
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 820;
    public double WebZoom { get; set; } = 1.0;
    public string LastUrl { get; set; } = "";

    /// <summary>站点上报的顶栏底色（#RRGGBB），用于纯色兜底时的对齐。</summary>
    public string TitleBarTint { get; set; } = "";

    /// <summary>
    /// true = 把网页上报的标题栏拖动区注册成原生 caption 区域（手感更跟手，但拖动不经过桥接消息）；
    /// false = 按规格书走「网页判断 → 桥接消息 → 原生拖动」。
    /// </summary>
    public bool NativeCaptionRegions { get; set; } = false;
}

/// <summary>设置存储：%LOCALAPPDATA%\ClassSoftwareHub\settings.json</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassSoftwareHub");

    public string FilePath { get; } = Path.Combine(Dir, "settings.json");

    public AppSettings Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            }
        }
        catch
        {
            Current = new AppSettings();
        }

        // 用户没自己挑过通道时，通道跟着「这个安装包是哪条线」走
        // （预览版安装包默认收预发布，正式版安装包默认收 Latest；老 settings.json 从这里也能纠正过来）
        if (!Current.UpdateChannelSetByUser)
            Current.UpdateChannel = Core.ShellConfig.DefaultUpdateChannel;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonOpts));
        }
        catch
        {
            // 设置写失败不影响主流程
        }
    }
}
