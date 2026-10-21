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
            Desc = "输入含 x 的表达式即可绘制函数曲线，可同时绘制多条以便比较；支持滚轮缩放与拖动查看。",
            Glyph = "\uE9D2", Page = typeof(ClassSoftwareHub.Desktop.Pages.FunctionPlotPage)
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
    };
}
