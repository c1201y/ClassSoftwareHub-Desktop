using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.HubAi;

/// <summary>
/// 历史对话里的一个消息节点。
/// <para>
/// ⛔⛔ 2026-10-23 起是**树**，不再是数组：同一条 <see cref="ParentId"/> 下的多个节点
/// 就是同一个位置的多个版本（对提问"编辑后重发"、对回答"重新生成"都只是长出一个兄弟节点）。
/// 旧文件（线性 entries、没有 id/parentId）由 <see cref="ChatSession.EnsureTree"/> 自动补齐，
/// 不用迁移脚本。
/// </para>
/// </summary>
public sealed class SessionEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>父节点；根为 null。</summary>
    [JsonPropertyName("parentId")]
    public string? ParentId { get; set; }

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

    /// <summary>当前显示的那一支的叶子节点（2026-10-23 加）。空 = 取最后一条。</summary>
    [JsonPropertyName("currentId")]
    public string? CurrentId { get; set; }

    [JsonPropertyName("entries")]
    public List<SessionEntry> Entries { get; set; } = new();

    /// <summary>列表副标题用。⭐ 只读属性，不进 JSON。</summary>
    [JsonIgnore]
    public string DateText => CreatedAt.ToString("MM-dd HH:mm");

    /// <summary>
    /// 补齐树结构。兼容三种历史数据：
    /// ① 旧版线性文件（没有 id、没有 parentId）→ 补 id 并串成一条链；
    /// ② 有 id 但 currentId 缺失 → 指到最后一条；
    /// ③ 正常树 → 只校验 currentId 是否还存在。
    /// </summary>
    public void EnsureTree()
    {
        for (var i = 0; i < Entries.Count; i++)
        {
            if (!string.IsNullOrEmpty(Entries[i].Id)) continue;

            // ⛔⛔ 必须**确定性**生成，不能用随机 id。
            //    旧文件在磁盘上没有 id，每次加载都补一遍 —— 若每次补出来的不一样，
            //    页面拿到的 id 和 `SaveBranch` 里 `Load()` 补出来的就会对不上，
            //    新节点挂到 parentId 上等于挂到空处，整棵树直接断掉（2026-10-23 踩过）。
            var candidate = $"e{i}";
            var n = i;
            while (Entries.Any(e => string.Equals(e.Id, candidate, StringComparison.Ordinal)))
                candidate = $"e{n++}x";

            Entries[i].Id = candidate;
        }

        // 全新格式里根节点也是 null，所以"所有节点都没父"才当作旧版线性数据
        if (Entries.Count > 1 && Entries.All(e => string.IsNullOrEmpty(e.ParentId)))
            for (var i = 1; i < Entries.Count; i++)
                Entries[i].ParentId = Entries[i - 1].Id;

        if (string.IsNullOrEmpty(CurrentId) || Entries.All(e => e.Id != CurrentId))
            CurrentId = Entries.Count > 0 ? Entries[^1].Id : null;
    }

    /// <summary>当前分支：根 → 叶子。</summary>
    public List<SessionEntry> Branch() => BranchUpTo(CurrentId);

    /// <summary>根 → 指定节点（含自己）。id 为空时返回空表。</summary>
    public List<SessionEntry> BranchUpTo(string? entryId)
    {
        var result = new List<SessionEntry>();
        if (string.IsNullOrEmpty(entryId)) return result;

        var byId = Entries
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var cursor = entryId;
        var guard = 0;

        while (!string.IsNullOrEmpty(cursor)
               && byId.TryGetValue(cursor, out var entry)
               && guard++ < 4096)
        {
            result.Add(entry);
            cursor = entry.ParentId;
        }

        result.Reverse();
        return result;
    }

    /// <summary>同一个父节点下、同角色的兄弟节点（按写入顺序）。分支切换靠它。</summary>
    public List<SessionEntry> Siblings(SessionEntry entry) =>
        Entries
            .Where(e => e.Role == entry.Role && string.Equals(e.ParentId, entry.ParentId, StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// 从某个节点一路往下走到叶子（有多条子分支时取最后写入的那条）。
    /// 切换兄弟节点时要落到它的叶子上 —— 否则只显示半截，后面的回合看起来"没了"。
    /// </summary>
    public string LeafOf(string entryId)
    {
        var cursor = entryId;
        var guard = 0;

        while (guard++ < 4096)
        {
            var child = Entries.LastOrDefault(e => string.Equals(e.ParentId, cursor, StringComparison.Ordinal));
            if (child is null) return cursor;
            cursor = child.Id;
        }

        return cursor;
    }
}

/// <summary>
/// 历史对话的本地存取（2026-10-18，2026-10-23 改为树）。一次对话一个文件，
/// 放在 <c>AppDataDir\hubai-sessions\</c>。
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
/// 文件按创建时间命名，列表按时间倒序 —— 不建索引文件，少一份可能写坏的东西。
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

    /// <summary>节点 id（短、可读、无冲突即可，不需要全局唯一）。</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..10];

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
                    if (session is null || session.Entries.Count == 0) continue;

                    session.EnsureTree();
                    list.Add(session);
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
    /// 把一段新产生的轮次挂到 <paramref name="parentId"/> 下面（null = 从空白的根开始）。
    ///
    /// <para>
    /// ⭐ 这是树的核心：不再"整篇覆盖"，而是**只追加一条链**。
    /// 于是"编辑重发"和"重新生成"都变成"在同一个父节点下多一个兄弟"，
    /// 旧分支原样留在文件里，随时可以切回去。
    /// </para>
    /// </summary>
    /// <returns>会话 id（新建时生成）；没有可存的内容时返回原 id。</returns>
    public static string? SaveBranch(
        string? sessionId,
        string? parentId,
        IReadOnlyList<AiMessage> turns,
        string fallbackTitle,
        string? model = null,
        int tokens = 0)
    {
        try
        {
            var session = (string.IsNullOrEmpty(sessionId) ? null : Load(sessionId))
                          ?? new ChatSession
                          {
                              Id = DateTime.Now.ToString("yyyyMMdd-HHmmss"),
                              CreatedAt = DateTime.Now,
                          };

            session.EnsureTree();

            var cursor = parentId;
            var added = 0;

            foreach (var message in turns)
            {
                if (message.Role != "user" && message.Role != "assistant") continue;

                // ⚠️ 有思考但没正文的那一轮也要留（模型可能只想着就调工具了）
                if (string.IsNullOrWhiteSpace(message.Content) && string.IsNullOrEmpty(message.Reasoning)) continue;

                var entry = new SessionEntry
                {
                    Id = NewId(),
                    ParentId = cursor,
                    Role = message.Role,
                    Content = message.Content,
                    Reasoning = message.Role == "assistant" ? message.Reasoning : null,
                };

                session.Entries.Add(entry);
                cursor = entry.Id;
                added++;
            }

            // 什么都没存进去：新会话就当没发生（别留一个空文件），老会话沿用原 id
            if (added == 0) return string.IsNullOrEmpty(sessionId) ? null : session.Id;

            session.CurrentId = cursor;
            session.Model = model ?? session.Model;
            session.Tokens += tokens;

            if (string.IsNullOrWhiteSpace(session.Title))
                session.Title = MakeTitle(session.Branch(), fallbackTitle);

            Write(session);
            PruneOld();
            return session.Id;
        }
        catch (Exception ex)
        {
            // 存历史失败不该影响对话本身，记一条就够
            AppLog.Exception("hubai", ex, "保存历史对话");
            return sessionId;
        }
    }

    /// <summary>切换当前显示的分支（用户点 ‹1/2›）。</summary>
    public static void SetCurrent(string sessionId, string entryId)
    {
        try
        {
            var session = Load(sessionId);
            if (session is null) return;

            session.EnsureTree();
            if (session.Entries.All(e => e.Id != entryId)) return;

            session.CurrentId = entryId;
            Write(session);
        }
        catch (Exception ex)
        {
            AppLog.Exception("hubai", ex, "切换历史分支");
        }
    }

    public static ChatSession? Load(string id)
    {
        try
        {
            var path = Path.Combine(Dir, id + ".json");
            if (!File.Exists(path)) return null;

            var session = JsonSerializer.Deserialize<ChatSession>(File.ReadAllText(path), JsonOpts);
            session?.EnsureTree();
            return session;
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

    private static void Write(ChatSession session)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(
            Path.Combine(Dir, session.Id + ".json"),
            JsonSerializer.Serialize(session, JsonOpts));
    }

    private static string MakeTitle(List<SessionEntry> branch, string fallback)
    {
        var first = branch.FirstOrDefault(e => e.Role == "user")?.Content ?? fallback;

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
