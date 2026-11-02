using System.Collections.Generic;

namespace ClassSoftwareHub.Desktop.Data;

/// <summary>
/// 「实验性功能」这一组的结算清单 —— **全项目唯一一份**。
///
/// <para>
/// 导航栏「实验性功能」分组的子项、以及实验性功能总览页的卡片，都从这里生成
/// （见 <c>ShellPage.BuildExperimentalNavItems</c> 与 <c>ExperimentalPage</c>）。
/// ⛔ 别再往 <c>ShellPage.xaml</c> 里手写子项 —— 那正是 2026-11-01 之前的做法，
/// 结果两处清单各改一半：「白板专杀/AppData 管理/系统修复」转正搬去系统工具之后，
/// 总览页里还留着三张旧卡；而「虚拟内存」只加进了导航、总览页看不见。
/// </para>
///
/// <para>
/// 加一项只改这里一处（外加 <c>NavigateTagCore</c> 的 case）。<see cref="ExperimentalFeature.Tag"/>
/// 就是导航 tag，反查页面的规矩跟工具那套一致。
/// </para>
///
/// <para>
/// ⚠️ 判断标准：**成熟度不够**才放这儿，不是"系统类"就放这儿。
/// 系统工具（磁盘、关机、校验、取色）在 <see cref="ToolCatalog"/> 的 <c>System</c> 组里，
/// 那一组已经是正式功能 —— 两边不要互相搬来搬去（转正时就该从这张表删掉）。
/// </para>
/// </summary>
public static class ExperimentalFeatureCatalog
{
    public static readonly IReadOnlyList<ExperimentalFeature> All = new List<ExperimentalFeature>
    {
        // 本机核实：拿本机已装软件跟清单对一遍。
        // ⚠️ 为啥塞实验性：结果**明显不准**（只读「程序和功能」注册表，匹配靠名称模糊比对，
        //    Store 版 / 免安装版 / 改了名的全认不出来）。放一级导航容易被当成装机验收依据。
        new ExperimentalFeature
        {
            Id = "machinecheck", Name = "本机核实",
            Desc = "对照本机的已安装程序记录，看清单里的软件装没装",
            Glyph = "\uE7F4", Tag = "machinecheck"
        },

        // 程序专杀：用户自己指定进程名，每个程序单独配结束时间点。
        // ⚠️ 与白板专杀各存各的配置、各跑各的定时器（Nick 明确要求白板独立）。
        new ExperimentalFeature
        {
            Id = "procguard", Name = "程序专杀",
            Desc = "为指定程序设定结束时间点，到点结束其后驻留进程",
            Glyph = "\uE7E8", Tag = "procguard"
        },

        // 应用卸载（2026-10-12 加）。⚠️ 为啥塞实验性：卸载不可逆，而扫描口径有已知盲区
        //    （免安装版没有登记、别的账户注册的商店应用读不到）。
        new ExperimentalFeature
        {
            Id = "uninstaller", Name = "应用卸载",
            Desc = "扫描本机已安装的软件，可卸载其中的任意一款",
            Glyph = "\uE74D", Tag = "uninstaller"
        },

        // 开机启动项（2026-10-16 加）。⚠️ 为啥塞实验性：只覆盖注册表 Run 与启动文件夹，
        //    服务 / 计划任务 / 组策略启动脚本都不在范围内，容易被当成"全部自启项"。
        // ⚠️ 关掉一项写的是 StartupApproved 状态位（与任务管理器同一套），
        //    不删注册表值、不移动文件。
        new ExperimentalFeature
        {
            Id = "startup", Name = "开机启动项",
            Desc = "查看开机时会自动启动的程序，并逐个关掉",
            Glyph = "\uE945", Tag = "startup"
        },

        // 系统优化（2026-10-31，需求 20）。⚠️ 改的是整机系统设置，猜错一个键就变成
        //    "某功能突然不按预期"，先跑一段再说转正。
        new ExperimentalFeature
        {
            Id = "systemtweaks", Name = "系统优化",
            Desc = "任务栏、资源管理器这些整机设置，选中后一次授权统一应用",
            Glyph = "\uE713", Tag = "systemtweaks"
        },

        // 系统功能（2026-10-31，需求 20 重做）：13 项 HKLM / powercfg 设置，排队后一次提权写入。
        new ExperimentalFeature
        {
            Id = "sysfeatures", Name = "系统功能",
            Desc = "休眠、快速启动、电源计划等系统开关，排队后一次授权写入",
            Glyph = "\uE7E8", Tag = "sysfeatures"
        },

        // PowerPoint 触控优化（2026-11-01，需求 2）：放映时轻点翻页。
        // ⚠️ 为啥塞实验性：它挂的是全局鼠标钩子、补的是真实点击，行为跟触屏驱动/PPT 版本有关，
        //    先跑一段看看再决定转正。
        new ExperimentalFeature
        {
            Id = "ppttouch", Name = "PowerPoint 触控优化",
            Desc = "放映时轻点屏幕翻到下一页，视频与按钮照常响应",
            Glyph = "\uE786", Tag = "ppttouch"
        },

        // 虚拟内存（2026-10-31，需求 19）：搬系统的「虚拟内存」对话框。
        // ⚠️ 单独一页不并进「系统功能」：它改的是逐个驱动器的初始/最大值，形态完全不同，
        //    而且改完必须重启，风险等级也不一样。
        new ExperimentalFeature
        {
            Id = "pagefile", Name = "虚拟内存",
            Desc = "逐个驱动器设置分页文件大小，重启后生效",
            Glyph = "\uEDA2", Tag = "pagefile"
        },
    };
}
