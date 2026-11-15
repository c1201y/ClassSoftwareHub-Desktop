using System;
using System.Collections.Generic;
using System.Linq;
using ClassSoftwareHub.Desktop.Services.Docs;
using Microsoft.UI.Xaml;

namespace ClassSoftwareHub.Desktop.Data;


public enum SearchHitKind
{
    App,
    Tool,
    Doc,
    Page,
}


public sealed class SearchHit
{
    public SearchHitKind Kind { get; init; }

    
    public string Key { get; init; } = "";

    public string Title { get; init; } = "";

    public string Subtitle { get; init; } = "";

    
    public string Glyph { get; init; } = "";

    
    public bool MatchedName { get; init; }

    
    public string Target { get; init; } = "";

    
    
    

    
    
    
    
    
    public string Badge => Kind switch
    {
        SearchHitKind.App => "软件",
        SearchHitKind.Tool => "内置工具",
        SearchHitKind.Doc => "文章",
        _ => "页面",
    };

    
    public string IconGlyph => Kind == SearchHitKind.App ? "" : Glyph;

    
    public string IconLetter => Kind == SearchHitKind.App ? FirstLetter(Title) : "";

    public Visibility GlyphVisibility =>
        Kind == SearchHitKind.App ? Visibility.Collapsed : Visibility.Visible;

    public Visibility LetterVisibility =>
        Kind == SearchHitKind.App ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SubtitleVisibility =>
        Subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    
    
    
    
    public override string ToString() => Title;

    private static string FirstLetter(string title)
    {
        var text = title.Trim();
        if (text.Length == 0) return "?";
        return char.IsHighSurrogate(text[0]) && text.Length > 1 ? text[..2] : text[..1];
    }
}


public sealed class SearchGroup
{
    public SearchHitKind Kind { get; init; }

    public IReadOnlyList<SearchHit> Hits { get; init; } = Array.Empty<SearchHit>();

    public string Title => SearchIndex.GroupTitle(Kind);
}






















public static class SearchIndex
{
    
    public const int LimitPerKind = 5;

    public static string GroupTitle(SearchHitKind kind) => kind switch
    {
        SearchHitKind.App => "软件",
        SearchHitKind.Tool => "内置工具",
        SearchHitKind.Doc => "文档库",
        _ => "页面",
    };

    

    
    private static List<SearchHit> SearchApps(string q, string[] parts, int limit)
    {
        var byName = new List<SearchHit>();
        var byIntro = new List<SearchHit>();

        foreach (var app in App.Content.Apps)
        {
            if (Hit(app.Name, q, parts))
            {
                byName.Add(AppHit(app, true));
            }
            else if (Hit(app.Tagline, q, parts) || Hit(app.Description, q, parts))
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

    

    
    private static List<SearchHit> SearchTools(string q, string[] parts, int limit)
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

            if (Hit(tool.Name, q, parts)) byName.Add(hit);
            else if (Hit(tool.Desc, q, parts)) byDesc.Add(hit);
        }

        return byName.Concat(byDesc).Take(limit).ToList();
    }

    

    
    
    
    
    
    private static List<SearchHit> SearchDocs(string q, string[] parts, int limit)
    {
        var byTitle = new List<SearchHit>();
        var byMeta = new List<SearchHit>();

        foreach (var article in DocLibrary.Articles)
        {
            var hit = new SearchHit
            {
                Kind = SearchHitKind.Doc,
                Key = article.Id,
                Title = article.Title,
                Subtitle = Clip(article.Summary),
                Glyph = "\uE8A5",
                Target = article.Id,
            };

            if (Hit(article.Title, q, parts)) byTitle.Add(hit);
            else if (Hit(article.Summary, q, parts)
                     || Hit(article.Author, q, parts)
                     || Hit(string.Join(" ", article.Tags), q, parts))
                byMeta.Add(hit);
        }

        return byTitle.Concat(byMeta).Take(limit).ToList();
    }

    

    
    
    
    
    private static List<SearchHit> SearchPages(string q, string[] parts, int limit)
    {
        var hits = new List<SearchHit>();

        foreach (var page in Pages)
        {
            
            
            if (q.Length == 0)
            {
                if (!page.TopLevel) continue;
            }
            else if (!Hit(page.Title, q, parts)
                     && !Hit(page.Desc, q, parts)
                     && !Hit(page.Keywords, q, parts))
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

    
    private sealed class PageDef
    {
        public string Tag { get; init; } = "";
        public string Title { get; init; } = "";
        public string Desc { get; init; } = "";
        public string Glyph { get; init; } = "";
        
        public string Keywords { get; init; } = "";
        
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
        new() { Tag = "announce", Title = "教室喊话", Desc = "接收远程喊话，也可向已配对的电脑发送", Glyph = "\uE720", TopLevel = true,
                Keywords = "announce 喊话 教室喊话 通知 广播 消息 配对码 全屏 朗读" },
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
        
        new() { Tag = "about", Title = "关于", Desc = "版本、反馈与更新日志、回声洞、授权、条款", Glyph = "\uE946", TopLevel = true,
                Keywords = "about 关于 版本 版权 许可 license 授权 激活 激活码 条款 隐私政策 用户协议 免责声明 回声洞 反馈 更新日志 作者" },
        new() { Tag = "settings", Title = "设置", Desc = "主题、材质、侧边栏、下载位置", Glyph = "\uE713", TopLevel = true,
                Keywords = "settings 设置 主题 外观 深色 浅色 材质 更新 缓存 日志" },

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
        new() { Tag = "startup", Title = "开机启动项", Desc = "管理开机时自动启动的程序", Glyph = "\uE945",
                Keywords = "startup 开机 启动项 自启 自启动 开机启动 autostart run 关掉" },
        new() { Tag = "repair", Title = "系统修复", Desc = "修复系统文件、组件存储与网络", Glyph = "\uE7EF",
                Keywords = "repair 系统修复 修复 sfc dism chkdsk 网络重置 组件存储 磁盘 scannow" },
    };

    

    
    
    
    
    public static List<SearchGroup> Query(string query, int limitPerKind = LimitPerKind)
    {
        var q = Normalize(query);

        
        
        var parts = (query ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(p => p.Length > 0)
            .Distinct()
            .ToArray();

        if (q.Length == 0)
        {
            var quick = SearchPages("", Array.Empty<string>(), Pages.Count(p => p.TopLevel));
            return quick.Count > 0
                ? new List<SearchGroup> { new() { Kind = SearchHitKind.Page, Hits = quick } }
                : new List<SearchGroup>();
        }

        var groups = new List<SearchGroup>
        {
            new() { Kind = SearchHitKind.App, Hits = SearchApps(q, parts, limitPerKind) },
            new() { Kind = SearchHitKind.Tool, Hits = SearchTools(q, parts, limitPerKind) },
            new() { Kind = SearchHitKind.Doc, Hits = SearchDocs(q, parts, limitPerKind) },
            new() { Kind = SearchHitKind.Page, Hits = SearchPages(q, parts, limitPerKind) },
        };

        return groups.Where(g => g.Hits.Count > 0).ToList();
    }

    
    
    
    
    
    
    
    private static bool Hit(string? text, string q, string[] parts)
    {
        var t = Normalize(text);
        if (t.Length == 0) return false;

        if (t.Contains(q, StringComparison.Ordinal)) return true;
        if (parts.Length < 2) return false;

        foreach (var part in parts)
            if (!t.Contains(part, StringComparison.Ordinal)) return false;

        return true;
    }

    
    
    
    
    
    
    
    private static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            
            if (ch is ' ' or '\t' or '-' or '_' or '.' or '/' or '\\' or '·' or '—' or '–') continue;
            sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    
    private static string Clip(string? value, int max = 90)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(value ?? "", @"\s+", " ").Trim();
        return text.Length > max ? text[..max] + "…" : text;
    }
}
