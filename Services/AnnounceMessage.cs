using System;
using System.Text.Json;

namespace ClassSoftwareHub.Desktop.Services;







public sealed class AnnounceMessage
{
    [System.Text.Json.Serialization.JsonPropertyName("id")] public string Id { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("text")] public string Text { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("from")] public string From { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("duration")] public int Duration { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("ts")] public long Ts { get; set; }

    
    public static AnnounceMessage? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var text = root.TryGetProperty("text", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(text)) return null;

            return new AnnounceMessage
            {
                Id = root.TryGetProperty("id", out var i) ? (i.GetString() ?? "") : "",
                Text = text,
                From = root.TryGetProperty("from", out var f) ? (f.GetString() ?? "") : "",
                Duration = root.TryGetProperty("duration", out var d) && d.TryGetInt32(out var dur) ? dur : 30,
                Ts = root.TryGetProperty("ts", out var ts) && ts.TryGetInt64(out var l) ? l : 0,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
