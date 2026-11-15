using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ClassSoftwareHub.Desktop.Data;


















public sealed class NoticeItem
{
    
    public string Id { get; init; } = "";

    
    public string Level { get; init; } = "info";

    public string Title { get; init; } = "";

    public string Body { get; init; } = "";

    
    public DateTimeOffset? StartsAt { get; init; }

    
    public DateTimeOffset? EndsAt { get; init; }

    
    
    
    
    
    public bool Dismissible { get; init; } = true;

    
    public string Link { get; init; } = "";

    
    public string LinkText { get; init; } = "";

    
    
    
    
    
    public static List<NoticeItem> Parse(string? json)
    {
        var result = new List<NoticeItem>();
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            var items = doc.RootElement;
            if (items.ValueKind == JsonValueKind.Object
                && items.TryGetProperty("items", out var inner)
                && inner.ValueKind == JsonValueKind.Array)
            {
                items = inner;
            }

            if (items.ValueKind != JsonValueKind.Array) return result;

            foreach (var node in items.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object) continue;

                var item = new NoticeItem
                {
                    Id = Text(node, "id"),
                    Level = Text(node, "level").ToLowerInvariant(),
                    Title = Text(node, "title"),
                    Body = Text(node, "body"),
                    StartsAt = Time(node, "startsAt"),
                    EndsAt = Time(node, "endsAt"),
                    
                    Dismissible = !node.TryGetProperty("dismissible", out var d)
                                  || d.ValueKind != JsonValueKind.False,
                    Link = Text(node, "link"),
                    LinkText = Text(node, "linkText"),
                };

                
                if (item.Id.Length == 0 || (item.Title.Length == 0 && item.Body.Length == 0)) continue;
                if (item.Level.Length == 0) item = Clone(item, level: "info");

                result.Add(item);
            }
        }
        catch
        {
            
        }

        return result;
    }

    private static NoticeItem Clone(NoticeItem s, string level) => new()
    {
        Id = s.Id,
        Level = level,
        Title = s.Title,
        Body = s.Body,
        StartsAt = s.StartsAt,
        EndsAt = s.EndsAt,
        Dismissible = s.Dismissible,
        Link = s.Link,
        LinkText = s.LinkText,
    };

    private static string Text(JsonElement node, string name)
        => node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "").Trim()
            : "";

    private static DateTimeOffset? Time(JsonElement node, string name)
    {
        var raw = Text(node, name);
        if (raw.Length == 0) return null;
        return DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var t)
            ? t
            : null;
    }
}
