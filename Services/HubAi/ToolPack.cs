using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.HubAi;

/// <summary>
/// 一个可交给模型调用的工具**声明**（2026-10-18）。
///
/// <para>
/// ⚠️⚠️ 这里只有"说明书"，**没有任何可执行代码**。
/// 工具包里放的是「叫什么、什么时候该用、参数长什么样」，真正干活的实现永远在主程序里
/// （按 <see cref="Id"/> 分派到内置动作）。理由：
/// <list type="bullet">
///   <item>远程下载并加载代码 = 把安全交给一个随时可能被改的文件，杀软也会直接拦；</item>
///   <item>内置实现是审计过的、有单元测试的，改一行就要发版，跑不掉；</item>
///   <item>包体只有几 KB，更新工具清单不必发新版程序。</item>
/// </list>
/// 所以：**白名单在主程序里**，JSON 里写了程序不认识的 id 一律拒绝执行。
/// </para>
/// </summary>
public sealed class ToolDefinition
{
    /// <summary>程序内认得的动作 id。⛔ 不认识的一律拒绝，不做模糊匹配。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>给用户看的名字。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// 给模型看的说明。<b>这一段直接决定模型会不会用错工具</b>：
    /// 写清楚"什么时候该调它""返回什么"，比写代码还重要。
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>参数 JSON Schema（OpenAI function calling 的 parameters 字段原样放这里）。</summary>
    [JsonPropertyName("parameters")]
    public JsonElement Parameters { get; set; }

    /// <summary>是否只读。<b>当前版本只允许 true</b> —— 写操作工具的执行通道还没有。</summary>
    [JsonPropertyName("readOnly")]
    public bool ReadOnly { get; set; } = true;
}

/// <summary>远程下发的工具包。</summary>
public sealed class ToolPack
{
    /// <summary>格式版本。程序只认自己支持的那一版，高了就拒绝（避免新版字段被旧程序误读）。</summary>
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

    /// <summary>程序支持的最高格式版本。</summary>
    public const int SupportedSchema = 1;
}

/// <summary>
/// 工具包的本地存取（2026-10-18）。装好的包放在 <c>AppDataDir\hubai-tools\toolpack.json</c>。
/// 没装过就是不存在 —— 这也正是"可选安装、不占空间"的落点：不装就一个字节都没有。
/// </summary>
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

    /// <summary>读已安装的包。没装、读坏、格式版本过高都返回 null。</summary>
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

    /// <summary>解析并校验。⛔ 校验失败一律返回 null —— 坏包比没包更糟（模型会拿到空工具表）。</summary>
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

            // 丢掉缺 id / 缺描述的条目：没有描述的工具模型不会用，留着只是噪声
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

    /// <summary>落盘。先写临时文件再替换，避免写一半断电留下半个文件。</summary>
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

            // 目录空了就一并收掉，别在用户机器上留一个空壳
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
