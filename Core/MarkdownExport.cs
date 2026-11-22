using System;
using System.Collections.Generic;
using System.Text;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// Markdown 的两种导出形态：去掉标记的纯文本，以及带内联样式的独立 HTML。
/// 解析口径与 <see cref="MarkdownText"/> 的渲染保持一致，改一边记得同改另一边的块级规则。
/// </summary>
public static class MarkdownExport
{
    /// <summary>去掉所有 Markdown 标记，得到可直接粘贴到 Word、邮件里的纯文本。</summary>
    public static string ToPlainText(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return "";

        // WinUI 的 TextBox 交出的是纯 \r 换行，只换 \r\n 会把整篇当成一行（2026-11-21 的导出事故）。
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var output = new StringBuilder();
        var inCode = false;

        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }

            if (inCode)
            {
                output.AppendLine(raw);
                continue;
            }

            if (trimmed.Length == 0)
            {
                output.AppendLine();
                continue;
            }

            // 分隔线本身没有文字内容，留一个空行代替。
            if (trimmed is "---" or "***" or "___")
            {
                output.AppendLine();
                continue;
            }

            // 表格分隔行（| --- | --- |）直接丢掉。
            if (IsTableDivider(trimmed)) continue;

            if (trimmed[0] == '#')
            {
                var level = 0;
                while (level < trimmed.Length && trimmed[level] == '#') level++;
                output.AppendLine(StripInline(trimmed[level..].Trim()));
                continue;
            }

            if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                var quote = trimmed.TrimStart('>').Trim();
                if (quote.Length > 0) output.AppendLine(StripInline(quote));
                continue;
            }

            if (trimmed.StartsWith('|'))
            {
                var cells = SplitRow(trimmed);
                for (var i = 0; i < cells.Count; i++) cells[i] = StripInline(cells[i]);
                output.AppendLine(string.Join("    ", cells));
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal)
                || trimmed.StartsWith("* ", StringComparison.Ordinal)
                || trimmed.StartsWith("+ ", StringComparison.Ordinal)
                || trimmed.StartsWith("• ", StringComparison.Ordinal))
            {
                // 纯文本不要项目符号，只留缩进，粘贴到 Word 或邮件里更干净。
                var rest = StripInline(trimmed[2..].Trim());
                var indent = IndentOf(raw);
                output.AppendLine(new string(' ', indent + 2) + TrimTaskBox(rest));
                continue;
            }

            if (TryOrderedItem(trimmed, out var marker, out var orderedRest))
            {
                var indent = IndentOf(raw);
                output.AppendLine(new string(' ', indent) + marker + " " + StripInline(orderedRest));
                continue;
            }

            output.AppendLine(StripInline(trimmed));
        }

        return output.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>生成可直接双击打开的 HTML 文档，样式与页面里的渲染结果贴近。</summary>
    public static string ToHtml(string markdown, string title = "Markdown")
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html lang=\"zh-CN\">");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\" />");
        builder.Append("<title>").Append(Escape(title)).AppendLine("</title>");
        builder.AppendLine("<style>");
        builder.AppendLine(StyleSheet);
        builder.AppendLine("</style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine(BuildBody(markdown));
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return builder.ToString();
    }

    private const string StyleSheet = """
        @page { size: A4; margin: 18mm 16mm; }
        * { box-sizing: border-box; }
        body {
          margin: 0 auto;
          padding: 56px 64px 72px;
          max-width: 840px;
          font-family: "Segoe UI Variable Text", "Microsoft YaHei UI", "Microsoft YaHei", sans-serif;
          font-size: 15px;
          line-height: 1.78;
          color: #1a1a1a;
          background: #ffffff;
          -webkit-font-smoothing: antialiased;
        }
        h1, h2, h3, h4 { font-weight: 600; line-height: 1.36; margin: 1.5em 0 0.55em; }
        h1 { font-size: 30px; margin-top: 0; }
        h2 { font-size: 23px; }
        h3 { font-size: 19px; }
        h4 { font-size: 16px; }
        p { margin: 0.68em 0; }
        a { color: #0b62c4; text-decoration: none; }
        a:hover { text-decoration: underline; }
        code {
          font-family: "Cascadia Mono", Consolas, "Courier New", monospace;
          font-size: 0.88em;
          background: #f2f2f2;
          padding: 1px 5px;
          border-radius: 3px;
        }
        pre {
          background: #f7f7f7;
          border: 1px solid #e6e6e6;
          border-radius: 8px;
          padding: 14px 16px;
          overflow-x: auto;
          line-height: 1.6;
        }
        pre code { background: none; padding: 0; font-size: 13px; }
        blockquote {
          margin: 0.9em 0;
          padding: 2px 0 2px 14px;
          border-left: 3px solid #4a90e2;
          color: #555;
        }
        blockquote p { margin: 0.3em 0; }
        ul, ol { margin: 0.65em 0; padding-left: 1.7em; }
        li { margin: 0.28em 0; }
        table { border-collapse: collapse; margin: 1em 0; width: 100%; font-size: 14px; }
        th, td { border: 1px solid #e0e0e0; padding: 8px 12px; text-align: left; vertical-align: top; }
        th { background: #f5f5f5; font-weight: 600; }
        img { max-width: 100%; height: auto; border-radius: 6px; }
        hr { border: none; border-top: 1px solid #e2e2e2; margin: 1.7em 0; }
        @media print {
          body { padding: 0; max-width: none; }
          pre, table, img { page-break-inside: avoid; }
          h1, h2, h3 { page-break-after: avoid; }
        }
        """;

    private static string BuildBody(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return "";

        // WinUI 的 TextBox 交出的是纯 \r 换行，只换 \r\n 会把整篇当成一行（2026-11-21 的导出事故）。
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var html = new StringBuilder();
        var paragraph = new List<string>();
        var listMode = "";               // "ul" / "ol" / 空
        var listIndent = 0;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            html.Append("<p>").Append(Inline(string.Join(" ", paragraph))).AppendLine("</p>");
            paragraph.Clear();
        }

        void CloseList()
        {
            if (listMode.Length == 0) return;
            html.AppendLine($"</{listMode}>");
            listMode = "";
            listIndent = 0;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var trimmed = raw.Trim();

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                CloseList();
                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                CloseList();
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    code.AppendLine(lines[i]);
                    i++;
                }

                html.Append("<pre><code>").Append(Escape(code.ToString().TrimEnd('\r', '\n')))
                    .AppendLine("</code></pre>");
                continue;
            }

            if (trimmed is "---" or "***" or "___")
            {
                FlushParagraph();
                CloseList();
                html.AppendLine("<hr />");
                continue;
            }

            if (IsTableLine(trimmed) && i + 1 < lines.Length && IsTableLine(lines[i + 1].Trim()))
            {
                FlushParagraph();
                CloseList();
                i = AppendTable(lines, i, html);
                continue;
            }

            if (trimmed[0] == '#')
            {
                FlushParagraph();
                CloseList();
                var level = 0;
                while (level < trimmed.Length && trimmed[level] == '#') level++;
                var tag = Math.Clamp(level, 1, 4);
                html.Append($"<h{tag}>").Append(Inline(trimmed[level..].Trim())).AppendLine($"</h{tag}>");
                continue;
            }

            if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                FlushParagraph();
                CloseList();
                var quote = new StringBuilder();
                while (i < lines.Length && lines[i].TrimStart().StartsWith(">", StringComparison.Ordinal))
                {
                    quote.Append(Inline(lines[i].TrimStart()[1..].Trim())).Append(' ');
                    i++;
                }

                i--;
                html.Append("<blockquote><p>").Append(quote.ToString().Trim())
                    .AppendLine("</p></blockquote>");
                continue;
            }

            if (IsBullet(trimmed))
            {
                FlushParagraph();
                var indent = IndentOf(raw) / 2;
                if (listMode != "ul" || indent != listIndent)
                {
                    CloseList();
                    listMode = "ul";
                    listIndent = indent;
                    html.Append("<ul>");
                    if (indent > 0) html.Append($"<ul>");
                    html.AppendLine();
                }

                var item = trimmed[2..].Trim();
                html.Append("<li>").Append(Inline(TrimTaskBox(item))).AppendLine("</li>");
                continue;
            }

            if (TryOrderedItem(trimmed, out _, out var orderedRest))
            {
                FlushParagraph();
                if (listMode != "ol")
                {
                    CloseList();
                    listMode = "ol";
                    html.AppendLine("<ol>");
                }

                html.Append("<li>").Append(Inline(orderedRest)).AppendLine("</li>");
                continue;
            }

            if ((trimmed.StartsWith("![", StringComparison.Ordinal)
                 || trimmed.StartsWith("<img", StringComparison.OrdinalIgnoreCase))
                && MarkdownText.TryImage(trimmed, out var imageAlt, out var imageSource))
            {
                FlushParagraph();
                CloseList();
                html.Append("<p><img src=\"").Append(Escape(imageSource))
                    .Append("\" alt=\"").Append(Escape(imageAlt))
                    .AppendLine("\" /></p>");
                continue;
            }

            paragraph.Add(trimmed);
        }

        FlushParagraph();
        CloseList();

        // 缩进列表收尾时补偿未闭合的内层 ul，避免生成非法结构。
        var broken = html.ToString();
        var opens = CountOccurrences(broken, "<ul>");
        var closes = CountOccurrences(broken, "</ul>");
        for (var i = 0; i < opens - closes; i++) html.AppendLine("</ul>");

        return html.ToString();
    }

    private static int AppendTable(string[] lines, int start, StringBuilder html)
    {
        var rows = new List<List<string>> { SplitRow(lines[start]) };

        var i = start + 1;
        if (i < lines.Length && IsTableDivider(lines[i].Trim())) i++;

        while (i < lines.Length)
        {
            var trimmed = lines[i].Trim();
            if (!IsTableLine(trimmed)) break;
            rows.Add(SplitRow(trimmed));
            i++;
        }

        html.AppendLine("<table><thead><tr>");
        foreach (var cell in rows[0]) html.Append("<th>").Append(Inline(cell)).Append("</th>");
        html.AppendLine("</tr></thead><tbody>");

        for (var r = 1; r < rows.Count; r++)
        {
            html.AppendLine("<tr>");
            foreach (var cell in rows[r]) html.Append("<td>").Append(Inline(cell)).Append("</td>");
            html.AppendLine("</tr>");
        }

        html.AppendLine("</tbody></table>");
        return i - 1;
    }

    /// <summary>行内标记：图片、链接、行内代码、粗体、斜体、删除线。</summary>
    private static string Inline(string text)
    {
        if (text.Length == 0) return "";

        // 先把行内代码抽成占位符，避免里面的 * 和 _ 被当成强调。
        var codes = new List<string>();
        var buffer = new StringBuilder();
        var inCode = false;

        foreach (var ch in text)
        {
            if (ch == '`')
            {
                inCode = !inCode;
                if (inCode)
                {
                    buffer.Append('\u0001').Append(codes.Count).Append('\u0002');
                    codes.Add("");
                }
                continue;
            }

            if (inCode) codes[^1] += ch;
            else buffer.Append(ch);
        }

        var result = Escape(buffer.ToString());

        result = Replace(result, @"!\[([^\]]*)\]\(([^)\s]+)\)", m =>
            $"<img alt=\"{m.Groups[1].Value}\" src=\"{m.Groups[2].Value}\" />");

        result = Replace(result, @"\[([^\]]+)\]\(([^)\s]+)\)", m =>
            $"<a href=\"{m.Groups[2].Value}\">{m.Groups[1].Value}</a>");

        result = Replace(result, @"\*\*([^*]+)\*\*", m => $"<strong>{m.Groups[1].Value}</strong>");
        result = Replace(result, @"__([^_]+)__", m => $"<strong>{m.Groups[1].Value}</strong>");
        result = Replace(result, @"(?<![*\w])\*([^*]+)\*(?![*\w])", m => $"<em>{m.Groups[1].Value}</em>");
        result = Replace(result, @"~~([^~]+)~~", m => $"<del>{m.Groups[1].Value}</del>");

        for (var i = 0; i < codes.Count; i++)
        {
            result = result.Replace($"\u0001{i}\u0002",
                "<code>" + Escape(codes[i]) + "</code>", StringComparison.Ordinal);
        }

        return result;
    }

    private static string StripInline(string text)
    {
        if (text.Length == 0) return "";

        var result = text;

        // 图片留 alt，链接留文字。
        result = Replace(result, @"!\[([^\]]*)\]\(([^)]*)\)", m => m.Groups[1].Value);
        result = Replace(result, @"\[([^\]]+)\]\(([^)]*)\)", m => m.Groups[1].Value);

        result = result.Replace("**", "", StringComparison.Ordinal)
                       .Replace("__", "", StringComparison.Ordinal)
                       .Replace("~~", "", StringComparison.Ordinal)
                       .Replace("`", "", StringComparison.Ordinal);

        // 单星号斜体：只清理成对出现的部分。
        result = Replace(result, @"\*([^*\n]+)\*", m => m.Groups[1].Value);

        return result.Trim();
    }

    private static string Replace(string input, string pattern, Func<System.Text.RegularExpressions.Match, string> build)
        => System.Text.RegularExpressions.Regex.Replace(input, pattern, m => build(m));

    private static string TrimTaskBox(string text)
    {
        if (text.StartsWith("[ ]", StringComparison.Ordinal)) return "☐ " + text[3..].TrimStart();
        if (text.StartsWith("[x]", StringComparison.OrdinalIgnoreCase)) return "☑ " + text[3..].TrimStart();
        return text;
    }

    private static bool IsBullet(string trimmed)
        => trimmed.StartsWith("- ", StringComparison.Ordinal)
           || trimmed.StartsWith("* ", StringComparison.Ordinal)
           || trimmed.StartsWith("• ", StringComparison.Ordinal);

    private static int IndentOf(string raw)
    {
        var count = 0;
        foreach (var ch in raw)
        {
            if (ch == ' ') count++;
            else if (ch == '\t') count += 4;
            else break;
        }

        return count;
    }

    private static bool TryOrderedItem(string line, out string marker, out string rest)
    {
        marker = "";
        rest = "";

        var i = 0;
        while (i < line.Length && char.IsDigit(line[i])) i++;

        if (i == 0 || i + 1 >= line.Length) return false;
        if (line[i] != '.' && line[i] != ')') return false;
        if (line[i + 1] != ' ') return false;

        marker = line[..(i + 1)];
        rest = line[(i + 1)..].Trim();
        return true;
    }

    private static bool IsTableDivider(string line)
    {
        var trimmed = line.Replace('｜', '|');
        if (trimmed.Length == 0 || trimmed[0] != '|') return false;

        foreach (var ch in trimmed)
        {
            if (ch is '|' or '-' or ':' or ' ' or '\t') continue;
            return false;
        }

        return trimmed.Contains("---", StringComparison.Ordinal);
    }

    private static bool IsTableLine(string trimmed)
        => trimmed.Length > 0 && (trimmed[0] == '|' || trimmed[0] == '｜');

    private static List<string> SplitRow(string line)
    {
        var trimmed = line.Trim().Replace('｜', '|');
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|')) trimmed = trimmed[..^1];

        var cells = new List<string>();
        foreach (var part in trimmed.Split('|')) cells.Add(part.Trim());
        return cells;
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static string Escape(string text)
        => text.Replace("&", "&amp;", StringComparison.Ordinal)
               .Replace("<", "&lt;", StringComparison.Ordinal)
               .Replace(">", "&gt;", StringComparison.Ordinal)
               .Replace("\"", "&quot;", StringComparison.Ordinal);
}
