using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;

namespace ClassSoftwareHub.Desktop.Data;

/// <summary>全局搜索的结果类别。</summary>
public enum SearchHitKind
{
    App,
    Tool,
    Page,
}

/// <summary>一条搜索结果。</summary>
public sealed class SearchHit
{
    public SearchHitKind Kind { get; init; }

    /// <summary>同类内的去重键：软件 id / 工具 id / 页面 tag。</summary>
    public string Key { get; init; } = "";

    public string Title { get; init; } = "";

    public string Subtitle { get; init; } = "";

    /// <summary>工具与页面用的字形码；软件留空（界面改用首字色块）。</summary>
    public string Glyph { get; init; } = "";

    /// <summary>软件命中来源：true＝名称命中，false＝简介/介绍命中。其余类别不看它。</summary>
    public bool MatchedName { get; init; }

    /// <summary>点开要导航到的 tag（页面与工具用；软件走 id，见 <see cref="Key"/>）。</summary>
    public string Target { get; init; } = "";

    // ── 以下几项是给界面模板直接绑的 ──────────────────────────────
    // 下拉列表用 AutoSuggestBox 的原生建议列表承载，ItemTemplate 里用 {Binding} 取属性；
    // 绑定不认方法调用，所以"显隐""首字"这些都得在这儿先算好。

    /// <summary>
    /// 右侧那行类型小字。
    /// ⚠️ 与站点的取舍不同：站点是按组显示（组标题写着"软件/内置工具/页面"），
    ///    桌面端走系统原生下拉，是个扁平列表、没有组标题，所以改由每行的这个小字交代类型。
    /// </summary>
    public string Badge => Kind switch
    {
        SearchHitKind.App => "软件",
        SearchHitKind.Tool => "内置工具",
        _ => "页面",
    };

    /// <summary>字形（工具与页面用；软件没有字形，界面改用首字色块）。</summary>
    public string IconGlyph => Kind == SearchHitKind.App ? "" : Glyph;

    /// <summary>首字（软件用 —— 搜索结果不等远程图标加载，与站点同）。</summary>
    public string IconLetter => Kind == SearchHitKind.App ? FirstLetter(Title) : "";

    public Visibility GlyphVisibility =>
        Kind == SearchHitKind.App ? Visibility.Collapsed : Visibility.Visible;

    public Visibility LetterVisibility =>
        Kind == SearchHitKind.App ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SubtitleVisibility =>
        Subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// ⛔ 别删：AutoSuggestBox 选中一项之后会把它 `ToString()` 的结果回填进输入框，
    /// 没有这个重写，框里就会变成类型名。
    /// </summary>
    public override string ToString() => Title;

    private static string FirstLetter(string title)
    {
        var text = title.Trim();
        if (text.Length == 0) return "?";
        return char.IsHighSurrogate(text[0]) && text.Length > 1 ? text[..2] : text[..1];
    }
}

/// <summary>一组结果（界面按组显示，组标题见 <see cref="SearchIndex.GroupTitle"/>）。</summary>
public sealed class SearchGroup
{
    public SearchHitKind Kind { get; init; }

    public IReadOnlyList<SearchHit> Hits { get; init; } = Array.Empty<SearchHit>();

    public string Title => SearchIndex.GroupTitle(Kind);
}

/// <summary>
/// 全局搜索（标题栏那个搜索框）的索引与匹配。
/// 桌面端的对应物是站点的 <c>src/gallery/searchIndex.ts</c> —— **匹配规则逐条对齐**，改一处要想着另一处。
///
/// <para>搜索范围（结果按类别分组）：</para>
/// <list type="number">
/// <item>软件 —— 软件名 / 一句话简介 / 详细介绍（数据来自 <c>ContentStore</c>，即站点的「软件数据/apps/」）</item>
/// <item>内置工具 —— 工具名 / 说明（清单只有 <see cref="ToolCatalog.All"/> 一份）</item>
/// <item>页面 —— 导航栏的各个页面（见下面的页面注册表）</item>
/// </list>
/// <para>
/// ⛔ 站点还有第 4 类「AI 导航」（站外站点）—— 桌面端没有这个页面，所以这里也没有，不是漏了。
/// </para>
///
/// <para>
/// 规则：不知道软件叫什么、只记得"它是干嘛的"也能搜到 ——
/// 名称命中的结果排在同组的简介命中之前；组内不超过 <see cref="LimitPerKind"/> 条。
/// 空关键词不走匹配，直接给"去哪儿"的页面快捷入口（与站点同）。
/// </para>
/// </summary>
public static class SearchIndex
{
    /// <summary>每组最多几条（与站点一致；超了靠继续打字缩小范围）。</summary>
    public const int LimitPerKind = 5;

    public static string GroupTitle(SearchHitKind kind) => kind switch
    {
        SearchHitKind.App => "软件",
        SearchHitKind.Tool => "内置工具",
        _ => "页面",
    };

    // ── ① 软件 ────────────────────────────────────────────────────

    /// <summary>软件：名称命中的排前面，简介/介绍命中排后面。</summary>
    private static List<SearchHit> SearchApps(string q, int limit)
    {
        var byName = new List<SearchHit>();
        var byIntro = new List<SearchHit>();

        foreach (var app in App.Content.Apps)
        {
            if (Normalize(app.Name).Contains(q, StringComparison.Ordinal))
            {
                byName.Add(AppHit(app, true));
            }
            else if (Normalize(app.Tagline).Contains(q, StringComparison.Ordinal)
                     || Normalize(app.Description).Contains(q, StringComparison.Ordinal))
            {
                byIntro.Add(AppHit(app, false));
            }
        }

        return byName.Concat(byIntro).Take(limit).ToList();
    }

    private static SearchHit AppHit(SoftwareApp app, bool byName) => new()
    {
        Kind = SearchHitKind.App,
        Key = app.Id,
        Title = app.Name,
        Subtitle = Clip(app.Tagline.Length > 0 ? app.Tagline : app.Description),
        MatchedName = byName,
    };

    // ── ② 内置工具 ────────────────────────────────────────────────

    /// <summary>内置工具：工具名命中优先，说明命中其次。</summary>
    private static List<SearchHit> SearchTools(string q, int limit)
    {
        var byName = new List<SearchHit>();
        var byDesc = new List<SearchHit>();

        foreach (var tool in ToolCatalog.All)
        {
            var hit = new SearchHit
            {
                Kind = SearchHitKind.Tool,
                Key = tool.Id,
                Title = tool.Name,
                Subtitle = Clip(tool.Desc),
                Glyph = tool.Glyph,
                Target = tool.Id,
            };

            if (Normalize(tool.Name).Contains(q, StringComparison.Ordinal)) byName.Add(hit);
            else if (Normalize(tool.Desc).Contains(q, StringComparison.Ordinal)) byDesc.Add(hit);
        }

        return byName.Concat(byDesc).Take(limit).ToList();
    }

    // ── ③ 页面 ────────────────────────────────────────────────────

    /// <summary>
    /// 页面：标题 / 说明 / 关键词三处任一命中即可。
    /// 空关键词时返回全部**顶层**页面（面板一开始的"去哪儿"快捷入口）。
    /// </summary>
    private static List<SearchHit> SearchPages(string q, int limit)
    {
        var hits = new List<SearchHit>();

        foreach (var page in Pages)
        {
            // 没有关键词＝快捷入口，只列导航栏上直接看得到的那几个（实验性子项不铺开，
            // 不然一聚焦就是 15 条；子项留着靠打字搜）。
            if (q.Length == 0)
            {
                if (!page.TopLevel) continue;
            }
            else if (!Normalize(page.Title).Contains(q, StringComparison.Ordinal)
                     && !Normalize(page.Desc).Contains(q, StringComparison.Ordinal)
                     && !Normalize(page.Keywords).Contains(q, StringComparison.Ordinal))
            {
                continue;
            }

            hits.Add(new SearchHit
            {
                Kind = SearchHitKind.Page,
                Key = page.Tag,
                Title = page.Title,
                Subtitle = page.Desc,
                Glyph = page.Glyph,
                Target = page.Tag,
            });

            if (hits.Count >= limit) break;
        }

        return hits;
    }

    /// <summary>页面注册表。顺序＝导航栏顺序（上半区 → 下半区），图标与导航项同码位。</summary>
    private sealed class PageDef
    {
        public string Tag { get; init; } = "";
        public string Title { get; init; } = "";
        public string Desc { get; init; } = "";
        public string Glyph { get; init; } = "";
        /// <summary>额外匹配词（中英都放，方便只记得英文/拼音时也能搜到）。</summary>
        public string Keywords { get; init; } = "";
        /// <summary>是不是导航栏上直接看得到的那一层（决定空关键词时列不列它）。</summary>
        public bool TopLevel { get; init; }
    }

    private static readonly List<PageDef> Pages = new()
    {
        new() { Tag = "home", Title = "首页", Desc = "回到软件卡片列表", Glyph = "\uE80F", TopLevel = true,
                Keywords = "home 首页 主页 全部软件 start" },
        new() { Tag = "apps", Title = "软件下载", Desc = "浏览全部软件，可按分类筛选", Glyph = "\uE719", TopLevel = true,
                Keywords = "apps app 软件 应用 下载 商店 分类" },
        new() { Tag = "tools", Title = "内置工具", Desc = "计时器、抽号、取色、编码等内置小工具", Glyph = "\uE71D", TopLevel = true,
                Keywords = "tools 工具 内置工具 小工具 计时器 二维码 取色 编码 哈希" },
        new() { Tag = "sidebar", Title = "侧边布局", Desc = "调整侧边栏收起条与浮窗的外观", Glyph = "\uE8A0", TopLevel = true,
                Keywords = "sidebar 侧边栏 收起 布局 浮窗 贴边" },
        new() { Tag = "experimental", Title = "实验性功能", Desc = "还在验证中的小功能", Glyph = "\uEA80", TopLevel = true,
                Keywords = "experimental 实验 实验性 尝鲜 测试" },
        new() { Tag = "downloads", Title = "任务进行", Desc = "查看下载与应用更新的进度", Glyph = "\uE896", TopLevel = true,
                Keywords = "downloads 任务 下载 进度 更新 安装" },
        new() { Tag = "submit", Title = "提交软件", Desc = "推荐软件 / 反馈收录", Glyph = "\uE898", TopLevel = true,
                Keywords = "submit 提交 投稿 收录 添加软件 推荐" },
        new() { Tag = "feedback", Title = "反馈中心", Desc = "报告问题、提出建议", Glyph = "\uE939", TopLevel = true,
                Keywords = "feedback 反馈 报错 出错 问题 bug 建议 意见 issue 修正 失效" },
        new() { Tag = "changelog", Title = "更新日志", Desc = "查看各版本的更新内容", Glyph = "\uE81C", TopLevel = true,
                Keywords = "changelog 更新日志 版本 更新内容 新功能" },
        new() { Tag = "settings", Title = "设置", Desc = "主题、材质、侧边栏、关于", Glyph = "\uE713", TopLevel = true,
                Keywords = "settings 设置 主题 外观 深色 浅色 材质 关于 更新" },

        new() { Tag = "machinecheck", Title = "本机核实", Desc = "拿本机已装软件跟清单对一遍", Glyph = "\uE7F4",
                Keywords = "machinecheck 本机 核实 检测 装机 检查 已装" },
        new() { Tag = "easiguard", Title = "白板专杀", Desc = "结束希沃白板5 的残留进程", Glyph = "\uEA99",
                Keywords = "easiguard 白板 专杀 希沃 easinote 进程 后台" },
        new() { Tag = "procguard", Title = "程序专杀", Desc = "按时间点结束指定程序", Glyph = "\uE7E8",
                Keywords = "procguard 程序 专杀 进程 结束 定时" },
        new() { Tag = "uninstaller", Title = "应用卸载", Desc = "卸载已安装的应用并清理残留", Glyph = "\uE74D",
                Keywords = "uninstaller 卸载 删除 移除 残留 uninstall 清理" },
        new() { Tag = "appdata", Title = "AppData 管理", Desc = "查看并清理 AppData 中的缓存目录", Glyph = "\uE8B7",
                Keywords = "appdata 缓存 清理 数据 文件夹 cache" },
    };

    // ── 汇总 ──────────────────────────────────────────────────────

    /// <summary>
    /// 全局搜索：返回已经分好组的结果（顺序＝软件 → 内置工具 → 页面）。
    /// 空关键词只返回页面快捷入口；一条都没有时返回空表（界面据此显示"没找到"）。
    /// </summary>
    public static List<SearchGroup> Query(string query, int limitPerKind = LimitPerKind)
    {
        var q = Normalize(query);

        if (q.Length == 0)
        {
            var quick = SearchPages("", Pages.Count(p => p.TopLevel));
            return quick.Count > 0
                ? new List<SearchGroup> { new() { Kind = SearchHitKind.Page, Hits = quick } }
                : new List<SearchGroup>();
        }

        var groups = new List<SearchGroup>
        {
            new() { Kind = SearchHitKind.App, Hits = SearchApps(q, limitPerKind) },
            new() { Kind = SearchHitKind.Tool, Hits = SearchTools(q, limitPerKind) },
            new() { Kind = SearchHitKind.Page, Hits = SearchPages(q, limitPerKind) },
        };

        return groups.Where(g => g.Hits.Count > 0).ToList();
    }

    private static string Normalize(string? value) => (value ?? "").Trim().ToLowerInvariant();

    /// <summary>简介太长时截断（卡片只显示一行，没必要拼一个长字符串）。与站点同：空白折叠成单空格，90 字封顶。</summary>
    private static string Clip(string? value, int max = 90)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(value ?? "", @"\s+", " ").Trim();
        return text.Length > max ? text[..max] + "…" : text;
    }
}
