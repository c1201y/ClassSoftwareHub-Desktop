using System;
using System.Collections.Generic;
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
