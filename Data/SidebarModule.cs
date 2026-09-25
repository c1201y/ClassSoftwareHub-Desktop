using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassSoftwareHub.Desktop.Data;

/// <summary>模块怎么打开。</summary>
public static class SidebarModuleKinds
{
    /// <summary>置顶小浮窗（ToolPaletteWindow），不占主界面。</summary>
    public const string Palette = "palette";
    /// <summary>主窗口里的整页工具（拉出主窗口并跳到那一页）。</summary>
    public const string Page = "page";
    /// <summary>讲台动作：按一下就干活（不外跳、不抢焦点、不开新窗口）。</summary>
    public const string Action = "action";
}

/// <summary>
/// 侧边栏里的一个"模块"。侧边布局页就是拿 <see cref="SidebarModules.All"/> 这份清单让用户勾选 + 排序，
/// 侧边栏再按 AppSettings.SidebarModuleIds 动态拼出来。
/// </summary>
public sealed class SidebarModule
{
    /// <summary>模块 id（同时也是存取设置用的键；palette 类就是工具 id）。</summary>
    public string Id { get; init; } = "";

    /// <summary>完整名字（侧边布局页、鼠标悬停提示用）。</summary>
    public string Name { get; init; } = "";

    /// <summary>侧边栏按钮上的短名（那条很窄，只放得下两三个字）。</summary>
    public string ShortName { get; init; } = "";

    /// <summary>Segoe Fluent Icons 字形。</summary>
    public string Glyph { get; init; } = "";

    /// <summary>SidebarModuleKinds.Palette / .Page / .Action。</summary>
    public string Kind { get; init; } = SidebarModuleKinds.Palette;

    /// <summary>kind = page 时要跳转的页面类型。</summary>
    public Type? Page { get; init; }

    /// <summary>想给某个模块写更贴切的说明就填它（不填就用 Kind 的通用说法）。</summary>
    public string? Note { get; init; }

    /// <summary>给用户看的一句话说明。</summary>
    public string Hint => Note ?? Kind switch
    {
        SidebarModuleKinds.Page => "打开主窗口里的这一页",
        SidebarModuleKinds.Action => "按一下就干活，不跳窗口、不抢焦点",
        _ => "弹一个小浮窗，不占主界面"
    };
}

/// <summary>侧边栏可拼的模块清单（改这里就能加新模块）。</summary>
public static class SidebarModules
{
    public static readonly IReadOnlyList<SidebarModule> All = new List<SidebarModule>
    {
        new() { Id = "pick-number", Name = "随机抽号", ShortName = "抽号", Glyph = "\uE716", Kind = SidebarModuleKinds.Palette },
        new() { Id = "timer", Name = "课堂计时", ShortName = "计时", Glyph = "\uE81C", Kind = SidebarModuleKinds.Palette },
        new() { Id = "stopwatch", Name = "秒表计时", ShortName = "秒表", Glyph = "\uE916", Kind = SidebarModuleKinds.Palette },
        new() { Id = "clock", Name = "全屏时钟", ShortName = "时钟", Glyph = "\uE740", Kind = SidebarModuleKinds.Palette },
        new() { Id = "image-color", Name = "图片取色", ShortName = "取色", Glyph = "\uE790", Kind = SidebarModuleKinds.Page, Page = typeof(Pages.Tools.ImageColorToolPage) },
        new() { Id = "encoding", Name = "编码 / 哈希转换", ShortName = "编码", Glyph = "\uE943", Kind = SidebarModuleKinds.Page, Page = typeof(Pages.Tools.EncodingToolPage) },
        new() { Id = "mirror-download", Name = "系统镜像下载", ShortName = "镜像", Glyph = "\uE896", Kind = SidebarModuleKinds.Page, Page = typeof(Pages.Tools.MirrorToolPage) },

        // ── 讲台动作：按一下就干活，不外跳、不抢焦点 ──
        new() { Id = "mag", Name = "放大镜", ShortName = "放大", Glyph = "\uE71E", Kind = SidebarModuleKinds.Action, Note = "调系统放大镜（跟着鼠标走的那块）；点一下开、再点一下关" },
        new() { Id = "screenshot", Name = "截屏贴图", ShortName = "截屏", Glyph = "\uE722", Kind = SidebarModuleKinds.Action, Note = "拖一块 → 弹出**编辑窗**：画笔/荧光笔/箭头/矩形/椭圆/文字/马赛克 + 撤销重做，然后**复制到剪贴板 / 保存到本地 / 钉图**（钉图 1:1、拖边框缩放、双击或右键关掉）" },
        new() { Id = "taskview", Name = "任务视图", ShortName = "任务", Glyph = "\uE7C4", Kind = SidebarModuleKinds.Action, Note = "等于 Win+Tab" },
        new() { Id = "showdesktop", Name = "回到桌面", ShortName = "桌面", Glyph = "\uE7F4", Kind = SidebarModuleKinds.Action, Note = "把窗口都收起来看桌面；再按一次还原（不关任何东西）" },
        new() { Id = "closefg", Name = "关闭前台应用", ShortName = "关前台", Glyph = "\uE8BB", Kind = SidebarModuleKinds.Action, Note = "关掉你正在用的那个窗口（等于点 ×，会弹「是否保存」，不硬杀）" },
        new() { Id = "closeall", Name = "关闭全部窗口", ShortName = "关全部", Glyph = "\uE74D", Kind = SidebarModuleKinds.Action, Note = "把任务栏里开着的一堆一次关掉（含最小化的）。防误触：**第一下只点数，再按一下才真关**；全是优雅关闭，不硬杀" },
        // 「最小化全部窗口」已删（2026-09-25）：「回到桌面」在 Windows 上做的事跟它一模一样（都是把窗口全收起来、再按一次还原），
        // 两个按钮一个效果，留着只会让人问「有区别吗」。要恢复就把这行抄回去。
    };

    /// <summary>恢复默认时用的清单（就是老版本侧边栏的那四个）。</summary>
    public static readonly string[] DefaultIds = { "pick-number", "timer", "stopwatch", "clock" };

    /// <summary>按 id 找模块；找不到（比如设置里存了已经删掉的模块）返回 null，调用方直接跳过。</summary>
    public static SidebarModule? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : All.FirstOrDefault(m => m.Id == id);
}

/// <summary>「侧边布局」页里的一行（给 ItemsControl 用）。</summary>
public sealed class SidebarModuleRow
{
    public SidebarModuleRow(SidebarModule m, bool enabled)
    {
        Id = m.Id;
        Name = m.Name;
        Hint = m.Hint;
        Glyph = m.Glyph;
        Enabled = enabled;
    }

    public string Id { get; }
    public string Name { get; }
    public string Hint { get; }
    public string Glyph { get; }

    /// <summary>这个模块当前在不在侧边栏上（开关绑它）。</summary>
    public bool Enabled { get; set; }
}
