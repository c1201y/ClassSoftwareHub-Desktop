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
    /// <summary>
    /// 窗口背景材质：<c>mica</c> | <c>acrylic</c> | <c>solid</c>。
    /// 2026-10-01 默认从 acrylic 改成 mica：Mica 只采样一次壁纸、几乎零每帧成本，
    /// 而亚克力要实时模糊窗口背后，滚动/拖动时是白交的 GPU 税；且 Mica 本就是
    /// Windows 11 系统窗口（设置、资源管理器）用的材质，更贴"要 Windows 原生"这条铁律。
    /// Windows 10 上 Mica 不可用，ApplyBackdrop 会自动退到亚克力，不用在这儿判。
    /// ⚠️ 这只影响<B>没存过设置</B>的新用户；老用户存档里存的是自己的选择，不会被动改。
    /// </summary>
    public string Backdrop { get; set; } = "mica";         // mica | acrylic | solid
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

    /// <summary>
    /// 侧边布局页里拖放卡片的**落位收尾**效果（2026-10-01 Nick：两种手感都挺好，索性都给用户自己挑）：
    /// <list type="bullet">
    ///   <item><c>plain</c> = 平滑收势：落位后直接把"提起来"的装饰（蓝描边 / 投影 / 0.95 透明度）收掉就完事。</item>
    ///   <item><c>dip</c>（默认）= 轻落一下：收势之前替身先轻微下沉一次再复位，落地感更实。</item>
    /// </list>
    /// ⚠️ 认不出的值一律当 <c>dip</c>（见 <c>SidebarLayoutPage.DropDipScale</c>）。
    /// ⚠️ 只管**侧边布局页里拖放动画**的手感，跟真侧边栏的行为无关，改了不用通知任何窗口。
    /// </summary>
    public string SidebarDropAnim { get; set; } = "dip";

    /// <summary>
    /// 沿边位置比例（0~1，-1 = 居中）。
    ///
    /// 历史：2026-10-19 曾一度废弃（说它与「开机最小化」冲突）、10-22 又明确"不落盘"；
    /// **2026-10-24 按 Nick 要求恢复**——他反馈"每次重启都跑回右边居中，明明调过位置"。
    /// 现在的写入时机只有一处：<c>ToolSidebarWindow.DockToNearestEdge()</c>（松手那一下），
    /// 拖动过程中绝不写盘 —— 当初出问题正是拖动时反复 Save（卡顿 + 把启动早期的中间尺寸当成最终位置）。
    /// </summary>
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
    /// 学科工具窗上次停在哪个工具（<c>function-plot</c> / <c>equation</c> / <c>chemistry</c>）。
    /// 侧边栏那颗「学科」只传 null，窗口就按这个值恢复 —— 上次算化学，下次点开还是化学。
    /// 认不出的值由 <c>SubjectToolsWindow.Normalize</c> 兜回第一件，不用在这里校验。
    /// </summary>
    public string SubjectToolsLastTool { get; set; } = "function-plot";

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
    /// 侧边栏**收起条**的大小样式（2026-10-11 Nick：有用户嫌默认那套悬浮按钮太大）：
    /// <list type="bullet">
    ///   <item><c>standard</c>（默认）= 现在这套：抓手 20×110，展开后面板宽 92 dip，按钮 52 高、图标 20、图标下面带短名。</item>
    ///   <item><c>compact</c> = 紧凑：细条 + 小图标小字，但**按钮本身的点击区域不跟着缩到底**
    ///         （否则细条上的键点不准，2026-10-11 Nick 特意提的）。</item>
    /// </list>
    /// ⚠️ 认不出的值一律当 <c>standard</c>（见 <c>ToolSidebarWindow.CompactCollapsed</c>）。
    ///
    /// ⚠️ 2026-10-12 起这项**只决定收起态**（那条小抓手），展开面板改由
    ///    <see cref="SidebarExpandedDensity"/> 决定 —— 有用户要「收起时紧凑省地方、展开后仍用标准」。
    ///    它的默认值 <c>same</c> 会让展开面板继续跟随本项，所以老用户升上来观感不变。
    /// </summary>
    public string SidebarDensity { get; set; } = "standard";

    /// <summary>
    /// 侧边栏**展开后**（面板态）的大小样式（2026-10-12 Nick：有用户要「收起紧凑、展开标准」）：
    /// <list type="bullet">
    ///   <item><c>same</c>（默认）= 跟随 <see cref="SidebarDensity"/>，上下两态一致 —— 2026-10-12 之前的唯一行为。</item>
    ///   <item><c>standard</c> / <c>compact</c> = 无论收起态怎么设，展开面板固定用这一套。</item>
    /// </list>
    /// ⚠️ 只认这三个值，别的（含 <c>null</c>）一律当 <c>same</c>（见 <c>ToolSidebarWindow.CompactBody</c>）。
    /// </summary>
    public string SidebarExpandedDensity { get; set; } = "same";

    /// <summary>
    /// 侧边栏工具区**每行摆几个**（2026-10-11 Nick：单列排下来太占地方，想要能折行）：
    /// <c>1</c>（默认）= 单列竖着排；<c>3</c> = 每行三个、满了自动折行。
    /// ⚠️ 只对**贴左右两边**（竖条）生效 —— 贴上/下边是横条，本来就是横向排一排，这个值不参与。
    /// ⚠️ 只认 1 / 3，别的值一律当 1（见 <c>ToolSidebarWindow.Columns</c>）。
    /// </summary>
    public int SidebarColumns { get; set; } = 1;

    /// <summary>
    /// 侧边栏**收起**时那条小抓手离屏幕边缘留多少 dip（2026-10-11 Nick：有用户不喜欢那道缝）。
    /// <c>0</c> = 紧贴屏幕边缘；默认 <c>6</c>（与 <c>ToolSidebarWindow.CollapsedGapDip</c> 的默认值一致）。
    ///
    /// ⚠️ 只在**收起态**生效 —— 展开态的面板始终紧贴边缘（面板悬空一条缝很难看），这是刻意的行为差异。
    /// ⚠️ 值会被夹到 0~24（见 <c>ToolSidebarWindow.CollapsedGapDip</c>）：存档里万一存了离谱的数，
    ///    边条会被推到屏幕中间去，那就不是"贴边"了。
    /// </summary>
    public int SidebarCollapsedGap { get; set; } = 6;

    /// <summary>
    /// 侧边栏展开面板的高度上限占工作区高度的几成（2026-10-18 Nick：别再固定调死，交给用户自己拖）。
    /// 取值 <c>0.45</c>～<c>0.95</c>，默认 <c>0.75</c>。对应「侧边布局 → 展开面板高度」那根滑块。
    ///
    /// ⚠️ 它只是**封顶**，不是"给个固定高度"：面板仍按内容实测，模块少时该多矮就多矮
    ///    （见 <c>ToolSidebarWindow.ExpandedHeight</c> / <c>ApplyScrollLimit</c>）。
    /// ⚠️ 只在**单列**（「工具排列模式 → 紧凑模式」）生效；每行三个时行数天然少、不受这一项约束。
    /// ⚠️ 用的人自行夹取（<c>ToolSidebarWindow.SingleColumnMaxHeightRatio</c>），存档里出现怪值也不会撑爆屏幕。
    /// </summary>
    public double SidebarHeightRatio { get; set; } = 0.75;

    /// <summary>
    /// 展开面板的高度是否**跟随内容**（2026-10-18 Nick 要，默认开）。
    /// · <c>true</c>（自适应）= 高度由里面模块的数量实测决定，<see cref="SidebarHeightRatio"/> 只当**上限**；
    ///   只有两个模块时面板就矮矮的，下面不留白。
    /// · <c>false</c>（锁定）= 高度固定为 <see cref="SidebarHeightRatio"/> × 工作区高度，
    ///   模块少时下面会留白。两者用「侧边布局 → 自适应高度」切换。
    /// ⚠️ 都只在单列（紧凑模式）下有意义；横条是固定厚度，不参与。
    /// </summary>
    public bool SidebarHeightAuto { get; set; } = true;

    /// <summary>
    /// 侧边栏「关机」按钮的关机方式（2026-10-18 Nick 要）：
    /// <list type="bullet">
    ///   <item><c>slide</c>（**默认**）= 手动关机：调系统的 <c>SlideToShutDown.exe</c>，弹出「下滑关机」界面，
    ///         下滑滑块才真正关机；不滑就一直停着，本身即确认步骤。</item>
    ///   <item><c>shutdown</c> = 直接关机：直接执行 <c>shutdown /s /f /t 0</c>，立即关闭计算机。</item>
    /// </list>
    /// ⚠️ 认不出的值一律当 <c>slide</c>（见 <c>TeachingActions</c> 的归一化）。
    /// </summary>
    public string ShutdownMode { get; set; } = "slide";

    /// <summary>
    /// Hub AI 的**总闸**（2026-10-18）。
    ///
    /// <para>
    /// 闸门关闭后的表现（Nick 14:00 定）：
    /// <list type="bullet">
    ///   <item>软件下载页的**入口卡隐藏**（不占位、不留灰块）；</item>
    ///   <item>导航里的「Hub AI」**保留**，点进去给一张"去启用"的引导，而不是直接报错；</item>
    ///   <item>已经填过的配置全部保留。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 默认 <c>true</c>：先让人看见有这么个东西，再用引导带他去配置。
    /// ⚠️ 模型服务商 / 模型名 / Base URL / 参数这些**不放这里**：密钥要单独走 DPAPI 加密存文件，
    /// 其余配置等二级设置页做的时候另开一组字段，⛔ 别把密钥塞进 settings.json。
    /// </para>
    /// </summary>
    public bool HubAiEnabled { get; set; } = true;

    /// <summary>
    /// 模型服务商预设 id（见 <c>AiProviders</c>）：deepseek / tongyi / zhipu / openai / local / custom。
    /// ⚠️ 只是"预设"不是"事实" —— 用户改过服务地址后，这里仍然记着当初选的预设，
    /// 界面按 <see cref="HubAiBaseUrl"/> 的实际值显示，别拿预设去覆盖用户填的地址。
    /// </summary>
    public string HubAiProvider { get; set; } = "deepseek";

    /// <summary>兼容 OpenAI 协议的服务根地址，例如 <c>https://api.deepseek.com/v1</c>。</summary>
    public string HubAiBaseUrl { get; set; } = "https://api.deepseek.com/v1";

    /// <summary>模型名。<see cref="HubAiProvider"/> 换了预设时由界面带出默认值，用户可改。</summary>
    public string HubAiModel { get; set; } = "deepseek-v4-flash";

    /// <summary>
    /// 是否开启思考模式（2026-10-18）。默认开 —— 主流思考模型现在都默认开，关掉反而要额外解释。
    /// ⚠️ 各家写法不同（<c>thinking.type</c> / <c>enable_thinking</c> / 仅 <c>reasoning_effort</c>），
    /// 由 <c>AiClient.ApplyThinking</c> 按服务商翻译，⛔ 别在这里存"最终发给服务商的字段"。
    /// </summary>
    public bool HubAiThinking { get; set; } = true;

    /// <summary>思考强度：<c>low</c> / <c>high</c> / <c>max</c>。服务商不支持时会被忽略。</summary>
    public string HubAiEffort { get; set; } = "high";

    /// <summary>
    /// 用户希望 AI 怎么称呼自己（2026-10-18）。空 = 未设置，AI 就用「你」。
    /// ⚠️ 只进 prompt，**不进日志**、不参与统计。
    /// </summary>
    public string UserDisplayName { get; set; } = "";

    /// <summary>
    /// 已配置的模型列表（2026-10-18）。同一个服务商可以配多条。
    /// ⚠️ 上面的 <c>HubAiProvider/HubAiBaseUrl/HubAiModel/HubAiThinking/HubAiEffort</c> 现在只是
    /// **当前那条的镜像**，供尚未改造的代码读取；真正的数据源是这里 + <see cref="HubAiActiveModelId"/>。
    /// </summary>
    public System.Collections.Generic.List<HubAi.AiModelEntry> HubAiModels { get; set; } = new();

    /// <summary>当前使用哪一条模型（对应 <see cref="HubAiModels"/> 里的 Id）。</summary>
    public string? HubAiActiveModelId { get; set; }

    /// <summary>Hub AI 左侧历史栏是否收起（2026-10-19）。</summary>
    public bool HubAiHistoryCollapsed { get; set; }

    /// <summary>
    /// Hub AI 是否允许联网检索（2026-10-20，输入框上的「联网」开关）。
    /// ⚠️ 只有开启且工具总闸也开着时，搜索工具才会交给模型。
    /// </summary>
    public bool HubAiWebEnabled { get; set; }

    /// <summary>
    /// Hub AI **上次停留的那个会话 id**（空 = 新对话）。
    /// 2026-10-23 加：Nick 报「对话到一半切到别的页面，回来对话被吞了」。
    /// 页面虽然是 NavigationCacheMode=Required，但一旦因为任何原因被重建（缓存被顶掉 / 导航栈清理），
    /// 内存里的 _chat 与当前会话 id 全丢。把 id 落到存档，重建后能自动选回那一段。
    /// </summary>
    public string HubAiLastSessionId { get; set; } = "";

    /// <summary>
    /// 是否启用 AI 工具包（2026-10-18）。
    ///
    /// <para>
    /// 工具包是**可选安装**：不装就不占空间，装了才有"查磁盘明细 / 检索软件库"这些能力。
    /// ⚠️ 开关关掉只是不把工具交给模型，**不删已下载的包** —— 用户可能只是想暂时不用，
    /// 下次打开不该再等一次下载。真要删走「移除」按钮。
    /// </para>
    /// </summary>
    public bool HubAiToolsEnabled { get; set; }

    /// <summary>
    /// 侧边栏「新模块补入」的批次号。
    /// 设置里存的是一份**用户自己勾选好的**模块清单，光在代码里加新模块它不会自己冒出来，
    /// 于是升级后用户会发现"我要的东西没在侧边栏上"。靠这个标记做**一次性**补入
    /// （见 <see cref="SettingsStore.EnsureNewSidebarModules"/>）。
    /// ⚠️ 有它才能保证「用户主动删掉的模块不会被下次启动又塞回来」。
    /// </summary>
    public int SidebarModulesRevision { get; set; }

    // ⚠️ 这里原本还有 11 项虚拟键盘设置（VirtualKeyboardEnabled / 布局 / 缩放 / 悬浮坐标……）。
    //    该功能 2026-10-09 整体下线，字段一并删掉 —— 老存档里的这些键会被 JsonSerializer
    //    静默忽略（JsonOpts 没开严格模式），读取不受影响。

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

    /// <summary>
    /// 本地安装包保留数量（1~10，默认 3）：启动时自动清理 <c>updates</c> 目录，
    /// 按修改时间从新到旧留 N 个、其余删掉。留着的包给「装回旧版本」用 —— 本地有就不再下载。
    /// 使用处会自行夹取（见 InstallerCleanup.Clean），存档里不夹是为了老档里出现怪值时也不至于越界。
    /// </summary>
    public int InstallerKeepCount { get; set; } = 3;

    /// <summary>
    /// 素材缓存保留数量（1~10，默认 3）：启动时自动清理 <c>cache</c> 下「按版本分的子目录」，
    /// 按目录名里的时间从新到旧留 N 个、其余删掉。**当前正在用的那个版本目录永不删**。
    /// 使用处会自行夹取（见 CacheHome.Clean），存档里不夹是为了老档出现怪值时不至于越界。
    /// </summary>
    public int CacheKeepCount { get; set; } = 3;

    /// <summary>
    /// 日志保留天数（默认 7；<c>0</c> = 永不删除）：启动时自动清理 <c>logs</c> 下
    /// 「启动时间早于 今天-N 天」的会话目录。**当前正在运行的那个会话目录永不删**。
    /// 会话化日志见 <see cref="LogHome"/>：一次启动一个目录，所以"过期"就是整目录一起收。
    /// </summary>
    public int LogKeepDays { get; set; } = 7;

    /// <summary>
    /// 后台下载完成的更新安装包绝对路径（空 = 没有）。下载校验通过就记下，
    /// 通知里的「稍后安装」和首页横幅都认它；装好新版本启动后由首页自检清掉。
    /// </summary>
    public string UpdatePendingPath { get; set; } = "";

    /// <summary>后台下载完成的版本号（配 UpdatePendingPath，仅供展示）。</summary>
    public string UpdatePendingTag { get; set; } = "";

    /// <summary>
    /// 安装或解压完成之后，自动删除下载下来的那个安装包 / 压缩包（**默认关闭**）。
    ///
    /// ⚠️ 只管「软件下载」页下下来的普通文件。应用自身的更新包不归它管 ——
    ///    更新包另有 InstallerKeepCount 那套保留策略（要留着"装回旧版本"）。
    /// ⚠️ 安装那条路的删除时机是「安装程序退出之后」：装的过程中删不动（文件正被读），
    ///    而且安装程序退出前我们也无从知道用户是装完了还是取消了。
    /// </summary>
    public bool AutoDeletePackage { get; set; }

    /// <summary>
    /// 解压完成之后自动打开产物文件夹（**默认开启**）。
    /// 解压本来就是为了去那个目录里拿东西，多一步手动打开纯属多余。
    /// </summary>
    public bool AutoOpenExtractFolder { get; set; } = true;

    /// <summary>软件下载页的卡片视图：tile（磁贴，3 列带简介）| grid（网格，5 列紧凑）。</summary>
    public string AppCardView { get; set; } = "tile";
    public bool WebTransparent { get; set; } = true;
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 820;

    /// <summary>还原态时窗口左上角的 X（-32768 = 没存过，交给系统摆）。</summary>
    public int WindowLeft { get; set; } = int.MinValue;

    /// <summary>还原态时窗口左上角的 Y（-32768 = 没存过，交给系统摆）。</summary>
    public int WindowTop { get; set; } = int.MinValue;

    /// <summary>关掉时是不是最大化（下一次启动照样铺满）。</summary>
    public bool WindowMaximized { get; set; }
    public double WebZoom { get; set; } = 1.0;
    public string LastUrl { get; set; } = "";

    /// <summary>站点上报的顶栏底色（#RRGGBB），用于纯色兜底时的对齐。</summary>
    public string TitleBarTint { get; set; } = "";

    /// <summary>
    /// true = 把网页上报的标题栏拖动区注册成原生 caption 区域（手感更跟手，但拖动不经过桥接消息）；
    /// false = 按规格书走「网页判断 → 桥接消息 → 原生拖动」。
    /// </summary>
    public bool NativeCaptionRegions { get; set; } = false;

    /// <summary>
    /// 课堂计时器到点的自定义铃声（完整路径；空 = 用内嵌的默认铃声）。
    /// ⚠️ 只存路径不存副本：这是本地工具，用户自己挑的文件放在他自己知道的地方；
    ///    哪天文件没了由 <c>TimerAlarm.ResolvePath</c> 退回默认，不会静默哑掉。
    /// </summary>
    public string TimerAlarmPath { get; set; } = "";

    /// <summary>
    /// 全屏倒计时 / 全屏秒表的背景是否沿用「全屏时钟」那套外观（背景图、蒙版、底色、字色）。
    /// 开着的场合，投影出来的计时盘跟教室那块全屏时钟长一样，不会一边一个画风。
    /// </summary>
    public bool TimerUseClockBackground { get; set; }

    // ── 下载路径（2026-10-05 Nick：设置 → 软件内容 →「GitHub 下载体验优化」）────────

    /// <summary>
    /// GitHub 下载链接走哪条路，取值见 <see cref="GithubRoutes"/>：
    /// <list type="bullet">
    ///   <item><c>github</c>（**默认**，2026-10-05 Nick 定）= GitHub 源：原样直连，不做任何改写；</item>
    ///   <item><c>auto</c> = 自建节点 / 各条公益镜像 / GitHub 源一起探速，按快慢排候选，结果缓存 8 分钟；</item>
    ///   <item><c>selfhosted</c> = 自建加速服务：先换一条自建节点的限时签名链接（Worker 代签）并固定首选，
    ///         取不到签名自动退公益镜像、再退 GitHub 源。</item>
    /// </list>
    /// ⚠️ 默认是「官方直连」而不是「自动」：不该替所有用户先探一遍外网。
    /// ⚠️ 这是**存档格式**的一部分：值只能加、不能改字面量（认不出的值一律当 GitHub 源，见
    ///    <see cref="GithubRoutes.Normalize"/>）。
    /// </summary>
    public string GithubDownloadRoute { get; set; } = GithubRoutes.Official;

    // ── 教室喊话（2026-10-25：对齐设计稿 docs/教室喊话功能设计.md 的 §6 配置表）──────────

    /// <summary>是否接收喊话（= 是否开轮询）。默认关：不该替所有用户默认后台上报。</summary>
    public bool AnnounceEnabled { get; set; }

    /// <summary>轮询间隔（秒）。页面提供 15 / 30 / 60 三档，默认 30（设计稿 §3.4 的额度核算结论）。</summary>
    public int AnnounceIntervalSec { get; set; } = 30;

    /// <summary>安静模式：只弹窗，不响提示音、不朗读。</summary>
    public bool AnnounceQuietMode { get; set; }

    /// <summary>语音朗读：用系统中文语音念正文；系统没有中文语音时自动跳过。</summary>
    public bool AnnounceSpeakText { get; set; } = true;

    /// <summary>
    /// 打开教室喊话页时默认展开哪一栏：<c>receive</c> | <c>send</c>（2026-10-25 Nick：
    /// 有些用户主要是发，不该每次都先看到接收）。认不出的值按接收处理。
    /// </summary>
    public string AnnounceDefaultView { get; set; } = "receive";

    /// <summary>
    /// 本机标识（2026-10-25 Nick 提）：例如「高28班」「三楼办公室」。
    /// 随局域网心跳广播出去，发送端的设备列表直接显示它 —— 老师一眼知道这台机是哪里的。
    /// 空 = 显示机器名。
    /// </summary>
    public string AnnounceLabel { get; set; } = "";

    /// <summary>局域网接收开关（默认关：开了会占用 UDP/TCP 两个端口并接受同网段的喊话）。</summary>
    public bool AnnounceLanEnabled { get; set; }

    /// <summary>发送端「已保存的电脑」（手动填过的配对码，勾选即发）。</summary>
    public System.Collections.Generic.List<AnnounceSavedPeer> AnnounceSavedPeers { get; set; } = new();

    /// <summary>
    /// 接收端「谁保存了本机配对码」（对方保存时经局域网登记的）。
    /// ⚠️ 只是登记记录，不是白名单：删掉它不等于对方失去发送能力（见页面上的说明）。
    /// </summary>
    public System.Collections.Generic.List<AnnounceBinder> AnnounceBinders { get; set; } = new();

    /// <summary>删除登记时对方不在线、还没通知到它的移除请求（等它下次出现再补发）。</summary>
    public System.Collections.Generic.List<PendingUnbind> AnnouncePendingUnbinds { get; set; } = new();

    /// <summary>
    /// 全屏喊话的出场方式（2026-10-25）：<c>typewriter</c> 打字机 / <c>slide</c> 按行交错划入 /
    /// <c>pop</c> 弹入 / <c>none</c> 不做动画（兜底，给不喜欢动画的人）。认不出的值按 none 处理。
    /// </summary>
    public string AnnounceAnimation { get; set; } = "typewriter";

    /// <summary>提示音路径；空 = 用内嵌的 announce-alert.wav。文件不存在时自动退回内嵌。</summary>
    public string AnnounceSoundPath { get; set; } = "";
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
    ///   · revision 3（虚拟键盘）：**已作废** —— 功能在 2026-10-09 整体下线，不再补入，直接跳到 4。
    ///   · revision 4（2026-10-09）：反过来做，把已下线的模块（keyboard / mirror-download）
    ///     从用户清单里**剔除**（见下面那一段）。
    ///   · revision 5（2026-10-11）：系统快捷三件套（任务管理器 / 安全选项 / 剪贴板历史），
    ///     三个一起插在讲台动作组最前面。
    ///   · revision 6（2026-10-12）：结束希沃白板（杀希沃）—— 插在讲台动作组**末尾**，
    ///     它属于特定场景的低频动作，不挤占那几颗常用的位置。
    ///   · revision 7（2026-10-18）：学科工具（合并成一项「学科」）—— **已被 revision 8 取代**。
    ///   · revision 8（2026-10-18）：把 rev7 那**一项**拆成**三个独立入口**
    ///     （函数图像 / 方程求解 / 化学配平）。Nick 定：三个工具各一个按钮，不合并。
    ///     存过 subject-tools 的**原地**替换成三项，保持用户排好的位置。
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

        // revision 3（虚拟键盘，2026-10-01）原本是往讲台动作类最前面插 "keyboard"。
        // ⚠️ 2026-10-09：虚拟键盘整体下线，这一批**不再补入**（还没迁到 3 的老用户直接跳过 ——
        //    那个模块已经不存在，补进去只会得到一条死 id）。
        if (Current.SidebarModulesRevision < 4)
        {
            // revision 4（2026-10-09）：把**已下线的模块**从用户自己那份清单里剔掉。
            // 跟别的功能不同，侧边栏模块是用户勾选 + 排序出来的：功能删了之后，存档里那条 id
            // 会被 SidebarModules.Find 跳过（不会崩），但用户在「侧边布局」页里再也看不到它，
            // 也就永远删不掉 —— 留在这儿只是一条谁也管不着的死条目。这里替他收干净。
            try
            {
                var list = (Current.SidebarModuleIds ?? Array.Empty<string>()).ToList();

                var removed = list.RemoveAll(id => id is "keyboard" or "mirror-download");
                if (removed > 0)
                {
                    Current.SidebarModuleIds = list.ToArray();
                    Log($"侧边栏清单已剔除 {removed} 个下线模块（虚拟键盘 / 系统镜像下载）");
                }
            }
            catch (Exception ex)
            {
                Log($"清理下线模块失败: {ex.Message}");
            }

            Current.SidebarModulesRevision = 4;
            changed = true;
        }

        if (Current.SidebarModulesRevision < 5)
        {
            // revision 5（2026-10-11）：系统快捷三件套 —— 任务管理器 / 安全选项 / 剪贴板历史。
            // 三个一起插在**讲台动作组最前面**（跟 rev1 递音量同一个位置约定）；
            // 每插一个就把落点往后挪一格，保证它们仨的先后顺序就是 taskmgr → security → clipboard。
            try
            {
                var list = (Current.SidebarModuleIds ?? Array.Empty<string>()).ToList();
                var at = list.FindIndex(id => Data.SidebarModules.Find(id)?.Kind == Data.SidebarModuleKinds.Action);

                foreach (var id in new[] { "taskmgr", "security", "clipboard" })
                {
                    if (list.Contains(id)) continue;            // 用户自己已经加过了，别插重复
                    if (at < 0) { list.Add(id); at = list.Count; }
                    else { list.Insert(at, id); at++; }
                }

                Current.SidebarModuleIds = list.ToArray();
            }
            catch (Exception ex)
            {
                Log($"补侧边栏新模块失败: {ex.Message}");
            }

            Current.SidebarModulesRevision = 5;
            changed = true;
        }

        if (Current.SidebarModulesRevision < 6)
        {
            // revision 6（2026-10-12）：侧边栏新增「杀希沃」—— 立刻清一次白板5 残留进程，不带时间点。
            // ⚠️ 插在**讲台动作组末尾**（FindLastIndex），不跟前面那几颗常用的抢位置；
            //    用户已经自己加过了就跳过，别插重复。
            try
            {
                var list = (Current.SidebarModuleIds ?? Array.Empty<string>()).ToList();

                if (!list.Contains("killseewo"))
                {
                    var last = list.FindLastIndex(id => Data.SidebarModules.Find(id)?.Kind == Data.SidebarModuleKinds.Action);
                    if (last < 0) list.Add("killseewo");
                    else list.Insert(last + 1, "killseewo");

                    Current.SidebarModuleIds = list.ToArray();
                }
            }
            catch (Exception ex)
            {
                Log($"补侧边栏新模块失败: {ex.Message}");
            }

            Current.SidebarModulesRevision = 6;
            changed = true;
        }

        if (Current.SidebarModulesRevision < 7)
        {
            // revision 7（2026-10-18）：学科工具 —— 函数图像 / 方程求解 / 化学配平单开一扇窗。
            // ⚠️ 插在**工具组末尾**（第一个既不是浮窗也不是整页、也不是独立窗的模块之前），
            //    不打断用户已经排好的顺序；用户自己已经加过就跳过，别插重复。
            try
            {
                var list = (Current.SidebarModuleIds ?? Array.Empty<string>()).ToList();

                if (!list.Contains("subject-tools"))
                {
                    var at = list.FindIndex(id =>
                    {
                        var kind = Data.SidebarModules.Find(id)?.Kind;
                        return kind is not (Data.SidebarModuleKinds.Palette
                            or Data.SidebarModuleKinds.Page
                            or Data.SidebarModuleKinds.Window);
                    });

                    if (at < 0) list.Add("subject-tools");
                    else list.Insert(at, "subject-tools");

                    Current.SidebarModuleIds = list.ToArray();
                }
            }
            catch (Exception ex)
            {
                Log($"补侧边栏新模块失败: {ex.Message}");
            }

            Current.SidebarModulesRevision = 7;
            changed = true;
        }

        if (Current.SidebarModulesRevision < 8)
        {
            // revision 8（2026-10-18）：rev7 那个合并的「学科工具」一项，拆成**三个独立入口**
            // （函数图像 / 方程求解 / 化学配平 —— Nick 定：三个工具各一个按钮，不要合并）。
            // · 存过 subject-tools 的：**原地**换成三项，保持用户已经排好的位置；
            // · 没存过的（新装或自己删过）：按工具组末尾补入，跟 rev7 的位置规则一致。
            try
            {
                var list = (Current.SidebarModuleIds ?? Array.Empty<string>()).ToList();
                string[] wanted = { "subject-function", "subject-equation", "subject-chemistry" };

                var legacy = list.IndexOf("subject-tools");
                if (legacy >= 0)
                {
                    list.RemoveAt(legacy);
                    list.InsertRange(legacy, wanted.Where(id => !list.Contains(id)));
                }

                foreach (var id in wanted)
                {
                    if (list.Contains(id)) continue;

                    var at = list.FindIndex(x =>
                    {
                        var kind = Data.SidebarModules.Find(x)?.Kind;
                        return kind is not (Data.SidebarModuleKinds.Palette
                            or Data.SidebarModuleKinds.Page
                            or Data.SidebarModuleKinds.Window);
                    });

                    if (at < 0) list.Add(id);
                    else list.Insert(at, id);
                }

                Current.SidebarModuleIds = list.ToArray();
            }
            catch (Exception ex)
            {
                Log($"补侧边栏新模块失败: {ex.Message}");
            }

            Current.SidebarModulesRevision = 8;
            changed = true;
        }

        if (Current.SidebarModulesRevision < 9)
        {
            // revision 9（2026-10-18）：侧边栏新增「关机」按钮（手动 / 直接关机，受设置控制）。
            // 插在动作组里、紧挨着「取消关机」之前（两者都跟关机相关，凑一块好找）；
            // 多数用户没勾过「取消关机」，列表里没有那个 id，就直接追加到末尾。
            try
            {
                var list = (Current.SidebarModuleIds ?? Array.Empty<string>()).ToList();

                if (!list.Contains("shutdown"))
                {
                    var at = list.IndexOf("cancelshutdown");
                    if (at < 0) list.Add("shutdown");
                    else list.Insert(at, "shutdown");

                    Current.SidebarModuleIds = list.ToArray();
                }
            }
            catch (Exception ex)
            {
                Log($"补侧边栏新模块失败: {ex.Message}");
            }

            Current.SidebarModulesRevision = 9;
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
            Core.AppLog.Info("settings", message);
        }
        catch
        {
            // 日志写不进去就算了
        }
    }
}
