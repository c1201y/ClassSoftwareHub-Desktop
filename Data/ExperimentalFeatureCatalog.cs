using System;
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

        // ── 2026-11-07 从本表移出（Nick：这五个不必再挂实验性） ──
        // 系统优化 systemtweaks / 系统功能 sysfeatures / PowerPoint 触控优化 ppttouch /
        // 虚拟内存 pagefile / 幽灵文件清理 ghostfile
        // ⚠️ 搬到了 <see cref="ToolCatalog"/> 的 System 组（与 2026-10-29 那三个同一套搬法），
        //    导航子项、工具索引页卡片、搜索索引都由那张表生成。
        // ⚠️ 页面上的「实验性功能」标记不用动 —— <c>Controls/ExperimentalBadge</c> 查不到就自动收起。
        // ⚠️ 其中 ppttouch 默认占一级导航，位置在 <see cref="NavLayout.Pages"/>（可被用户收进分组）。
    };

    /// <summary>
    /// 按导航 tag 找一条定义；找不到返回 null。
    ///
    /// <para>
    /// 页面标题旁那枚「实验性功能」标记（<c>Controls/ExperimentalBadge</c>）就靠它决定显示与否：
    /// **功能转正时只要从这里删掉这一条，标志会自动消失**，不用回头去改页面
    /// （2026-11-03 Nick 定的规矩，见 MustRead）。
    /// </para>
    /// </summary>
    public static ExperimentalFeature? Find(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        foreach (var def in All)
        {
            if (string.Equals(def.Tag, tag, StringComparison.OrdinalIgnoreCase)) return def;
        }

        return null;
    }
}
