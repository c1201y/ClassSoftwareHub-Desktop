using System;
using System.Text.Json;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>一条教室喊话（服务端 <c>/api/announce/poll</c> 200 响应体）。
/// <para>
/// ⚠️ 服务端实际返回的是整条存储记录，除下列字段外还带 <c>targets</c> / <c>consumed</c> / <c>_exp</c>
/// 这些内部字段 —— 解析时只取自己要的，多余字段一概不管（2026-10-25 对照 csh-api 的 gateAnn 实现确认）。
/// </para>
/// </summary>
public sealed class AnnounceMessage
{
    [System.Text.Json.Serialization.JsonPropertyName("id")] public string Id { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("text")] public string Text { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("from")] public string From { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("duration")] public int Duration { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("ts")] public long Ts { get; set; }

    /// <summary>从 poll 的 JSON 响应解出一条喊话；结构对不上返回 null（不当空喊话播）。</summary>
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
