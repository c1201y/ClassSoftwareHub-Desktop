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
/// 数据来源（**只有网络这一条**，安装包不自带内容包）：
///   1) 正式：%LOCALAPPDATA%\ClassSoftwareHub\content（由 GithubContentSync 从站点仓库同步下来）
///   2) 开发：ShellConfig.DevContentDir（站点工程产物目录，仅本机存在）
/// 所以新装的机器首次启动必须联网 —— 拿不到就是空清单，界面会给出引导与重试入口。
/// </summary>
public sealed class ContentStore
{
    /// <summary>
    /// 内容被重新加载了（同步完成 / 手动刷新后触发）。界面订阅它就能在数据到手时立刻重建列表，
    /// 而不是等用户切页 —— ⛔ 别去掉：安装包不再自带清单后，首启的"空 → 有"全靠这个通知。
    /// ⚠️ 事件在 UI 线程上触发（Load 的调用点都在 UI 线程），订阅方若不确定请自行用 DispatcherQueue 兜一层。
    /// </summary>
    public event Action? Changed;

    /// <summary>是否正在从网络同步内容（首启空清单时用来区分"正在获取"和"没取到"）。</summary>
    public bool IsSyncing { get; private set; }

    /// <summary>
    /// 内容被重新读了几次。<see cref="Load"/> 每跑一次 +1。
    ///
    /// ⛔ 给界面做"内容真的变了没有"的判据用，别拿 <see cref="ContentVersion"/> 或
    /// <see cref="Source"/> 代替（2026-10-05 踩到）：manifest.json 不再同步，ContentVersion 恒为空；
    /// Source 又永远是同一个目录路径 —— 两者组合根本判不出"同步把 16 个补成了 84 个"，
    /// 于是首启时页面会一直停在旧列表上，非等用户切页才刷新。
    /// ⚠️ 它只在 <see cref="Load"/> 里涨，纯粹切页不会变 —— 所以不会破坏"切回来就别重建"的优化。
    /// </summary>
    public int Revision { get; private set; }

    /// <summary>同步状态变化（进入/退出同步中），界面据此切换"正在获取软件清单…"这类提示。</summary>
    public void SetSyncing(bool value)
    {
        if (IsSyncing == value) return;
        IsSyncing = value;
        Changed?.Invoke();
    }

    private static readonly string[] Pick =
    {
        "id", "name", "icon", "category", "tagline", "description", "version",
        "size", "system", "website", "github", "notice", "store"
    };

    public List<SoftwareApp> Apps { get; } = new();
    public List<DownloadCategory> Categories { get; } = new();
    public List<DataIssue> Issues { get; } = new();

    /// <summary>站点文字（text/ui.json 里桌面版需要的标题 / 版本号）。</summary>
    public UiText Ui { get; } = new();

    /// <summary>本次数据来自哪儿（界面上显示一下，方便确认桌面版是不是最新内容）。</summary>
    public string Source { get; private set; } = "";

    /// <summary>来源类型："cache"（联网同步下来的内容） | "dev"（开发目录） | ""（还没拿到）。</summary>
    public string SourceKind { get; private set; } = "";

    /// <summary>来源的中文说明（设置页显示用）。</summary>
    public string SourceLabel => SourceKind switch
    {
        "cache" => "本地内容（从站点仓库同步）",
        "dev" => "开发目录（站点工程产物，仅本机开发用）",
        _ => "（尚未获取到内容）",
    };

    public string ContentVersion { get; private set; } = "";

    public bool HasData => Apps.Count > 0;

    public SoftwareApp? FindById(string id) => Apps.FirstOrDefault(a => a.Id == id);

    public string CategoryName(string key) => Categories.FirstOrDefault(c => c.Key == key)?.Name ?? key;

    public int CountInCategory(string key) => Apps.Count(a => a.Category == key);

    public void Load()
    {
        Revision++;                     // 先涨再读：顺序不重要，重要的是"每次 Load 都换一个值"
        Apps.Clear();
        Categories.Clear();
        Issues.Clear();

        var root = ResolveContentRoot(out var kind);
        SourceKind = kind;
        if (root is null)
        {
            Source = "（无数据源）";
            Changed?.Invoke();          // 让"正在获取 / 没取到"的空清单提示能立刻刷出来
            return;
        }
        Source = root;

        LoadApps(Path.Combine(root, "apps"));
        LoadCategories(Path.Combine(root, "categories.json"));
        ApplyCategoryDisplay();
        LoadManifestVersion(Path.Combine(root, "manifest.json"));
        LoadUi(Path.Combine(root, "text", "ui.json"));

        Changed?.Invoke();
    }

    /// <summary>
    /// 站点文字（拿不到就留空，界面用兜底文案）。
    /// ⚠️ 站点仓库现在**没有放** text/ui.json（见 <see cref="Data.UiText"/> 的说明），
    /// 所以正式环境走到这儿一定是空 —— 不是 bug，别去"修"它。
    /// </summary>
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

    /// <summary>
    /// 找一个可用的内容目录。**只有两个候选，没有"安装包自带"这一档**（2026-10-05 起）：
    ///   · 正式版：本地缓存（%LOCALAPPDATA%\ClassSoftwareHub\content，由 GithubContentSync 同步）；
    ///   · 开发机：站点工程产物目录（正式用户机器上不存在，自动跳过）。
    /// DEBUG 把开发目录放最前面，方便改站点工程立刻看效果。
    /// </summary>
    private static string? ResolveContentRoot(out string kind)
    {
#if DEBUG
        var order = new[]
        {
            (ShellConfig.DevContentDir, "dev"),
            (ShellConfig.CachedContentDir, "cache"),
        };
#else
        var order = new[]
        {
            (ShellConfig.CachedContentDir, "cache"),
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
            Issues.Add(new DataIssue { File = "apps/", Message = "未找到软件数据目录" });
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
            catch (Exception ex) { Issues.Add(new DataIssue { File = file, Message = "读取失败：" + ex.Message }); continue; }

            JsonDocument doc;
            try { doc = JsonDocument.Parse(text); }
            catch (Exception ex) { Issues.Add(new DataIssue { File = file, Message = FriendlyJsonHint(ex.Message) }); continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    Issues.Add(new DataIssue { File = file, Message = "顶层结构不是 { } 对象" });
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
            Issues.Add(new DataIssue { File = "categories.json", Message = "分类文件缺失，已用软件中出现过的分类临时替代" });
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
            Issues.Add(new DataIssue { File = "categories.json", Message = "分类读取失败（" + FriendlyJsonHint(ex.Message) + "），已用软件中的分类临时替代" });
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
        if (raw.Contains("end of data") || raw.Contains("end of JSON") || raw.Contains("depth")) return "文件不完整：可能缺少结尾的 } 或 ]，或末尾存在多余逗号";
        if (raw.Contains("is invalid") || raw.Contains("Expected") || raw.Contains("cannot be parsed")) return "标点或引号有误：请检查字段之间是否缺少逗号、引号是否配对";
        if (raw.Contains("escape") || raw.Contains("control character")) return "字符串中存在非法字符（换行需写作 \\n）";
        return "JSON 语法错误";
    }
}
