using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.HubAi;
















public sealed class ToolDefinition
{
    
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    
    
    
    
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    
    [JsonPropertyName("parameters")]
    public JsonElement Parameters { get; set; }

    
    [JsonPropertyName("readOnly")]
    public bool ReadOnly { get; set; } = true;
}


public sealed class ToolPack
{
    
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("updated")]
    public string Updated { get; set; } = "";

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("tools")]
    public List<ToolDefinition> Tools { get; set; } = new();

    
    public const int SupportedSchema = 1;
}





public static class ToolPackStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Dir => Path.Combine(AppPaths.DataDir, "hubai-tools");

    public static string FilePath => Path.Combine(Dir, "toolpack.json");

    public static bool IsInstalled
    {
        get
        {
            try { return File.Exists(FilePath); }
            catch { return false; }
        }
    }

    
    public static ToolPack? Load()
    {
        try
        {
            if (!IsInstalled) return null;
            return Parse(File.ReadAllText(FilePath));
        }
        catch (Exception ex)
        {
            AppLog.Exception("hubai", ex, "读取已安装工具包");
            return null;
        }
    }

    
    public static ToolPack? Parse(string json)
    {
        try
        {
            var pack = JsonSerializer.Deserialize<ToolPack>(json, JsonOpts);
            if (pack is null) return null;

            if (pack.SchemaVersion <= 0 || pack.SchemaVersion > ToolPack.SupportedSchema)
            {
                AppLog.Warn("hubai", $"工具包格式版本 {pack.SchemaVersion} 不受支持（本程序支持 {ToolPack.SupportedSchema}）");
                return null;
            }

            
            pack.Tools = pack.Tools
                .Where(t => !string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.Description))
                .ToList();

            if (pack.Tools.Count == 0) return null;

            return pack;
        }
        catch (JsonException ex)
        {
            AppLog.Exception("hubai", ex, "解析工具包");
            return null;
        }
    }

    
    public static bool Save(string json)
    {
        try
        {
            Directory.CreateDirectory(Dir);

            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);

            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Exception("hubai", ex, "保存工具包");
            return false;
        }
    }

    public static void Remove()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);

            
            if (Directory.Exists(Dir) && Directory.GetFileSystemEntries(Dir).Length == 0)
                Directory.Delete(Dir);

            AppLog.Step("hubai", "已移除工具包");
        }
        catch (Exception ex)
        {
            AppLog.Exception("hubai", ex, "移除工具包");
        }
    }
}
