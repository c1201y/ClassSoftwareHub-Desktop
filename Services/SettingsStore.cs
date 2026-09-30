using System.Text.Json;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 侧边栏展开面板**底部那五个按钮**的稳定 key（数组顺序 = 显示顺序，跟侧边栏上排的先后一致）。
/// ⚠️ 这是**存档格式**的一部分（<see cref="AppSettings.SidebarFooterHidden"/> 按它记账）——
///    改名 / 换字面量等于让所有用户已经拨好的开关状态全部失效，别动。
/// </summary>
public static class SidebarFooterKeys
{
    public const string Fold = "fold";          // 收起
    public const string Pin = "pin";            // 常驻
    public const string Reset = "reset";        // 位置复原
    public const string Hide = "hide";          // 隐藏
    public const string OpenApp = "openapp";    // 打开应用

    public static readonly string[] All = { Fold, Pin, Reset, Hide, OpenApp };
}

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

    /// <summary>
    /// 「收进托盘」的提示气泡是否已经出现过了（只提示一次）。
    ///
    /// 为什么要有：2026-09-30 用户反馈"把软件关掉之后点桌面图标打不开，三四次没反应，只能去任务管理器"，
    /// 根子就是收进托盘后用户不知道程序还在后台跑。托盘图标一旦被系统折进溢出区，更是一点线索都没有。
    /// </summary>
    public bool TrayHideHintShown { get; set; }

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

    /// <summary>
    /// **贴靠模式**下侧边栏贴哪条边：<c>left</c> | <c>right</c>，外加 <c>both</c>
    /// （= 左右两条同时显示，共用 <see cref="SidebarPosRatio"/> 所以上下位置天然联动）。
    /// ⚠️ 贴靠模式**只有左右两条边**（Nick 2026-09-28：贴靠 = 现在的左右模式）。
    ///    上/下边归属自由模式，见 <see cref="SidebarFreeEdge"/>。
    /// ⚠️ <c>both</c> 只是**设置层**的值：运行时会拆成 left + right 两个 ToolSidebarWindow 实例
    /// （见 <c>ToolSidebarWindow.SyncInstances</c>），每个实例自己的边存私有字段，不读这个。
    /// </summary>
    public string SidebarEdge { get; set; } = "right";

    /// <summary>
    /// 侧边栏的行为模式（2026-09-28 Nick 提，当天再澄清过一次）：
    /// <list type="bullet">
    ///   <item><c>dock</c>（默认）= 贴靠模式：只吸屏幕的**左右两条边**，可选「左右两边」同时显示两条，
    ///         沿边位置存 <see cref="SidebarPosRatio"/>。</item>
    ///   <item><c>free</c> = 自由模式：**四条边都能吸**（<c>left</c> / <c>right</c> / <c>top</c> / <c>bottom</c>，
    ///         贴上/下边时是横条），贴哪条边存 <see cref="SidebarFreeEdge"/>。
    ///         ⚠️ 它**不是**"浮在屏幕中间"—— 侧边栏永远吸在某条边上，只是可选范围比贴靠模式大。</item>
    /// </list>
    /// ⚠️ 认不出的值一律当 <c>dock</c>（见 <c>ToolSidebarWindow.IsFreeMode</c>）。
    /// </summary>
    public string SidebarMode { get; set; } = "dock";

    /// <summary>侧边栏沿边位置（0~1）；-1 = 居中（默认）。拖动收起状态的抓手时记下来。两种模式共用。</summary>
    public double SidebarPosRatio { get; set; } = -1;

    /// <summary>
    /// **自由模式**下侧边栏贴哪条边：<c>left</c> | <c>right</c> | <c>top</c> | <c>bottom</c>（默认 right）。
    /// ⚠️ 只有一个值 —— 自由模式不提供「左右两边」，要双边请用贴靠模式。
    /// </summary>
    public string SidebarFreeEdge { get; set; } = "right";

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
    /// 侧边栏展开面板**底部那一排按钮**（收起 / 常驻 / 位置复原 / 隐藏 / 打开应用）里
    /// **被关掉**的那几颗，key 见 <see cref="SidebarFooterKeys"/>。空数组 = 五颗全显示（默认）。
    /// 在「侧边布局」页底部**逐颗**切换；关掉任意一颗后侧边栏按剩余内容收缩
    /// （竖条变矮、横条变窄 —— 见 <c>ToolSidebarWindow.PlannedSize</c>）。
    /// ⚠️ 记的是"关掉的"而不是"打开的"：这样以后往底排**加新按钮**时，老用户默认能看见它
    ///    （记"打开的"就得为每台老存档补一次迁移，漏了就是"新按钮永远不出现"）。
    /// </summary>
    public string[] SidebarFooterHidden { get; set; } = Array.Empty<string>();

    /// <summary>
    /// ⚠️ **已弃用**（2026-09-29 从"一个总开关"改成"五颗各自一个开关"）：
    /// 老存档里 <c>false</c> = 五颗全隐藏。现在只用来做**一次性迁移**
    /// （见 <c>SettingsStore.MigrateSidebarFooter</c>，迁完就把它拨回 true 当"已迁"标记）。
    /// 留给老存档是为了不让用户"我明明全关了"这个状态在升级后丢掉。
    /// </summary>
    public bool SidebarFooterEnabled { get; set; } = true;

    /// <summary>
    /// 侧边栏「新模块补入」的批次号。
    /// 设置里存的是一份**用户自己勾选好的**模块清单，光在代码里加新模块它不会自己冒出来，
    /// 于是升级后用户会发现"我要的东西没在侧边栏上"。靠这个标记做**一次性**补入
    /// （见 <see cref="SettingsStore.EnsureNewSidebarModules"/>）。
    /// ⚠️ 有它才能保证「用户主动删掉的模块不会被下次启动又塞回来」。
    /// </summary>
    public int SidebarModulesRevision { get; set; }

    // ── 虚拟键盘（实验性功能，2026-10-01 从零重写那版）──────────
    // ⚠️ 总开关默认**关**：它要装全局触摸钩子、跑 UIA 探测，属于"用户明确想要才开"的功能，
    //    不能替所有人默认打开。关着的时候整套东西一行系统 API 都不会碰。

    /// <summary>启用虚拟键盘（会装触摸监听 + 落点判定）。</summary>
    public bool VirtualKeyboardEnabled { get; set; }

    /// <summary>
    /// 接管系统触摸键盘：写 <c>HKCU\...\TabletTip\1.7\EnableDesktopModeAutoInvoke = 0</c>，
    /// 让系统那个别在桌面模式下自动弹。
    /// ⛔ **默认关** —— 它是这套功能里**唯一会动注册表**的地方，得用户显式同意；
    ///    开启前会自动备份原值，关掉功能或取消勾选时原样还原。
    /// </summary>
    public bool VirtualKeyboardCaptureSystem { get; set; }

    /// <summary>布局模式：<c>full</c>（完整，**默认**）/ <c>compact</c>（精简）。见 <c>Data.KeyboardLayouts</c>。</summary>
    public string VirtualKeyboardMode { get; set; } = Data.KeyboardLayouts.ModeFull;

    /// <summary>当前层：letters / symbols / numpad。</summary>
    public string VirtualKeyboardLayer { get; set; } = Data.KeyboardLayouts.Letters;

    /// <summary>键帽缩放（0.7 ~ 1.6），连续可调。</summary>
    public double VirtualKeyboardScale { get; set; } = 1.0;

    /// <summary>键盘宽度占工作区的比例（0.35 ~ 1.0）。</summary>
    public double VirtualKeyboardWidthRatio { get; set; } = 1.0;

    /// <summary>键帽文字缩放（0.8 ~ 1.3）。</summary>
    public double VirtualKeyboardFontScale { get; set; } = 1.0;

    /// <summary>键盘底色：<c>system</c>（跟随系统）/ <c>light</c> / <c>dark</c>。颜色本体一律走主题画刷。</summary>
    public string VirtualKeyboardTheme { get; set; } = "system";

    /// <summary>排列方式：true = 自由悬浮（位置记在下面两个），false = 贴屏幕底边。</summary>
    public bool VirtualKeyboardFloating { get; set; }

    /// <summary>悬浮时的窗口左上角（物理像素）；负值 = 还没摆过，按贴底算。</summary>
    public double VirtualKeyboardFloatX { get; set; } = -1;
    public double VirtualKeyboardFloatY { get; set; } = -1;

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
        if (!_loadFailed)
        {
            EnsureNewSidebarModules();
            MigrateSidebarFooter();
        }
    }

    /// <summary>
    /// 一次性迁移：老存档只有一个总开关（<see cref="AppSettings.SidebarFooterEnabled"/>），
    /// <c>false</c> = 底排五颗全隐藏。改成"五颗各自一个开关"后，把这个状态搬进
    /// <see cref="AppSettings.SidebarFooterHidden"/>。
    /// ⚠️ 迁完把总开关拨回 <c>true</c> 当"已迁"标记 —— 否则每次启动都会重迁一遍，
    ///    把用户后来打开的那几颗又按回去。
    /// </summary>
    private void MigrateSidebarFooter()
    {
        if (Current.SidebarFooterEnabled) return;      // 开着 = 五颗全显示 = 新默认值，无事可做

        var hidden = (Current.SidebarFooterHidden ?? Array.Empty<string>()).ToList();
        foreach (var key in SidebarFooterKeys.All)
            if (!hidden.Contains(key)) hidden.Add(key);

        Current.SidebarFooterHidden = hidden.ToArray();
        Current.SidebarFooterEnabled = true;           // 已迁
        Save();
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

        if (Current.SidebarModulesRevision < 3)
        {
            // revision 3（虚拟键盘，2026-10-01）：放在讲台动作类最前面 —— 键盘是上课最常用的那一下。
            try
            {
                var list = (Current.SidebarModuleIds ?? Array.Empty<string>()).ToList();

                if (!list.Contains("keyboard"))
                {
                    var at = list.FindIndex(id => Data.SidebarModules.Find(id)?.Kind == Data.SidebarModuleKinds.Action);
                    if (at < 0) list.Add("keyboard");
                    else list.Insert(at, "keyboard");
                    Current.SidebarModuleIds = list.ToArray();
                }
            }
            catch (Exception ex)
            {
                Log($"补侧边栏新模块失败: {ex.Message}");
            }

            Current.SidebarModulesRevision = 3;
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
