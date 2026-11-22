using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;





public static class ConfigMigration
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    
    
    
    
    private static readonly HashSet<string> MachineLocalKeys = new(StringComparer.Ordinal)
    {
        "WindowLeft", "WindowTop", "WindowWidth", "WindowHeight", "WindowMaximized",
        "PaletteX", "PaletteY", "PalettePosVersion",
        "LastUrl", "ShotSaveDir", "DownloadFolder",
        "TimerAlarmPath", "AnnounceSoundPath", "OobeState", "WebView2MissingChoice",
        "OobeDraftJson", "OobePendingStep",
        "UpdatePendingPath", "UpdatePendingTag",
    };

    public static string Export(AppSettings settings)
    {
        var payload = new
        {
            schema = SchemaVersion,
            app = ShellConfig.AppName,
            version = ShellConfig.ShellVersion,
            exportedAt = DateTimeOffset.Now.ToString("o"),
            settings,
        };
        return JsonSerializer.Serialize(payload, JsonOpts);
    }

    
    public static AppSettings Import(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("迁移包不是有效的 JSON：" + ex.Message, ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("迁移包不是有效的 JSON 对象。");

            if (!root.TryGetProperty("schema", out var schema)
                || !schema.TryGetInt32(out var version)
                || version != SchemaVersion)
            {
                throw new FormatException($"迁移包版本不受支持，需要 schema={SchemaVersion}。");
            }

            if (!root.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
                throw new FormatException("迁移包缺少 settings 节点。");

            try
            {
                var model = JsonSerializer.Deserialize<AppSettings>(settings.GetRawText(), JsonOpts);
                return model ?? throw new FormatException("迁移包中的设置无法解析。");
            }
            catch (JsonException ex)
            {
                throw new FormatException("迁移包中的设置字段格式不正确：" + ex.Message, ex);
            }
        }
    }

    public static string BackupCurrent()
    {
        var dir = Path.Combine(AppPaths.DataDir, "backups");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"settings-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, Export(App.Settings.Current), new UTF8Encoding(false));
        return path;
    }

    public static IReadOnlyList<string> Preview(AppSettings imported)
    {
        var current = JsonSerializer.SerializeToNode(App.Settings.Current, JsonOpts)?.AsObject()
                      ?? throw new FormatException("当前设置无法序列化。");
        var incoming = JsonSerializer.SerializeToNode(imported, JsonOpts)?.AsObject()
                       ?? throw new FormatException("迁移设置无法序列化。");

        var lines = new List<string>();
        var count = 0;
        foreach (var property in incoming)
        {
            if (property.Value is null || MachineLocalKeys.Contains(property.Key)) continue;
            var oldValue = current[property.Key];
            var oldText = oldValue?.ToJsonString() ?? "未设置";
            var newText = property.Value.ToJsonString();
            if (oldText == newText) continue;

            count++;
            if (lines.Count < 18)
                lines.Add($"{FriendlyKey(property.Key)}：{TrimValue(oldText)} → {TrimValue(newText)}");
        }

        if (count > lines.Count)
            lines.Add($"另有 {count - lines.Count} 项差异未展开。");

        return lines;
    }

    public static IReadOnlyList<string> PathWarnings(AppSettings imported)
    {
        var warnings = new List<string>();
        if (imported.PinnedApps is null) return warnings;
        foreach (var app in imported.PinnedApps)
        {
            if (string.IsNullOrWhiteSpace(app.Path)) continue;
            if (app.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                || app.Path.StartsWith("::{", StringComparison.Ordinal)) continue;
            if (File.Exists(app.Path) || Directory.Exists(app.Path)) continue;
            warnings.Add($"快捷应用「{app.Name}」的路径在本机不存在：{app.Path}");
        }
        return warnings;
    }

    private static string FriendlyKey(string key)
    {
        return key switch
        {
            "Theme" => "主题",
            "Backdrop" => "窗口材质",
            "SplitTheme" => "分栏主题",
            "ExternalTheme" => "外部主题",
            "SidebarEnabled" => "侧边栏开关",
            "SidebarMode" => "侧边栏模式",
            "SidebarEdge" => "侧边栏贴边",
            "SidebarColumns" => "侧边栏列数",
            "SidebarDensity" => "侧边栏密度",
            "SidebarHeightRatio" => "侧边栏高度",
            "SidebarPinned" => "侧边栏固定",
            "ShotAutoSave" => "截图自动保存",
            "AutoStart" => "开机自启",
            "MinimizeOnStart" => "启动时最小化",
            "CloseToTray" => "关闭到托盘",
            "GithubDownloadRoute" => "下载线路",
            "AutoCheckUpdate" => "自动检查更新",
            "CacheKeepCount" => "更新包保留数量",
            "HubAiEnabled" => "Hub AI",
            "UserDisplayName" => "显示名称",
            "PinnedApps" => "快捷应用",
            "SidebarModules" => "侧边栏模块",
            "SidebarAppHotkeys" => "应用快捷键",
            "HotkeysEnabled" => "全局快捷键开关",
            "HotkeyShowMain" => "主窗口快捷键",
            "HotkeyAnnounce" => "公告快捷键",
            "HotkeyTimer" => "计时器快捷键",
            "HotkeyPptTouch" => "PPT 触控快捷键",
            "PaletteOnTop" => "调色盘置顶",
            "SidebarLongLabels" => "侧边栏长标签",
            _ => key,
        };
    }

    private static string TrimValue(string value)
    {
        value = value.Trim('"');
        if (value.Length <= 48) return value;
        return value[..45] + "…";
    }

    
    
    
    
    
    
    
    public static void MergeIntoCurrent(AppSettings imported)
    {
        var currentNode = JsonSerializer.SerializeToNode(App.Settings.Current, JsonOpts)?.AsObject()
                          ?? throw new FormatException("当前设置无法序列化。");
        var importedNode = JsonSerializer.SerializeToNode(imported, JsonOpts)?.AsObject()
                           ?? throw new FormatException("迁移设置无法序列化。");

        foreach (var property in importedNode)
        {
            if (property.Value is null || MachineLocalKeys.Contains(property.Key)) continue;
            currentNode[property.Key] = property.Value.DeepClone();
        }

        var merged = JsonSerializer.Deserialize<AppSettings>(currentNode.ToJsonString(), JsonOpts)
                     ?? throw new FormatException("合并后的设置无法解析。");

        App.Settings.Replace(merged);
    }
}
