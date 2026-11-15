using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassSoftwareHub.Desktop.Data;



















public sealed class NavPlaceable
{
    
    public string Tag { get; init; } = "";

    public string Name { get; init; } = "";

    
    public ToolGroup Group { get; init; } = ToolGroup.Classroom;

    
    public string Glyph { get; init; } = "";

    
    public string Desc { get; init; } = "";

    
    public bool DefaultTopLevel { get; init; }

    
    
    
    
    public bool AboveFunctionPlot { get; init; }
}



















public static class NavLayout
{
    
    public static readonly IReadOnlyList<string> FixedTopLevel = new[]
    {
        "home", "apps", "hubai", "sidebar", "function-plot",
    };

    
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
            
            
            
            
            Tag = "ppttouch", Name = "PowerPoint 触控优化", Group = ToolGroup.System,
            Glyph = "\uE786",
            Desc = "放映时轻点屏幕翻到下一页，上下滑动切换页面，长按弹出放映菜单。",
            DefaultTopLevel = true,
        },
    };

    
    
    
    
    
    
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

    
    public static IReadOnlyList<NavPlaceable> All { get; } = Pages.Concat(Tools).ToList();

    
    public static NavPlaceable? Find(string? tag)
        => string.IsNullOrEmpty(tag)
            ? null
            : All.FirstOrDefault(p => string.Equals(p.Tag, tag, StringComparison.OrdinalIgnoreCase));

    
    public static bool IsTopLevel(string? tag)
    {
        if (Find(tag) is not { } placeable) return true;   

        var s = App.Settings.Current;

        
        if (Contains(s.NavTopLevel, tag)) return true;
        if (Contains(s.NavSecondLevel, tag)) return false;

        return placeable.DefaultTopLevel;
    }

    
    
    
    
    public static void SetTopLevel(string tag, bool topLevel)
    {
        if (Find(tag) is not { } placeable) return;
        if (IsTopLevel(tag) == topLevel) return;

        var s = App.Settings.Current;

        var promoted = (s.NavTopLevel ?? Array.Empty<string>()).ToList();
        var demoted = (s.NavSecondLevel ?? Array.Empty<string>()).ToList();

        promoted.RemoveAll(t => Same(t, tag));
        demoted.RemoveAll(t => Same(t, tag));

        
        if (topLevel != placeable.DefaultTopLevel)
            (topLevel ? promoted : demoted).Add(tag);

        s.NavTopLevel = promoted.ToArray();
        s.NavSecondLevel = demoted.ToArray();
    }

    
    
    
    
    
    public static IReadOnlyList<NavPlaceable> SecondLevel(ToolGroup group)
        => All.Where(p => p.Group == group && !IsTopLevel(p.Tag)).ToList();

    private static bool Contains(string[]? list, string? tag)
        => list is { Length: > 0 } && list.Any(t => Same(t, tag));

    private static bool Same(string? a, string? b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
