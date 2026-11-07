using System;
using System.Collections.Generic;
using ClassSoftwareHub.Desktop.Pages.Tools;

namespace ClassSoftwareHub.Desktop.Data;

/// <summary>内置工具卡片定义（工具索引页用）。</summary>
/// <summary>内置工具的分组（2026-10-22 Nick：导航把原「内置工具」拆成两项）。</summary>
public enum ToolGroup
{
    /// <summary>课堂上用的：抽号、计时、时钟、学科工具等。</summary>
    Classroom,

    /// <summary>系统维护类：磁盘、关机、校验、取色等。</summary>
    System,
}

public sealed class ToolDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>归属分组（导航与工具索引页都按它分派）。⚠️ 默认课堂，新增系统工具时务必显式标注。</summary>
    public ToolGroup Group { get; init; } = ToolGroup.Classroom;
    public string Desc { get; init; } = "";
    /// <summary>SEGOEICONS 字形码。</summary>
    public string Glyph { get; init; } = "";
    /// <summary>点开要导航到的页面。</summary>
    public Type? Page { get; init; }

    /// <summary>
    /// 网页版工具包的 id（2026-10-23）。空 = 这个工具没有 Web 版，永远走 <see cref="Page"/>。
    /// <para>
    /// ⚠️ 填了它只是"有这个可能"：真正走不走 Web 由 <c>ToolCarrier</c> 现场判定
    /// （WebView2 运行时在不在 + 本地有没有这个工具包）。两样缺一就静默走原生页。
    /// </para>
    /// </summary>
    public string? WebToolId { get; init; }

    /// <summary>
    /// 这个工具已经**单独占了一个导航栏顶层项**（2026-10-28 加）。
    /// <para>
    /// 置 true 之后，「课堂工具 / 系统工具」的动态子项和工具索引页的卡片都不再出现它 ——
    /// 否则导航栏里会同时有顶层项和分组子项两个入口，看着像重复。
    /// </para>
    /// <para>
    /// ⚠️ 但它**仍然留在 <see cref="ToolCatalog.All"/> 里**，这一点与「教室喊话」不同（那个是整条移出）。
    /// 因为 <c>--tool=&lt;id&gt;</c>、学科工具窗、侧边栏模块、以及导航按 Id 反查页面类型，
    /// 全都依赖这张表；移出去会把那几条路一起打断。
    /// </para>
    /// </summary>
    public bool Standalone { get; init; }

    /// <summary>无障碍 / 调试用：列表项名字就是工具名。</summary>
    public override string ToString() => Name;
}

/// <summary>
/// 内置工具清单 —— 导航栏「内置工具」分组的子项、工具索引页的卡片，共用这一份。
/// <para>⚠️ 往这儿加工具：① 下面 <see cref="All"/> 加一条（Id/Name/Desc/Glyph/Page）；
/// ② 导航子项是 <c>ShellPage</c> 按 <see cref="ToolDef.Id"/> 动态生成的，<c>NavigateTagCore</c>
///    也是按 Id 反查 <see cref="All"/>，所以**不用再改导航**；③ 若首页卡片 / 浮窗有直达入口，另加。</para>
/// </summary>
public static class ToolCatalog
{
    public static readonly List<ToolDef> All = new()
    {
        new ToolDef
        {
            Id = "image-color", Name = "图片取色",
            Group = ToolGroup.System,
            Desc = "导入图片后自动提取一组柔和的配色（种子色 + 7 档明暗变体），单击色块即可复制。",
            Glyph = "\uE790", Page = typeof(ImageColorToolPage)
        },
        new ToolDef
        {
            Id = "pick-number", Name = "随机抽号",
            Desc = "按学号范围或班级名单随机抽取，可一次抽多个、抽过不重复，也能一键随机分组。",
            Glyph = "\uE716", Page = typeof(PickNumberToolPage)
        },
        new ToolDef
        {
            Id = "timer", Name = "课堂计时器",
            Desc = "倒计时 / 秒表，大字号便于投影，到达设定时间响铃。",
            Glyph = "\uE916", Page = typeof(TimerToolPage)
        },
        new ToolDef
        {
            Id = "clock", Name = "全屏时钟",
            Desc = "将屏幕变为大型时钟：可设置背景图与蒙版，全屏显示查看时间。",
            Glyph = "\uE740", Page = typeof(ClockToolPage)
        },
        new ToolDef
        {
            Id = "encoding", Name = "编码 / 哈希工具",
            Group = ToolGroup.System,
            Desc = "校验文件：拖入即得 MD5 / SHA-1 / SHA-256 / SHA-512，可粘贴官方值自动核对；也可做 Base64、URL 编解码。",
            Glyph = "\uE943", Page = typeof(EncodingToolPage)
        },
        new ToolDef
        {
            Id = "autoshutdown", Name = "自动关机",
            Group = ToolGroup.System,
            Desc = "按星期设定关机时间点，到达时间后自动关机；可直接关机（可设延迟），也可用系统原生的滑动关机。",
            Glyph = "\uE787", Page = typeof(ClassSoftwareHub.Desktop.Pages.AutoShutdownPage)
        },
        // ⚠️ 原来是第 6 个工具「系统镜像下载（mirror-download → MirrorToolPage）」，
        //    2026-10-09 该功能整体下线，工具定义、页面、侧边栏模块、内容解析一并删除。
        // ⚠️「自动关机」2026-10-12 从「实验性功能」转正搬来这里（Nick 定：这玩意可以退出实验性了）——
        //    导航子项与工具索引页卡片都会自动跟着出现，不用另改导航（见本类注释）。
        new ToolDef
        {
            Id = "function-plot", Name = "函数图像",
            // 2026-10-29 跟着网页版（V1.26.1029）重写：它早已不只是「画函数曲线」，
            // 隐函数 / 不等式 / 极坐标 / 参数方程，以及点线圆与圆锥曲线都在同一块画板上。
            // 这段文字是搜索索引与工具说明的唯一来源，别只写单条曲线的能力。
            Desc = "绘制函数与几何图形：支持显函数、隐函数、不等式、极坐标与参数方程，"
                   + "可作点、线段、圆、椭圆、双曲线与多边形，并能绘制平行线、垂线与切线。",
            Glyph = "\uE9D2", Page = typeof(ClassSoftwareHub.Desktop.Pages.FunctionPlotPage),
            // 网页版工具包 id：有它且本机跑得起来就走 Web，否则静默走上面那个原生页。
            WebToolId = "function-plot",
            // 2026-10-28 Nick：提到导航栏单独成项，工具分组与索引页里不再重复出现（见 Standalone 的说明）。
            Standalone = true
        },
        new ToolDef
        {
            Id = "disk-map", Name = "磁盘空间",
            Group = ToolGroup.System,
            Desc = "按占用面积画出目录结构，一眼看出空间被谁吃掉了；单击方块可逐层下钻。",
            // E7F1 = 硬盘
            Glyph = "\uE7F1", Page = typeof(DiskMapPage)
        },
        new ToolDef
        {
            Id = "equation", Name = "方程求解",
            Desc = "一次方程组给出精确分数解；一元方程按方程本身求根，支持平方、三角函数、对数与指数。",
            // E8EF = 计算器
            Glyph = "\uE8EF", Page = typeof(EquationPage)
        },
        new ToolDef
        {
            Id = "chemistry", Name = "化学方程式配平",
            Desc = "输入 A + B = C 形式的方程式即可算出最小正整数系数，并逐种元素核对左右原子数。支持括号与结晶水。",
            // E80A = 九宫格（像元素周期表）。⛔ 只用 E7/E8/E9 老码位，ED 段在 Win10 是空框
            Glyph = "\uE80A", Page = typeof(ChemistryPage)
        },
        // ⚠️「教室喊话」曾于 2026-10-25 上午作为工具加在这里，当天下午 Nick 要求「提出来单独做」——
        //    已改为导航栏一级功能项（ShellPage.xaml 的 Tag=announce + NavigateTagCore 的 case），
        //    不要再往工具清单里加回来（否则导航里会出现两个入口）。

        // ── 2026-10-29 从「实验性功能」转正搬来（Nick：这三个已经挺成熟了） ──
        // ⚠️ 搬过来的方式与「教室喊话」相反：这次是**移进** ToolCatalog ——
        //    导航子项与索引页卡片都由这里动态生成，ShellPage.xaml 里那三条手写的
        //    NavigationViewItem 已删除，NavigateTagCore 的三个 case 也一并删掉（按 Id 反查能命中）。
        // ⚠️ Id 必须与原 tag 一字不差（easiguard / appdata / repair）：
        //    page= 启动参数、首页快捷方式、崩溃日志里的 CurrentTag 全都按它认。
        new ToolDef
        {
            Id = "easiguard", Name = "白板专杀",
            Group = ToolGroup.System,
            Desc = "到点结束希沃白板5 后台滞留的残留进程。只认白板家族（EasiNote*），不碰集控端与更新组件。",
            Glyph = "\uEA99", Page = typeof(ClassSoftwareHub.Desktop.Pages.EasiNoteGuardPage)
        },
        new ToolDef
        {
            Id = "appdata", Name = "AppData 管理",
            Group = ToolGroup.System,
            Desc = "列出 AppData 下可安全清理的缓存目录，按占用排序，逐个确认后删除；禁碰区一律不列。",
            Glyph = "\uE8B7", Page = typeof(ClassSoftwareHub.Desktop.Pages.AppDataPage)
        },
        new ToolDef
        {
            Id = "repair", Name = "系统修复",
            Group = ToolGroup.System,
            Desc = "系统文件、组件存储、网络栈、系统服务的修复命令，按预设卡片执行，执行前原样列出命令。",
            Glyph = "\uE7EF", Page = typeof(ClassSoftwareHub.Desktop.Pages.SystemRepairPage)
        },
        // ⚠️ 「系统功能」「系统优化」2026-10-31 起改挂「实验性功能」（Nick 要求先跑一段），
        //    2026-11-07 又随下面这批一起转正搬回这里 —— 两次都只改表，导航与索引自动跟着变。

        // ── 2026-11-07 从「实验性功能」转正搬来（Nick：这五个不必再挂实验性） ──
        // ⚠️ 搬法与 2026-10-29 那三个一致：整条移进本表，导航子项与索引页卡片由这里生成，
        //    ExperimentalFeatureCatalog 里对应的条目已删，页面标题旁的「实验性功能」标记随之自动消失。
        // ⚠️ Id 必须与原 tag 一字不差：page= 启动参数、Hub AI 动作清单（AppUri.ForPage）、
        //    OOBE 完成页跳转、崩溃日志里的 CurrentTag 全都按它认。
        new ToolDef
        {
            // PowerPoint 触控优化（2026-11-01，需求 2）：放映时轻点翻页。
            // ⚠️ 它**不在**「系统工具」子项里显示 —— 默认就占一级导航，位置在 Data/NavLayout.Pages。
            //    写进本表是为了：搜索能搜到、工具索引页在它被收进分组时能出卡片、`--tool=ppttouch` 可用。
            //    ⛔ 别改成 Standalone：那个是"永远在一级、不参与自定义"，而这一项 Nick 要求可自定义。
            Id = "ppttouch", Name = "PowerPoint 触控优化",
            Group = ToolGroup.System,
            Desc = "放映时轻点屏幕翻到下一页，上下滑动切换页面，长按弹出放映菜单。",
            Glyph = "\uE786", Page = typeof(ClassSoftwareHub.Desktop.Pages.PptTouchPage)
        },
        new ToolDef
        {
            // 虚拟内存（2026-10-31，需求 19）：搬系统的「虚拟内存」对话框。
            // ⚠️ 单独一页不并进「系统功能」：它改的是逐个驱动器的初始/最大值，形态完全不同，
            //    而且改完必须重启，风险等级也不一样。
            Id = "pagefile", Name = "虚拟内存",
            Group = ToolGroup.System,
            Desc = "逐个驱动器设置分页文件大小，重启后生效。",
            Glyph = "\uEDA2", Page = typeof(ClassSoftwareHub.Desktop.Pages.PageFilePage)
        },
        new ToolDef
        {
            // 幽灵文件清理（2026-11-03）：文件删了、图标还在、双击提示找不到路径的那一类。
            Id = "ghostfile", Name = "幽灵文件清理",
            Group = ToolGroup.System,
            Desc = "清理删不掉、双击提示找不到路径的残留条目。",
            Glyph = "\uE7C4", Page = typeof(ClassSoftwareHub.Desktop.Pages.GhostFilePage)
        },
        new ToolDef
        {
            // 系统功能（2026-10-31，需求 20 重做）：13 项 HKLM / powercfg 设置，排队后一次提权写入。
            Id = "sysfeatures", Name = "系统功能",
            Group = ToolGroup.System,
            Desc = "休眠、快速启动、电源计划等系统开关，排队后一次授权写入。",
            Glyph = "\uE7E8", Page = typeof(ClassSoftwareHub.Desktop.Pages.SystemFeaturesPage)
        },
        new ToolDef
        {
            // 系统优化（2026-10-31，需求 20）：改的是整机系统设置，选中后一次授权统一应用。
            Id = "systemtweaks", Name = "系统优化",
            Group = ToolGroup.System,
            Desc = "任务栏、资源管理器这些整机设置，选中后一次授权统一应用。",
            Glyph = "\uE713", Page = typeof(ClassSoftwareHub.Desktop.Pages.SystemTweaksPage)
        },
    };
}
