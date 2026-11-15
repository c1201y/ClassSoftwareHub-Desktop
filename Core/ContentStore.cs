using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClassSoftwareHub.Desktop.Data;

namespace ClassSoftwareHub.Desktop.Core;












public sealed class ContentStore
{
    
    
    
    
    
    public event Action? Changed;

    
    public bool IsSyncing { get; private set; }

    
    
    
    
    
    
    
    
    
    public int Revision { get; private set; }

    
    public void SetSyncing(bool value)
    {
        if (IsSyncing == value) return;
        IsSyncing = value;
        Changed?.Invoke();
    }

    private static readonly string[] Pick =
    {
        "id", "name", "icon", "category", "tagline", "description", "version",
        "size", "system", "website", "github",
        
        
        
        "author", "license",
        "notice", "store"
    };

    public List<SoftwareApp> Apps { get; } = new();
    public List<DownloadCategory> Categories { get; } = new();

    
    public List<DownloadPlatform> Platforms { get; } = new();

    public List<DataIssue> Issues { get; } = new();

    
    public UiText Ui { get; } = new();

    
    public string Source { get; private set; } = "";

    
    public string SourceKind { get; private set; } = "";

    
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

    
    public int CountInPlatform(string key) => Apps.Count(a => a.SupportsPlatform(key));

    public string PlatformName(string key) => Platforms.FirstOrDefault(p => p.Key == key)?.Name ?? key;


    public void Load()
    {
        Revision++;                     
        Apps.Clear();
        Categories.Clear();
        Platforms.Clear();
        Issues.Clear();

        var root = ResolveContentRoot(out var kind);
        SourceKind = kind;
        if (root is null)
        {
            Source = "（无数据源）";
            Changed?.Invoke();          
            return;
        }
        Source = root;

        LoadApps(Path.Combine(root, "apps"));
        LoadCategories(Path.Combine(root, "categories.json"));
        LoadPlatforms(Path.Combine(root, "platforms.json"));
        ApplyCategoryDisplay();
        LoadManifestVersion(Path.Combine(root, "manifest.json"));
        LoadUi(Path.Combine(root, "text", "ui.json"));

        Changed?.Invoke();
    }

    
    
    
    
    
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

            
            Ui.All.Clear();
            foreach (var prop in root.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String)
                    Ui.All[prop.Name] = prop.Value.GetString() ?? "";
        }
        catch {  }
    }

    
    
    
    
    
    
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
            catch {  }
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
            if (file.StartsWith('_')) continue;                 

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
                        case "author": app.Author = value; break;
                        case "license": app.License = value; break;
                        case "notice": app.Notice = value; break;
                        case "store": app.Store = value; break;
                    }
                }

                if (root.TryGetProperty("platforms", out var plats) && plats.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in plats.EnumerateArray())
                    {
                        if (p.ValueKind != JsonValueKind.String) continue;
                        var key = (p.GetString() ?? "").Trim();
                        if (key.Length > 0 && !app.Platforms.Contains(key)) app.Platforms.Add(key);
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
                            
                            
                            DeclaredKind = ReadString(d, "kind"),
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

    
    
    
    
    
    
    
    private void LoadPlatforms(string file)
    {
        if (!File.Exists(file)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("最外层不是数组");

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var key = ReadString(item, "key");
                if (key.Length == 0) continue;
                var name = ReadString(item, "name");
                Platforms.Add(new DownloadPlatform { Key = key, Name = name.Length > 0 ? name : key });
            }
        }
        catch (Exception ex)
        {
            Platforms.Clear();
            Issues.Add(new DataIssue { File = "platforms.json", Message = "平台清单读取失败：" + FriendlyJsonHint(ex.Message) });
        }
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
        catch {  }
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

    
    private static string FriendlyJsonHint(string raw)
    {
        if (raw.Contains("end of data") || raw.Contains("end of JSON") || raw.Contains("depth")) return "文件不完整：可能缺少结尾的 } 或 ]，或末尾存在多余逗号";
        if (raw.Contains("is invalid") || raw.Contains("Expected") || raw.Contains("cannot be parsed")) return "标点或引号有误：请检查字段之间是否缺少逗号、引号是否配对";
        if (raw.Contains("escape") || raw.Contains("control character")) return "字符串中存在非法字符（换行需写作 \\n）";
        return "JSON 语法错误";
    }
}
