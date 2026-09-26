using System.Text.Json;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>可持久化的用户设置。</summary>
public sealed class AppSettings
{
    public string Backdrop { get; set; } = "acrylic";          // acrylic | mica | solid
    public string Theme { get; set; } = "system";              // system | light | dark
    /// <summary>分体：外部组件（侧边栏 / 常用工具浮窗 / 截图编辑窗）用**单独**的外观设置。</summary>
    public bool SplitTheme { get; set; }
    /// <summary>外部组件的外观（system | light | dark）；只有 <see cref="SplitTheme"/> 打开时才起作用。</summary>
    public string ExternalTheme { get; set; } = "system";

    public bool AlwaysOnTop { get; set; }
    public bool AutoStart { get; set; }
    /// <summary>开机启动时直接最小化（只有 AutoStart = true 时才有意义）。</summary>
    public bool MinimizeOnStart { get; set; }
    public bool TelemetryEnabled { get; set; }                 // 默认关闭
    public bool TelemetryAsked { get; set; }

    /// <summary>点关闭 = 收进托盘（不退出程序），默认开；关了就是以前那样直接退出。</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>工具浮窗是否始终置顶，默认开。</summary>
    public bool PaletteOnTop { get; set; } = true;

    /// <summary>工具浮窗上次的位置（物理像素）；-99999 = 还没存过（那就默认右下角）。</summary>
    public int PaletteX { get; set; } = -99999;
    public int PaletteY { get; set; } = -99999;

    /// <summary>
    /// 位置记忆的版本：老版本（没有这个字段 = 0）存在右下角的旧坐标会被忽略一次，
    /// 让浮窗按新默认值（屏幕正中间）摆一次。用户拖过之后就一直是他的位置了。
    /// </summary>
    public int PalettePosVersion { get; set; }

    /// <summary>工具浮窗上次停在哪个工具：pick-number | timer | clock</summary>
    public string PaletteTool { get; set; } = "pick-number";

    /// <summary>屏幕右边那条工具侧边栏要不要显示（默认开：全屏播放时也能点到工具）。</summary>
    public bool SidebarEnabled { get; set; } = true;

    /// <summary>侧边栏贴哪条边：left | right | top | bottom（默认右边；拖一下也能换，换完记这儿）。</summary>
    public string SidebarEdge { get; set; } = "right";

    /// <summary>侧边栏沿边位置（0~1）；-1 = 居中（默认）。拖动收起状态的抓手时记下来。</summary>
    public double SidebarPosRatio { get; set; } = -1;

    /// <summary>
    /// 常驻：展开之后**不自动收起**（鼠标移开不收、切窗口不收、也不会因为 10 秒没动就收）。
    /// 手动点「收起」照样能收。
    /// </summary>
    public bool SidebarPinned { get; set; }

    /// <summary>
    /// 侧边栏里显示哪些模块（顺序 = 显示顺序），见 Data/SidebarModules.All —— 在「侧边布局」页里勾选/排序。
    /// 空数组 = 只留底下那排自己的按钮（收起 / 位置复原 / 隐藏）。
    /// </summary>
    public string[] SidebarModuleIds { get; set; } = { "pick-number", "timer", "stopwatch", "clock" };

    /// <summary>
    /// 侧边栏「新模块补入」的批次号。
    /// 设置里存的是一份**用户自己勾选好的**模块清单，光在代码里加新模块它不会自己冒出来，
    /// 于是升级后用户会发现"我要的东西没在侧边栏上"。靠这个标记做**一次性**补入
    /// （见 <see cref="SettingsStore.EnsureNewSidebarModules"/>）。
    /// ⚠️ 有它才能保证「用户主动删掉的模块不会被下次启动又塞回来」。
    /// </summary>
    public int SidebarModulesRevision { get; set; }

    public string WebView2MissingChoice { get; set; } = "";    // "" | install | browser

    /// <summary>截图后自动存一份原图（默认开）。目录见 ShotSaveDir，空 = 桌面。</summary>
    public bool ShotAutoSave { get; set; } = true;

    /// <summary>截图自动保存目录；空 = 系统桌面。</summary>
    public string ShotSaveDir { get; set; } = "";

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

    /// <summary>
    /// 上次 Load 是不是因为「非 JSON 问题」失败了（多半是文件被临时占用 / 权限）。
    ///
    /// 为什么要记这一笔：Load 失败时内存里是默认值，若放任下一次 Save() 写盘，
    /// 就会把用户**完好的**旧设置永久覆盖掉 —— 而且用户只是想切个主题而已。
    /// 所以失败之后，Save() 会先尝试把文件读回来，读得到才允许写。
    /// </summary>
    private bool _loadFailed;

    public void Load()
    {
        try
        {
            var json = ReadTolerant();
            if (json is not null)
                Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            _loadFailed = false;
        }
        catch (JsonException)
        {
            // 只有「内容真的坏了」才退回默认值。坏文件先留一份 .bad ——
            // 用户可能想找回里面的设置，直接覆盖就再也拿不回来了。
            KeepCorruptCopy();
            Current = new AppSettings();
            _loadFailed = false;
        }
        catch (Exception ex)
        {
            // IO / 权限类异常：**不动**内存里的值，也标记住不让后面盲目写盘
            _loadFailed = true;
            Log($"读取设置失败，本次不覆盖原文件: {ex.Message}");
        }

        // 用户没自己挑过通道时，通道跟着「这个安装包是哪条线」走
        // （预览版安装包默认收预发布，正式版安装包默认收 Latest；老 settings.json 从这里也能纠正过来）
        if (!Current.UpdateChannelSetByUser)
            Current.UpdateChannel = Core.ShellConfig.DefaultUpdateChannel;

        // 读盘失败时内存里是默认值，这时候别去动用户文件（Save 自己也会拦一道）
        if (!_loadFailed) EnsureNewSidebarModules();
    }

    /// <summary>
    /// 把「后来才加进侧边栏的模块」补给老用户，每批只补一次（用 <see cref="AppSettings.SidebarModulesRevision"/> 记账）。
    ///   · revision 1（音量调节）：插在讲台动作类前面（工具 → 音量 → 动作），不打断用户已经排好的顺序；
    ///   · revision 2（屏幕亮度，2026-09-26）：紧挨着音量后面放（这俩是一对儿），
    ///     用户如果自己把音量删了，就还是插在动作类前面。
    /// </summary>
    private void EnsureNewSidebarModules()
    {
        var changed = false;

        if (Current.SidebarModulesRevision < 1)
        {
            try
            {
                var list = (Current.SidebarModuleIds ?? Array.Empty<string>()).ToList();

                if (!list.Contains("volume"))
                {
                    var at = list.FindIndex(id => Data.SidebarModules.Find(id)?.Kind == Data.SidebarModuleKinds.Action);
                    if (at < 0) list.Add("volume");
                    else list.Insert(at, "volume");
                    Current.SidebarModuleIds = list.ToArray();
                }
            }
            catch (Exception ex)
            {
                Log($"补侧边栏新模块失败: {ex.Message}");
            }

            Current.SidebarModulesRevision = 1;
            changed = true;
        }

        if (Current.SidebarModulesRevision < 2)
        {
            try
            {
                var list = (Current.SidebarModuleIds ?? Array.Empty<string>()).ToList();

                if (!list.Contains("brightness"))
                {
                    var at = list.IndexOf("volume");                       // 有音量就跟它并排
                    if (at >= 0) list.Insert(at + 1, "brightness");
                    else
                    {
                        at = list.FindIndex(id => Data.SidebarModules.Find(id)?.Kind == Data.SidebarModuleKinds.Action);
                        if (at < 0) list.Add("brightness");
                        else list.Insert(at, "brightness");
                    }
                    Current.SidebarModuleIds = list.ToArray();
                }
            }
            catch (Exception ex)
            {
                Log($"补侧边栏新模块失败: {ex.Message}");
            }

            Current.SidebarModulesRevision = 2;
            changed = true;
        }

        // 标记必须落盘，否则下次启动又会"补"一遍，用户删了也白删
        if (changed) Save();
    }

    /// <summary>
    /// 宽容读取：用 FileShare.ReadWrite 打开。
    /// 默认的 File.ReadAllText 只给 FileShare.Read，安全软件正在扫这个文件时会直接抛异常，
    /// 然后就被当成"设置坏了"退回默认值 —— 这个失败太容易发生了。
    /// </summary>
    private string? ReadTolerant()
    {
        if (!File.Exists(FilePath)) return null;

        using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    public void Save()
    {
        try
        {
            // 上次没读成功：写盘前再试一次。读得到说明文件其实是好的，先用它覆盖内存，
            // 免得把用户的旧设置冲掉；还是读不到就这轮先不写。
            if (_loadFailed)
            {
                try
                {
                    var existing = ReadTolerant();
                    if (existing is not null)
                    {
                        Current = JsonSerializer.Deserialize<AppSettings>(existing, JsonOpts) ?? Current;
                        _loadFailed = false;
                    }
                }
                catch
                {
                    Log("设置文件仍然读不到，跳过本次保存");
                    return;
                }
            }

            Directory.CreateDirectory(Dir);

            // 原子替换：先写临时文件，再整体换过去。
            // 直接 File.WriteAllText 会**先截断原文件**，写到一半断电 / 崩溃就留下半截 JSON，
            // 下次启动直接解析失败、设置全丢 —— 这个窗口必须堵掉。
            var json = JsonSerializer.Serialize(Current, JsonOpts);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);

            if (File.Exists(FilePath))
            {
                // File.Replace 是原子的，并且会顺手留一份 .bak（出事了还能人工找回）
                File.Replace(tmp, FilePath, FilePath + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tmp, FilePath);
            }
        }
        catch (Exception ex)
        {
            // 设置写失败不影响主流程
            Log("保存设置失败: " + ex.Message);
        }
    }

    private void KeepCorruptCopy()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Copy(FilePath, FilePath + ".bad", overwrite: true);
        }
        catch
        {
            // 留不下副本也不能影响启动
        }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(Path.Combine(Dir, "settings.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch
        {
            // 日志写不进去就算了
        }
    }
}
