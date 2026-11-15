using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassSoftwareHub.Desktop.Data;

/// <summary>
/// 一个「可以在一级导航和所属分类的二级菜单之间来回搬」的页面（2026-11-04）。
///
/// <para>
/// 分两类：
/// <list type="bullet">
///   <item><b>页面项</b>（<see cref="NavLayout.Pages"/>）：查阅单词、文档库、教室喊话 ——
///         本来就是一级项，用户可以把它们收进「课堂工具 / 系统工具」。</item>
///   <item><b>工具项</b>（<see cref="NavLayout.Tools"/>）：课堂/系统工具分组里的每个小工具 ——
///         本来在二级，用户可以把常用的单独拎到一级。</item>
/// </list>
/// </para>
///
/// <para>
/// ⛔ 分类（<see cref="Group"/>）**不给用户改**：查阅单词永远算课堂、文档库永远算系统。
/// 用户只能决定它显不显示在一级，不能决定它属于哪一组 —— 否则总览页会变成两不像的东西。
/// </para>
/// </summary>
public sealed class NavPlaceable
{
    /// <summary>导航 tag，与 <c>ShellPage.NavigateTagCore</c> 认的值一字不差。</summary>
    public string Tag { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>归属分类。工具沿用 <see cref="ToolDef.Group"/>，页面项在这里写死。</summary>
    public ToolGroup Group { get; init; } = ToolGroup.Classroom;

    /// <summary>SEGOEICONS 字形码（提到一级时用，总览页卡片也用它）。</summary>
    public string Glyph { get; init; } = "";

    /// <summary>一句话说明，只在总览页卡片上显示。</summary>
    public string Desc { get; init; } = "";

    /// <summary>默认就在一级。工具默认 false（在二级），页面项默认 true。</summary>
    public bool DefaultTopLevel { get; init; }

    /// <summary>
    /// 放到一级时，位置在「函数图像」**上面**而不是「课堂工具」上面。
    /// 只有教室喊话是这种（2026-10-25 Nick 把它挪到「侧边布局」正下方，那之后一直在函数图像前）。
    /// </summary>
    public bool AboveFunctionPlot { get; init; }
}

/// <summary>
/// 导航栏布局（2026-11-04 需求）：哪些页面单独放在一级，哪些收进「课堂工具 / 系统工具」。
///
/// <para>
/// 三条口径（Nick 定）：
/// <list type="number">
///   <item>默认 = 现在这个样子。两个设置数组都为空时，行为与加这个功能之前**逐项一致**。</item>
///   <item>首页 / 软件下载 / Hub AI / 侧边布局 / 函数图像 固定在一级，不参与。</item>
///   <item>实验性功能的子项固定留在二级，不能提到一级 —— 那是「还没成熟」的位置含义。</item>
/// </list>
/// </para>
///
/// <para>
/// ⛔ 存的是「与默认不同的那部分」而不是整张快照：这样以后往 <see cref="ToolCatalog"/>
/// 加新工具，老用户升级后能自动看到它（按默认位置），不会被一份旧快照挡在外面。
/// 反例见 <c>SettingsStore.SidebarModulesRevision</c> —— 那个存的是整份清单，所以得靠批次号补入。
/// </para>
/// </summary>
public static class NavLayout
{
    /// <summary>固定在一级的项（用户改不了）。这里只用于说明与校验，排序与图标都在 ShellPage.xaml 里。</summary>
    public static readonly IReadOnlyList<string> FixedTopLevel = new[]
    {
        "home", "apps", "hubai", "sidebar", "function-plot",
    };

    /// <summary>本来就在一级、但允许收进二级的三个页面。</summary>
    public static readonly IReadOnlyList<NavPlaceable> Pages = new List<NavPlaceable>
    {
        new()
        {
            Tag = "announce", Name = "教室喊话", Group = ToolGroup.Classroom,
            Glyph = "\uE720",
            Desc = "向同一配对码的设备发送一条通知，显示 180 秒后自动消失。",
            DefaultTopLevel = true, AboveFunctionPlot = true,
        },
        new()
        {
            Tag = "dictionary", Name = "查阅单词", Group = ToolGroup.Classroom,
            Glyph = "\uE8F1",
            Desc = "内置约三万条常用词，未收录的词转在线词典查询，支持英汉互译与整句翻译。",
            DefaultTopLevel = true,
        },
        new()
        {
            Tag = "docs", Name = "文档库", Group = ToolGroup.System,
            Glyph = "\uE8A5",
            Desc = "电脑优化与使用技巧的图文教程，支持按标题、简介与标签检索。",
            DefaultTopLevel = true,
        },
        new()
        {
            // PowerPoint 触控优化（2026-11-07 Nick：从实验性功能转正，**默认放在一级导航**）。
            // ⚠️ 它同时也是 ToolCatalog 里的一条（Id 相同）：这里管「放哪」，那张表管「用什么页面」。
            //    两边都写是刻意的 —— 不提一级时它就是「系统工具」里的一张卡片与子项。
            // ⚠️ 因此 NavLayout.Tools 的筛选里要排掉它，否则二级会多出一份同样的项（见 Tools 的注释）。
            Tag = "ppttouch", Name = "PowerPoint 触控优化", Group = ToolGroup.System,
            Glyph = "\uE786",
            Desc = "放映时轻点屏幕翻到下一页，上下滑动切换页面，长按弹出放映菜单。",
            DefaultTopLevel = true,
        },
    };

    /// <summary>
    /// 工具项：<see cref="ToolCatalog.All"/> 里除去已经单独占了一级位置的那些（函数图像、PowerPoint 触控优化）。
    /// ⚠️ 每次都从 ToolCatalog 现取，不再另存一份清单 —— 工具清单只有一份，抄第二份必然漂移。
    /// ⚠️ 还要排掉 <see cref="Pages"/> 里已经登记过的那几条：PowerPoint 触控优化在两张表里都有
    ///    （ToolCatalog 管页面、这里管位置），不排掉的话它会同时作为「一级项」和「二级子项」出现两次。
    /// </summary>
    public static IReadOnlyList<NavPlaceable> Tools { get; } =
        ToolCatalog.All
            .Where(t => !t.Standalone)
            .Where(t => !Pages.Any(p => Same(p.Tag, t.Id)))
            .Select(t => new NavPlaceable
            {
                Tag = t.Id,
                Name = t.Name,
                Group = t.Group,
                Glyph = t.Glyph,
                Desc = t.Desc,
                DefaultTopLevel = false,
            })
            .ToList();

    /// <summary>全部可移动项（页面项在前、工具在后）。</summary>
    public static IReadOnlyList<NavPlaceable> All { get; } = Pages.Concat(Tools).ToList();

    /// <summary>按 tag 找可移动项；固定项与实验性项返回 null。</summary>
    public static NavPlaceable? Find(string? tag)
        => string.IsNullOrEmpty(tag)
            ? null
            : All.FirstOrDefault(p => string.Equals(p.Tag, tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>这一项现在该不该显示在一级。</summary>
    public static bool IsTopLevel(string? tag)
    {
        if (Find(tag) is not { } placeable) return true;   // 固定项：永远在一级

        var s = App.Settings.Current;

        // 显式设置优先。两个数组都只会存「与默认不同」的那部分（见类注释）。
        if (Contains(s.NavTopLevel, tag)) return true;
        if (Contains(s.NavSecondLevel, tag)) return false;

        return placeable.DefaultTopLevel;
    }

    /// <summary>
    /// 改一项的位置。只记「与默认不同」的那些，所以改回默认时数组里那条会被抹掉。
    /// ⚠️ 调用方负责让导航重建（<c>ShellPage.ReloadNavLayout</c>、<c>ToolsPage</c> 下次进入时自会重算）。
    /// </summary>
    public static void SetTopLevel(string tag, bool topLevel)
    {
        if (Find(tag) is not { } placeable) return;
        if (IsTopLevel(tag) == topLevel) return;

        var s = App.Settings.Current;

        var promoted = (s.NavTopLevel ?? Array.Empty<string>()).ToList();
        var demoted = (s.NavSecondLevel ?? Array.Empty<string>()).ToList();

        promoted.RemoveAll(t => Same(t, tag));
        demoted.RemoveAll(t => Same(t, tag));

        // 回到默认就不留记录 —— 这样「恢复默认」不需要额外一套状态
        if (topLevel != placeable.DefaultTopLevel)
            (topLevel ? promoted : demoted).Add(tag);

        s.NavTopLevel = promoted.ToArray();
        s.NavSecondLevel = demoted.ToArray();
    }

    /// <summary>
    /// 某一组当前留在二级的项，顺序与导航子项一致（页面项在前、工具在后）。
    /// <c>ToolsPage</c> 的卡片列表与导航子项都走它 —— 两处口径必须一样，否则会出现
    /// 「导航里有、总览页没有」这种对不上的情况。
    /// </summary>
    public static IReadOnlyList<NavPlaceable> SecondLevel(ToolGroup group)
        => All.Where(p => p.Group == group && !IsTopLevel(p.Tag)).ToList();

    private static bool Contains(string[]? list, string? tag)
        => list is { Length: > 0 } && list.Any(t => Same(t, tag));

    private static bool Same(string? a, string? b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
