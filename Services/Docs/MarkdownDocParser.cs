using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClassSoftwareHub.Desktop.Services.Docs;

/// <summary>
/// Markdown → <see cref="DocBlock"/>。
///
/// <para>
/// 覆盖教程文章实际会用到的那些：<c>#</c> 标题、段落、<c>- / 1.</c> 列表（含多层缩进）、
/// <c>- [ ]</c> 任务列表、<c>&gt;</c> 引用、``` 代码块、<c>---</c> 分隔线、
/// <c>| --- |</c> 表格、独立成行的 <c>![说明](图片)</c>，
/// 行内交给 <see cref="DocInline"/>（粗体 / 斜体 / 删除线 / 行内代码 / 链接 / 裸链接）。
/// </para>
/// <para>
/// ⛔ 不支持 HTML 块 —— 按普通段落显示原文，信息不会丢。
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

            // 表格：本行含竖线，且下一行是 |---|---| 这样的分隔行。
            // ⛔ 判定必须看分隔行，不能只看竖线 —— 正文里随手写一个 "A | B" 不该变成表格。
            if (trimmed.Contains('|')
                && i + 1 < lines.Length
                && TryTableAlignments(lines[i + 1], out var alignments))
            {
                FlushParagraph();
                FlushList();

                var rows = new List<IReadOnlyList<string>> { SplitTableCells(raw) };
                i += 2;

                while (i < lines.Length)
                {
                    var line = lines[i];
                    if (line.Trim().Length == 0 || !line.Contains('|')) break;

                    // 中间夹了重复的分隔行：跳过，别当成一行空数据
                    if (TryTableAlignments(line, out _)) { i++; continue; }

                    rows.Add(SplitTableCells(line));
                    i++;
                }

                blocks.Add(BuildTable(rows, alignments));
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
            if (TryListItem(raw, out var itemLevel, out var ordered, out var marker,
                            out var itemText, out var itemChecked))
            {
                FlushParagraph();

                if (list is not null && listOrdered != ordered)
                {
                    FlushList();
                }

                list ??= new List<DocListItem>();
                listOrdered = ordered;
                list.Add(new DocListItem(
                    itemLevel, marker, DocInline.Parse(itemText.Trim()), itemChecked));
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

    // ── 表格 ─────────────────────────────────────────────────────

    /// <summary>
    /// 判定表格的分隔行（<c>|---|:---:|---:|</c>），同时取出每列的对齐方式。
    ///
    /// <para>
    /// 要求每一段都只由短横线（两边可加冒号）组成，且至少有一段。
    /// 普通的 <c>---</c> 分隔线不含竖线，不会被误判；反过来 <c>|---|---|</c> 也不会被
    /// <see cref="IsHorizontalRule"/> 当成分隔线（首字符是竖线）。
    /// </para>
    /// </summary>
    private static bool TryTableAlignments(string line, out List<DocAlign> alignments)
    {
        alignments = new List<DocAlign>();

        var trimmed = line.Trim();
        if (!trimmed.Contains('|')) return false;

        var cells = SplitTableCells(trimmed);
        if (cells.Count == 0) return false;

        foreach (var cell in cells)
        {
            var text = cell.Trim();
            if (text.Length == 0) return false;

            var left = text.StartsWith(':');
            var right = text.EndsWith(':');

            var dashes = text.Trim(':');
            if (dashes.Length == 0) return false;
            if (dashes.Any(c => c != '-')) return false;

            alignments.Add(left && right
                ? DocAlign.Center
                : right ? DocAlign.Right : DocAlign.Left);
        }

        return true;
    }

    /// <summary>
    /// 按竖线切单元格。首尾的竖线允许省略（<c>a | b</c> 与 <c>| a | b |</c> 等价），
    /// <c>\|</c> 是单元格内部的一个竖线，不参与切分。
    /// </summary>
    private static List<string> SplitTableCells(string line)
    {
        var cells = new List<string>();
        var current = new StringBuilder();

        var text = line.Trim();
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (ch == '\\' && i + 1 < text.Length && text[i + 1] == '|')
            {
                current.Append('|');
                i++;
                continue;
            }

            if (ch == '|')
            {
                cells.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        cells.Add(current.ToString());

        // 外层竖线切出来的空壳要去掉，但只去首尾各一个
        if (cells.Count > 0 && cells[0].Trim().Length == 0) cells.RemoveAt(0);
        if (cells.Count > 0 && cells[^1].Trim().Length == 0) cells.RemoveAt(cells.Count - 1);

        return cells;
    }

    /// <summary>把切好的原始单元格拼成 <see cref="DocTableBlock"/>，列数按各行最大值补齐。</summary>
    private static DocTableBlock BuildTable(
        List<IReadOnlyList<string>> rows, List<DocAlign> headerAlignments)
    {
        var columns = headerAlignments.Count;
        foreach (var row in rows) columns = Math.Max(columns, row.Count);
        columns = Math.Max(1, columns);

        var built = new List<DocTableRow>(rows.Count);
        foreach (var row in rows)
        {
            var cells = new List<DocTableCell>(columns);
            for (var c = 0; c < columns; c++)
            {
                var text = c < row.Count ? row[c] : "";
                cells.Add(new DocTableCell(DocInline.Parse(text.Trim())));
            }
            built.Add(new DocTableRow(cells));
        }

        var alignments = new List<DocAlign>(columns);
        for (var c = 0; c < columns; c++)
            alignments.Add(c < headerAlignments.Count ? headerAlignments[c] : DocAlign.Left);

        return new DocTableBlock(built, alignments);
    }

    private static bool TryListItem(
        string raw, out int level, out bool ordered, out string marker,
        out string text, out bool? check)
    {
        level = 0;
        ordered = false;
        marker = "";
        text = "";
        check = null;

        var indent = 0;
        while (indent < raw.Length && raw[indent] == ' ') indent++;
        level = Math.Min(4, indent / 2);

        var rest = raw[indent..];
        if (rest.Length < 2) return false;

        // 无序：- / * / + 后面跟空格
        if ((rest[0] is '-' or '*' or '+') && rest[1] == ' ')
        {
            var body = rest[2..];

            // 任务列表：- [ ] / - [x]（大小写都认）。认出来就不画圆点，改画勾选框
            if (body.Length >= 3 && body[0] == '[' && body[2] == ']')
            {
                var mark = char.ToLowerInvariant(body[1]);
                if (mark is ' ' or 'x')
                {
                    check = mark == 'x';
                    body = body.Length > 3 ? body[3..].TrimStart() : "";
                }
            }

            marker = check is null ? "•" : "";
            text = body;
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
