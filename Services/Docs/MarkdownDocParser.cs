using System;
using System.Collections.Generic;
using System.Text;

namespace ClassSoftwareHub.Desktop.Services.Docs;

/// <summary>
/// Markdown → <see cref="DocBlock"/>。
///
/// <para>
/// 覆盖教程文章实际会用到的那些：<c>#</c> 标题、段落、<c>- / 1.</c> 列表（含两层缩进）、
/// <c>&gt;</c> 引用、``` 代码块、<c>---</c> 分隔线、独立成行的 <c>![说明](图片)</c>，
/// 行内交给 <see cref="DocInline"/>（粗体 / 斜体 / 行内代码 / 链接 / 裸链接）。
/// </para>
/// <para>
/// ⛔ 不支持表格和 HTML 块 —— 表格按普通段落显示（原样的竖线），信息不会丢。
/// </para>
/// </summary>
public static class MarkdownDocParser
{
    /// <param name="resolveImage">
    /// 相对图片路径 → 本机缓存文件路径。返回 null 表示找不到（图片位置会显示一句提示）。
    /// 绝对地址（http/https）不需要经过它。
    /// </param>
    public static IReadOnlyList<DocBlock> Parse(string? markdown, Func<string, string?>? resolveImage = null)
    {
        var blocks = new List<DocBlock>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var paragraph = new List<string>();
        List<DocListItem>? list = null;
        var listOrdered = false;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            var text = string.Join(" ", paragraph).Trim();
            paragraph.Clear();
            if (text.Length == 0) return;
            blocks.Add(new DocParagraphBlock(DocInline.Parse(text)));
        }

        void FlushList()
        {
            if (list is null) return;
            if (list.Count > 0) blocks.Add(new DocListBlock(listOrdered, list));
            list = null;
        }

        var i = 0;
        while (i < lines.Length)
        {
            var raw = lines[i];
            var trimmed = raw.Trim();

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                FlushList();
                i++;
                continue;
            }

            // 代码块：``` 或 ~~~（收尾那一行允许带缩进）
            if (trimmed.StartsWith("```", StringComparison.Ordinal)
                || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                FlushParagraph();
                FlushList();

                var fence = trimmed[..3];
                var language = trimmed[3..].Trim();
                i++;

                var code = new StringBuilder();
                while (i < lines.Length
                       && !lines[i].TrimStart().StartsWith(fence, StringComparison.Ordinal))
                {
                    code.Append(lines[i]).Append('\n');
                    i++;
                }
                if (i < lines.Length) i++;

                blocks.Add(new DocCodeBlock(code.ToString().TrimEnd('\n'),
                    language.Length == 0 ? null : language));
                continue;
            }

            // 分隔线
            if (IsHorizontalRule(trimmed))
            {
                FlushParagraph();
                FlushList();
                blocks.Add(new DocDividerBlock());
                i++;
                continue;
            }

            // 标题：# ~ ######
            if (trimmed[0] == '#' && TryHeading(trimmed, out var level, out var headingText))
            {
                FlushParagraph();
                FlushList();
                blocks.Add(new DocHeadingBlock(level, DocInline.Parse(headingText)));
                i++;
                continue;
            }

            // 引用：连续 > 行合成一块
            if (trimmed[0] == '>')
            {
                FlushParagraph();
                FlushList();

                var quote = new List<IReadOnlyList<DocRun>>();
                while (i < lines.Length)
                {
                    var line = lines[i].TrimStart();
                    if (!line.StartsWith('>')) break;

                    var content = line[1..];
                    if (content.StartsWith(' ')) content = content[1..];
                    quote.Add(DocInline.Parse(content.TrimEnd()));
                    i++;
                }

                blocks.Add(new DocQuoteBlock(quote));
                continue;
            }

            // 独立成行的图片：![说明](地址)
            if (trimmed.StartsWith("![", StringComparison.Ordinal))
            {
                var head = raw.TrimStart();
                if (DocInline.TryReadLink(head, 1, out var alt, out var src, out var after)
                    && head[after..].Trim().Length == 0)
                {
                    FlushParagraph();
                    FlushList();
                    blocks.Add(MakeImage(src, alt, resolveImage));
                    i++;
                    continue;
                }
            }

            // 列表项
            if (TryListItem(raw, out var itemLevel, out var ordered, out var marker, out var itemText))
            {
                FlushParagraph();

                if (list is not null && listOrdered != ordered)
                {
                    FlushList();
                }

                list ??= new List<DocListItem>();
                listOrdered = ordered;
                list.Add(new DocListItem(itemLevel, marker, DocInline.Parse(itemText.Trim())));
                i++;
                continue;
            }

            // 普通段落（相邻行合并成一段；中文排版里单换行多半是软换行）
            FlushList();
            paragraph.Add(trimmed);
            i++;
        }

        FlushParagraph();
        FlushList();
        return blocks;
    }

    private static DocImageBlock MakeImage(
        string src, string alt, Func<string, string?>? resolveImage)
    {
        var raw = src.Trim();
        if (IsAbsolute(raw) || resolveImage is null)
            return new DocImageBlock(raw, alt.Trim());

        var local = resolveImage(raw);
        return local is null
            ? new DocImageBlock("", alt.Trim())   // 找不到：渲染器显示"图片未缓存"
            : new DocImageBlock(local, alt.Trim());
    }

    private static bool IsAbsolute(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    private static bool TryHeading(string trimmed, out int level, out string text)
    {
        level = 0;
        text = "";

        while (level < trimmed.Length && trimmed[level] == '#') level++;
        if (level is 0 or > 6) return false;

        var rest = trimmed[level..];
        if (rest.Length > 0 && rest[0] != ' ') return false;

        text = rest.Trim().TrimEnd('#').Trim();
        return text.Length > 0;
    }

    private static bool IsHorizontalRule(string trimmed)
    {
        if (trimmed.Length < 3) return false;

        var ch = trimmed[0];
        if (ch is not ('-' or '*' or '_')) return false;

        var count = 0;
        foreach (var c in trimmed)
        {
            if (c == ch) { count++; continue; }
            if (c is ' ' or '\t') continue;
            return false;
        }

        return count >= 3;
    }

    private static bool TryListItem(
        string raw, out int level, out bool ordered, out string marker, out string text)
    {
        level = 0;
        ordered = false;
        marker = "";
        text = "";

        var indent = 0;
        while (indent < raw.Length && raw[indent] == ' ') indent++;
        level = Math.Min(4, indent / 2);

        var rest = raw[indent..];
        if (rest.Length < 2) return false;

        // 无序：- / * / + 后面跟空格
        if ((rest[0] is '-' or '*' or '+') && rest[1] == ' ')
        {
            marker = "•";
            text = rest[2..];
            return true;
        }

        // 有序：1. / 1) 保留原编号
        var digits = 0;
        while (digits < rest.Length && char.IsDigit(rest[digits])) digits++;
        if (digits == 0 || digits + 1 >= rest.Length) return false;
        if (rest[digits] is not ('.' or ')') || rest[digits + 1] != ' ') return false;

        ordered = true;
        marker = rest[..digits] + ".";
        text = rest[(digits + 2)..];
        return true;
    }
}
