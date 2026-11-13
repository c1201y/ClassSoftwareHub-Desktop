using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 软件 ID（slug）的生成与查重。
///
/// <para>
/// 规则（2026-11-12 定）：名称里的英文字母与数字原样小写保留，汉字按 <see cref="PinyinTable"/>
/// 转成拼音连写，其余字符（空格、标点、括号、符号）折叠成一个短横线。
/// 例：`7-Zip` → `7-zip`、`网易云音乐` → `wangyiyunyinyue`、`Visual Studio Code` → `visual-studio-code`。
/// </para>
///
/// <para>
/// ⛔ 只输出 `a-z 0-9 -`：站点用这个值拼地址（`#/download/&lt;id&gt;`），
/// 出现 `? &amp; = /` 或中文都会让链接失效。
/// </para>
/// </summary>
public static class SoftwareSlug
{
    /// <summary>ID 长度上限。太长既难读，也会把详情页地址撑得很长。</summary>
    public const int MaxLength = 48;

    /// <summary>由软件名生成 ID；生成不出（名称里没有可用字符）返回空串。</summary>
    public static string FromName(string? name)
    {
        var text = (name ?? "").Trim();
        if (text.Length == 0) return "";

        var sb = new StringBuilder(text.Length * 2);
        var pendingDash = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                AppendToken(sb, ref pendingDash, char.ToLowerInvariant(ch).ToString());
            }
            else if (PinyinTable.IsHan(ch))
            {
                // 整段汉字一起交给词表：能按词匹配就先按词（「音乐」→ yinyue 而不是 yinle）
                var start = i;
                while (i + 1 < text.Length && PinyinTable.IsHan(text[i + 1])) i++;

                var pinyin = PinyinTable.Translate(text[start..(i + 1)]);
                if (pinyin.Length > 0) AppendToken(sb, ref pendingDash, pinyin);
                else pendingDash = sb.Length > 0;
            }
            else
            {
                // 空格 / 标点 / 符号：只留一个分隔意图，连续多个不重复出横线
                pendingDash = sb.Length > 0;
            }
        }

        var id = sb.ToString().Trim('-');
        return id.Length > MaxLength ? Shorten(id) : id;
    }

    /// <summary>站点里已经有同 id 的软件就加序号：`foo` → `foo-2` → `foo-3`……</summary>
    public static string EnsureUnique(string? id, IEnumerable<string> taken)
    {
        var baseId = (id ?? "").Trim().Trim('-');
        if (baseId.Length == 0) return "";

        var used = new HashSet<string>(taken.Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(baseId)) return baseId;

        for (var suffix = 2; suffix <= 99; suffix++)
        {
            var candidate = $"{baseId}-{suffix}";
            if (!used.Contains(candidate)) return candidate;
        }

        // 99 个都被占了（几乎不可能）：退回时间戳，保证一定能提交
        return $"{baseId}-{DateTime.Now:MMddHHmm}";
    }

    /// <summary>拿当前清单里的全部 id（大小写不敏感去重）。</summary>
    public static HashSet<string> TakenIds()
        => App.Content.Apps
            .Select(app => app.Id)
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>该 id 是否已被站内软件占用。</summary>
    public static bool IsTaken(string? id)
    {
        var text = (id ?? "").Trim();
        return text.Length > 0 && TakenIds().Contains(text);
    }

    /// <summary>格式是否合法：只允许小写字母、数字与短横线，且不以横线开头结尾。</summary>
    public static bool IsValid(string? id)
    {
        var text = (id ?? "").Trim();
        if (text.Length == 0 || text.Length > MaxLength) return false;
        if (text[0] == '-' || text[^1] == '-') return false;

        foreach (var ch in text)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') continue;
            return false;
        }

        return true;
    }

    private static void AppendToken(StringBuilder sb, ref bool pendingDash, string token)
    {
        if (pendingDash && sb.Length > 0) sb.Append('-');
        pendingDash = false;
        sb.Append(token);
    }

    /// <summary>超长时在横线处截断，避免把半个单词切掉。</summary>
    private static string Shorten(string id)
    {
        var cut = id[..MaxLength];
        var dash = cut.LastIndexOf('-');
        if (dash > MaxLength / 2) cut = cut[..dash];
        return cut.Trim('-');
    }
}
