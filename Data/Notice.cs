using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ClassSoftwareHub.Desktop.Data;

/// <summary>
/// 首页顶部的一条公告（需求：2026-11-05，Nick 定）。
///
/// <para>
/// 数据放在桌面端仓库的 <c>公告/</c> 目录，两个来源合并：
/// <list type="bullet">
///   <item><c>公告/all.json</c> —— 所有版本都看；</item>
///   <item><c>公告/{版本号}.json</c> —— 只有这个版本看（形如 <c>1.2.0.json</c>）。</item>
/// </list>
/// 所以"某条公告给谁看"完全由文件名决定，客户端不用理解任何规则。
/// </para>
///
/// <para>
/// ⛔ 别把公告做成"下发开关/远程配置"。它只是**单向拉一段静态 JSON**：不上报、不统计、
///    不做 A/B。一旦掺进上报，就变成需要后端配合的云控，这条通道的意义也就没了。
/// </para>
/// </summary>
public sealed class NoticeItem
{
    /// <summary>稳定标识，用来记"这条已经关过了"。为空则整条丢弃（关不掉就永远弹，比不显示更糟）。</summary>
    public string Id { get; init; } = "";

    /// <summary>严重级别：<c>info</c> / <c>success</c> / <c>warning</c> / <c>error</c>，其他值按 info 处理。</summary>
    public string Level { get; init; } = "info";

    public string Title { get; init; } = "";

    public string Body { get; init; } = "";

    /// <summary>生效起点（UTC，ISO 8601）。为空表示立即生效。</summary>
    public DateTimeOffset? StartsAt { get; init; }

    /// <summary>失效时间（UTC，ISO 8601）。为空表示一直有效，直到从 JSON 里删掉。</summary>
    public DateTimeOffset? EndsAt { get; init; }

    /// <summary>
    /// 老字段，**当前不生效**：公告一律不显示关闭按钮（2026-11-05 Nick 定），
    /// 失效只靠 <see cref="EndsAt"/> 到点，或者把它从 json 里删掉。
    /// 仍然解析是为了让之前写过 <c>"dismissible": false</c> 的文件不报错，⛔ 别把它当开关用。
    /// </summary>
    public bool Dismissible { get; init; } = true;

    /// <summary>可选的跳转地址，点了开浏览器 / 站内页面。</summary>
    public string Link { get; init; } = "";

    /// <summary>跳转按钮上的文字。为空时用"查看详情"。</summary>
    public string LinkText { get; init; } = "";

    /// <summary>
    /// 解析一个公告文件。两种写法都收：
    /// <c>{"items":[…]}</c> 和直接一个数组 <c>[…]</c> —— 后者是最省事的写法，
    /// 早期写公告的人（包括我自己）大概率会直接甩一个数组，别让格式把人劝退。
    /// </summary>
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
                    // 缺省给 true：公告要能关。想钉住的显式写 "dismissible": false。
                    Dismissible = !node.TryGetProperty("dismissible", out var d)
                                  || d.ValueKind != JsonValueKind.False,
                    Link = Text(node, "link"),
                    LinkText = Text(node, "linkText"),
                };

                // 没有 id 就没法记"已关闭"，会变成每次开首页都弹一遍的骚扰。直接丢。
                if (item.Id.Length == 0 || (item.Title.Length == 0 && item.Body.Length == 0)) continue;
                if (item.Level.Length == 0) item = Clone(item, level: "info");

                result.Add(item);
            }
        }
        catch
        {
            // 公告是锦上添花：格式写错了就当没这条，绝不因为一个坏 JSON 影响首页加载。
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
