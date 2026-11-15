using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.HubAi;










public sealed class SessionEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    
    [JsonPropertyName("parentId")]
    public string? ParentId { get; set; }

    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    
    
    
    
    [JsonPropertyName("reasoning")]
    public string? Reasoning { get; set; }
}


public sealed class ChatSession
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    
    [JsonPropertyName("tokens")]
    public int Tokens { get; set; }

    
    [JsonPropertyName("currentId")]
    public string? CurrentId { get; set; }

    [JsonPropertyName("entries")]
    public List<SessionEntry> Entries { get; set; } = new();

    
    [JsonIgnore]
    public string DateText => CreatedAt.ToString("MM-dd HH:mm");

    
    
    
    
    
    
    public void EnsureTree()
    {
        for (var i = 0; i < Entries.Count; i++)
        {
            if (!string.IsNullOrEmpty(Entries[i].Id)) continue;

            
            
            
            
            var candidate = $"e{i}";
            var n = i;
            while (Entries.Any(e => string.Equals(e.Id, candidate, StringComparison.Ordinal)))
                candidate = $"e{n++}x";

            Entries[i].Id = candidate;
        }

        
        if (Entries.Count > 1 && Entries.All(e => string.IsNullOrEmpty(e.ParentId)))
            for (var i = 1; i < Entries.Count; i++)
                Entries[i].ParentId = Entries[i - 1].Id;

        if (string.IsNullOrEmpty(CurrentId) || Entries.All(e => e.Id != CurrentId))
            CurrentId = Entries.Count > 0 ? Entries[^1].Id : null;

        
        
        
        
        
        for (var i = 1; i < Entries.Count; i++)
        {
            if (string.IsNullOrEmpty(Entries[i].ParentId))
                Entries[i].ParentId = Entries[i - 1].Id;
        }
    }

    
    public List<SessionEntry> Branch() => BranchUpTo(CurrentId);

    
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

    
    public List<SessionEntry> Siblings(SessionEntry entry) =>
        Entries
            .Where(e => e.Role == entry.Role && string.Equals(e.ParentId, entry.ParentId, StringComparison.Ordinal))
            .ToList();

    
    
    
    
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

















public static class ChatSessionStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    
    private const int MaxSessions = 60;

    public static string Dir => Path.Combine(AppPaths.DataDir, "hubai-sessions");

    
    public static string NewId() => Guid.NewGuid().ToString("N")[..10];

    
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

            
            
            
            
            
            
            var cursor = parentId ?? session.CurrentId;
            var added = 0;

            foreach (var message in turns)
            {
                if (message.Role != "user" && message.Role != "assistant") continue;

                
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
            
            AppLog.Exception("hubai", ex, "保存历史对话");
            return sessionId;
        }
    }

    
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

        
        var line = string.Join(' ', first.Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (line.Length == 0) line = "未命名对话";

        return line.Length > 24 ? line[..24] + "…" : line;
    }

    
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
