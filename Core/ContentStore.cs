using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClassSoftwareHub.Desktop.Data;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 软件数据加载器（原生版）。
/// 逻辑与站点 src/gallery/data/index.ts 保持一致：
///   逐文件容错解析 → 坏文件跳过并记一条 DataIssue → 必填 id/name 校验
///   → id 重复跳过 → 按 JSON 里的 sort 排序 → 分类读取（坏了就从软件里现推）
///
/// 数据来源：
///   1) 开发/本机：ShellConfig.DevContentDir（站点工程 dist/content/，跑过 build-content.mjs 就有）
///   2) 正式：%LOCALAPPDATA%\ClassSoftwareHub\content（由 ContentUpdater 从站点 manifest 增量同步）
/// </summary>
public sealed class ContentStore
{
    private static readonly string[] Pick =
    {
        "id", "name", "icon", "category", "tagline", "description", "version",
        "size", "system", "website", "github", "notice", "store"
    };

    public List<SoftwareApp> Apps { get; } = new();
    public List<DownloadCategory> Categories { get; } = new();
    public List<DataIssue> Issues { get; } = new();

    /// <summary>系统镜像下载页的内容（text/mirror-sites.json）。</summary>
    public MirrorInfo Mirror { get; } = new();

    /// <summary>站点文字（text/ui.json 里桌面版需要的标题 / 版本号）。</summary>
    public UiText Ui { get; } = new();

    /// <summary>本次数据来自哪儿（界面上显示一下，方便确认桌面版是不是最新内容）。</summary>
    public string Source { get; private set; } = "";

    /// <summary>来源类型："cache"（本地缓存 / 远端内容包） | "dev"（开发目录回退） | ""。</summary>
    public string SourceKind { get; private set; } = "";

    /// <summary>来源的中文说明（设置页显示用）。</summary>
    public string SourceLabel => SourceKind switch
    {
        "cache" => "本地缓存（远端内容包）",
        "dev" => "开发目录（远端内容包还没发布，先用本机的）",
        "bundled" => "安装包自带的内容（离线也有清单，联网后自动更新）",
        _ => "（没有数据源）",
    };

    public string ContentVersion { get; private set; } = "";

    public bool HasData => Apps.Count > 0;

    public SoftwareApp? FindById(string id) => Apps.FirstOrDefault(a => a.Id == id);

    public string CategoryName(string key) => Categories.FirstOrDefault(c => c.Key == key)?.Name ?? key;

    public int CountInCategory(string key) => Apps.Count(a => a.Category == key);

    public void Load()
    {
        Apps.Clear();
        Categories.Clear();
        Issues.Clear();
        Mirror.Sites.Clear();

        var root = ResolveContentRoot(out var kind);
        SourceKind = kind;
        if (root is null)
        {
            Source = "（没有数据源）";
            return;
        }
        Source = root;

        LoadApps(Path.Combine(root, "apps"));
        LoadCategories(Path.Combine(root, "categories.json"));
        ApplyCategoryDisplay();
        LoadManifestVersion(Path.Combine(root, "manifest.json"));
        LoadMirror(Path.Combine(root, "text", "mirror-sites.json"));
        LoadUi(Path.Combine(root, "text", "ui.json"));
    }

    /// <summary>站点文字（拿不到就留空，界面用兜底文案）。</summary>
    private void LoadUi(string file)
    {
        try
        {
            if (!File.Exists(file)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            Ui.AppTitle = ReadString(root, "app.title");
            Ui.AppVersion = ReadString(root, "app.version");
            Ui.HomeTitle = ReadString(root, "home.title");
            Ui.HomeSubtitle = ReadString(root, "home.subtitle");

            // 其余文字（详情页标签、按钮文案…）整份留着，界面用 Ui.T(key, 兜底) 取
            Ui.All.Clear();
            foreach (var prop in root.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String)
                    Ui.All[prop.Name] = prop.Value.GetString() ?? "";
        }
        catch { /* 文字读不到不影响使用 */ }
    }

    /// <summary>系统镜像下载清单（读不到就留空，页面显示一条提示）。</summary>
    private void LoadMirror(string file)
    {
        try
        {
            if (!File.Exists(file)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            Mirror.Title = Or(ReadString(root, "title"), Mirror.Title);
            Mirror.Subtitle = ReadString(root, "subtitle");
            Mirror.Disclaimer = ReadString(root, "disclaimer");
            Mirror.Note = ReadString(root, "note");

            if (root.TryGetProperty("sites", out var sites) && sites.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in sites.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object) continue;
                    var url = ReadString(s, "url");
                    if (url.Length == 0) continue;
                    Mirror.Sites.Add(new MirrorSite
                    {
                        Name = ReadString(s, "name"),
                        Desc = ReadString(s, "desc"),
                        Url = url,
                        Color = ReadString(s, "color"),
                        Icon = ReadString(s, "icon"),
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Issues.Add(new DataIssue { File = "text/mirror-sites.json", Message = "镜像清单读取失败：" + FriendlyJsonHint(ex.Message) });
        }
    }

    private static string Or(string a, string b) => a.Length > 0 ? a : b;

    /// <summary>
    /// 按优先级找一个可用的内容目录。
    /// ⚠️ **正式版优先本地缓存**（%LOCALAPPDATA%\ClassSoftwareHub\content，由 ContentUpdater 同步）；
    /// 其次是**安装目录里自带的那份内容包**（{app}\content，装机就有，离线也有软件清单）；
    /// 开发目录只是最后兜底。DEBUG 构建把开发目录放最前面，方便改站点工程立刻看效果。
    /// </summary>
    private static string? ResolveContentRoot(out string kind)
    {
#if DEBUG
        var order = new[]
        {
            (ShellConfig.DevContentDir, "dev"),
            (ShellConfig.CachedContentDir, "cache"),
            (ShellConfig.BundledContentDir, "bundled"),
        };
#else
        var order = new[]
        {
            (ShellConfig.CachedContentDir, "cache"),
            (ShellConfig.BundledContentDir, "bundled"),
            (ShellConfig.DevContentDir, "dev"),
        };
#endif
        foreach (var (dir, k) in order)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)
                    && Directory.Exists(Path.Combine(dir, "apps")))
                {
                    kind = k;
                    return dir;
                }
            }
            catch { /* 忽略，试下一个 */ }
        }

        kind = "";
        return null;
    }

    private void LoadApps(string appsDir)
    {
        if (!Directory.Exists(appsDir))
        {
            Issues.Add(new DataIssue { File = "apps/", Message = "找不到软件数据目录" });
            return;
        }

        var parsed = new List<(SoftwareApp App, int Sort)>();
        var seenIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(appsDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            var file = Path.GetFileName(path);
            if (file.StartsWith('_')) continue;                 // 模板 / 说明文件不参与

            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception ex) { Issues.Add(new DataIssue { File = file, Message = "读不出来：" + ex.Message }); continue; }

            JsonDocument doc;
            try { doc = JsonDocument.Parse(text); }
            catch (Exception ex) { Issues.Add(new DataIssue { File = file, Message = FriendlyJsonHint(ex.Message) }); continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    Issues.Add(new DataIssue { File = file, Message = "最外层不是 { } 对象" });
                    continue;
                }

                var app = new SoftwareApp();
                foreach (var key in Pick)
                {
                    var value = ReadString(root, key);
                    switch (key)
                    {
                        case "id": app.Id = value; break;
                        case "name": app.Name = value; break;
                        case "icon": app.Icon = value; break;
                        case "category": app.Category = value; break;
                        case "tagline": app.Tagline = value; break;
                        case "description": app.Description = value; break;
                        case "version": app.Version = value; break;
                        case "size": app.Size = value; break;
                        case "system": app.System = value; break;
                        case "website": app.Website = value; break;
                        case "github": app.Github = value; break;
                        case "notice": app.Notice = value; break;
                        case "store": app.Store = value; break;
                    }
                }

                if (root.TryGetProperty("downloads", out var downloads) && downloads.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in downloads.EnumerateArray())
                    {
                        if (d.ValueKind != JsonValueKind.Object) continue;
                        var item = new DownloadItem
                        {
                            Platform = ReadString(d, "platform"),
                            Note = ReadString(d, "note"),
                            Size = ReadString(d, "size"),
                            Url = ReadString(d, "url"),
                            Hash = ReadString(d, "hash"),
                        };
                        if (item.Platform.Length > 0 || item.Url.Length > 0) app.Downloads.Add(item);
                    }
                }

                if (app.Id.Length == 0 || app.Name.Length == 0)
                {
                    Issues.Add(new DataIssue { File = file, Message = "缺少必填字段 id 或 name" });
                    continue;
                }
                if (seenIds.TryGetValue(app.Id, out var other))
                {
                    Issues.Add(new DataIssue { File = file, Message = $"id 重复：与 {other} 相同，本文件已跳过" });
                    continue;
                }
                seenIds[app.Id] = file;

                var sort = int.MaxValue;
                if (root.TryGetProperty("sort", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var sv))
                    sort = sv;

                parsed.Add((app, sort));
            }
        }

        foreach (var entry in parsed.OrderBy(e => e.Sort))
            Apps.Add(entry.App);
    }

    private void LoadCategories(string file)
    {
        if (!File.Exists(file))
        {
            RebuildCategoriesFromApps();
            Issues.Add(new DataIssue { File = "categories.json", Message = "分类文件缺失，已用软件里出现的分类临时顶替" });
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("最外层不是数组");

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new JsonException("数组元素不是对象");
                var key = ReadString(item, "key");
                var name = ReadString(item, "name");
                if (key.Length == 0 || name.Length == 0) throw new JsonException("缺少 key 或 name");
                Categories.Add(new DownloadCategory { Key = key, Name = name, Icon = ReadString(item, "icon") });
            }
        }
        catch (Exception ex)
        {
            Categories.Clear();
            RebuildCategoriesFromApps();
            Issues.Add(new DataIssue { File = "categories.json", Message = "分类读取失败（" + FriendlyJsonHint(ex.Message) + "），已用软件分类临时顶替" });
        }

        foreach (var c in Categories)
            c.Display = $"{c.Name}  {CountInCategory(c.Key)}";
    }

    private void RebuildCategoriesFromApps()
    {
        foreach (var app in Apps)
        {
            if (app.Category.Length == 0) continue;
            if (Categories.Any(c => c.Key == app.Category)) continue;
            Categories.Add(new DownloadCategory { Key = app.Category, Name = app.Category });
        }
    }

    /// <summary>给每个软件补上分类显示名（卡片徽标用；分类缺失时就是 key 本身）。</summary>
    private void ApplyCategoryDisplay()
    {
        foreach (var app in Apps)
            app.CategoryDisplay = app.Category.Length == 0 ? "" : CategoryName(app.Category);
    }

    private void LoadManifestVersion(string file)
    {
        try
        {
            if (!File.Exists(file)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            ContentVersion = ReadString(doc.RootElement, "contentVersion");
        }
        catch { /* 版本号读不到就算了，不影响使用 */ }
    }

    private static string ReadString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => (v.GetString() ?? "").Trim(),
            JsonValueKind.Number => v.ToString().Trim(),
            _ => ""
        };
    }

    /// <summary>把 JSON 报错翻译成人话（与网页端 friendlyHint 一致）。</summary>
    private static string FriendlyJsonHint(string raw)
    {
        if (raw.Contains("end of data") || raw.Contains("end of JSON") || raw.Contains("depth")) return "文件不完整：可能漏了结尾的 } 或 ]，或末尾多了逗号";
        if (raw.Contains("is invalid") || raw.Contains("Expected") || raw.Contains("cannot be parsed")) return "标点/引号有误：检查字段之间是否漏了逗号、引号是否配对";
        if (raw.Contains("escape") || raw.Contains("control character")) return "字符串里有非法字符（换行要写 \\n）";
        return "JSON 语法错误";
    }
}
