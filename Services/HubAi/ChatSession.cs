using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.HubAi;

/// <summary>历史对话里的一条消息。只存**文本轮次**，不存工具调用与工具结果。</summary>
public sealed class SessionEntry
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    /// <summary>
    /// 这一轮 assistant 的思考过程（2026-10-18 加）。
    /// ⚠️ 之前不存，翻历史时"思考过程"整块消失，用户以为丢了 —— 它其实是对话内容的一部分。
    /// </summary>
    [JsonPropertyName("reasoning")]
    public string? Reasoning { get; set; }
}

/// <summary>一次完整的历史对话。</summary>
public sealed class ChatSession
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    /// <summary>列表上显示的标题，取自第一句提问。</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>这次对话用的模型（回答脚注上要显示）。</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>这次对话累计消耗的 token。</summary>
    [JsonPropertyName("tokens")]
    public int Tokens { get; set; }

    [JsonPropertyName("entries")]
    public List<SessionEntry> Entries { get; set; } = new();

    /// <summary>列表副标题用。⭐ 只读属性，不进 JSON。</summary>
    [JsonIgnore]
    public string DateText => CreatedAt.ToString("MM-dd HH:mm");
}

/// <summary>
/// 历史对话的本地存取（2026-10-18）。一次对话一个文件，放在 <c>AppDataDir\hubai-sessions\</c>。
///
/// <para>
/// ⛔ 刻意**只存 user / assistant 的文本**，不存工具调用与工具结果：
/// <list type="bullet">
///   <item>工具结果是"当时那台机器"的快照，过两天再看就是过期数据，留着只会误导；</item>
///   <item>只存文本可以让恢复后的对话直接继续，不会出现"有 tool 消息却找不到对应的
///         assistant.tool_calls"这种服务端直接 400 的破损结构。</item>
/// </list>
/// </para>
/// <para>
/// 文件按启动时生成的时间戳命名，列表按文件时间倒序 —— 不建索引文件，少一份可能写坏的东西。
/// </para>
/// </summary>
public static class ChatSessionStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>最多保留多少份历史。再老的自动删 —— 这东西只会越攒越多，用户不会主动清。</summary>
    private const int MaxSessions = 60;

    public static string Dir => Path.Combine(AppPaths.DataDir, "hubai-sessions");

    /// <summary>按时间倒序读全部历史。坏文件直接跳过，不影响其他的。</summary>
    public static List<ChatSession> LoadAll()
    {
        var list = new List<ChatSession>();

        try
        {
            if (!Directory.Exists(Dir)) return list;

            foreach (var file in Directory.GetFiles(Dir, "*.json"))
            {
                try
                {
                    var session = JsonSerializer.Deserialize<ChatSession>(File.ReadAllText(file), JsonOpts);
                    if (session is not null && session.Entries.Count > 0) list.Add(session);
                }
                catch (Exception ex)
                {
                    AppLog.Exception("hubai", ex, $"读取历史对话 {Path.GetFileName(file)}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("hubai", ex, "读取历史对话列表");
        }

        return list.OrderByDescending(s => s.CreatedAt).ToList();
    }

    /// <summary>
    /// 保存一次对话。返回会话 id（新建时生成，续聊时沿用）。
    /// ⚠️ 只保存有实际内容的轮次 —— 空对话不落盘，免得列表里全是噪声。
    /// </summary>
    public static string? Save(
        IReadOnlyList<AiMessage> messages,
        string fallbackTitle,
        string? existingId = null,
        string? model = null,
        int tokens = 0)
    {
        try
        {
            var entries = new List<SessionEntry>();

            foreach (var message in messages)
            {
                // system 是每次现拼的，tool 会破坏恢复后的消息结构（见类注释）
                if (message.Role != "user" && message.Role != "assistant") continue;

                // ⚠️ 有思考但没正文的那一轮也要留（模型可能只想着就调工具了）
                if (string.IsNullOrWhiteSpace(message.Content) && string.IsNullOrEmpty(message.Reasoning)) continue;

                entries.Add(new SessionEntry
                {
                    Role = message.Role,
                    Content = message.Content,
                    Reasoning = message.Role == "assistant" ? message.Reasoning : null,
                });
            }

            if (entries.Count == 0) return null;

            var id = string.IsNullOrEmpty(existingId)
                ? DateTime.Now.ToString("yyyyMMdd-HHmmss")
                : existingId;

            var previous = existingId is null ? null : Load(existingId);

            var session = new ChatSession
            {
                Id = id,
                CreatedAt = previous?.CreatedAt ?? DateTime.Now,
                Title = MakeTitle(entries, fallbackTitle),
                Model = model,
                // 续聊时累加，不是覆盖
                Tokens = (previous?.Tokens ?? 0) + tokens,
                Entries = entries,
            };

            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, id + ".json"), JsonSerializer.Serialize(session, JsonOpts));

            PruneOld();
            return id;
        }
        catch (Exception ex)
        {
            // 存历史失败不该影响对话本身，记一条就够
            AppLog.Exception("hubai", ex, "保存历史对话");
            return null;
        }
    }

    public static ChatSession? Load(string id)
    {
        try
        {
            var path = Path.Combine(Dir, id + ".json");
            if (!File.Exists(path)) return null;

            return JsonSerializer.Deserialize<ChatSession>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception ex)
        {
            AppLog.Exception("hubai", ex, "读取历史对话");
            return null;
        }
    }

    public static void Delete(string id)
    {
        try
        {
            var path = Path.Combine(Dir, id + ".json");
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLog.Exception("hubai", ex, "删除历史对话");
        }
    }

    private static string MakeTitle(List<SessionEntry> entries, string fallback)
    {
        var first = entries.FirstOrDefault(e => e.Role == "user")?.Content ?? fallback;

        // 去掉换行、压成一行，太长就截断 —— 列表上一行放不下就变成两行，很难看
        var line = string.Join(' ', first.Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (line.Length == 0) line = "未命名对话";

        return line.Length > 24 ? line[..24] + "…" : line;
    }

    /// <summary>超出上限就删最老的几份。</summary>
    private static void PruneOld()
    {
        try
        {
            var files = Directory.GetFiles(Dir, "*.json");
            if (files.Length <= MaxSessions) return;

            foreach (var file in files.OrderBy(File.GetCreationTimeUtc).Take(files.Length - MaxSessions))
                File.Delete(file);
        }
        catch (Exception ex)
        {
            AppLog.Exception("hubai", ex, "清理旧历史对话");
        }
    }
}
