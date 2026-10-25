using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 极简 Markdown → 原生控件（2026-10-18，2026-10-23 补表格）。
///
/// <para>
/// 为什么自己写而不是引库：WinUI 3 没有内置 Markdown 控件，而模型输出永远是 Markdown
/// （`**加粗**`、`- 列表`、`### 标题`、`| 表格 |`）。引一个第三方渲染器会带进一堆用不上的东西。
/// </para>
/// <para>
/// ⛔ 仍然**不支持**代码块、图片、链接：这三样在 AI 回答里出现频率低，
/// 按纯文本显示信息也不会丢。
/// </para>
/// <para>
/// ⚠️ 表格必须用 <see cref="Apply(StackPanel, string, ...)"/> —— <see cref="RichTextBlock"/>
/// 里塞不进 Grid，所以在那里表格仍会退化成一行竖线。（2026-10-23：Nick 截图里那张表
/// 就挤成了一段 `|软件|补的缺|说明||---|---|---|`，因为气泡正文当时用的就是 RichTextBlock。）
/// </para>
/// <para>
/// ⚠️ 流式渲染时这个方法会被反复调用（约每 80ms 一次），所以实现要够轻：
/// 只做一次字符串切分，不建语法树、不回溯。
/// </para>
/// </summary>
public static class MarkdownText
{
    /// <summary>把 <paramref name="markdown"/> 渲染进 <paramref name="target"/>，会清空原有内容。</summary>
    public static void Apply(RichTextBlock target, string markdown)
    {
        target.Blocks.Clear();

        if (string.IsNullOrEmpty(markdown)) return;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;

            var p = new Paragraph();
            AppendInline(p, string.Join(" ", paragraph).Trim());
            target.Blocks.Add(p);
            paragraph.Clear();
        }

        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            // 标题：# / ## / ### —— **字号必须分级**，否则标题和正文一样大，层级完全看不出来
            if (trimmed[0] == '#')
            {
                FlushParagraph();
                target.Blocks.Add(MakeHeading(trimmed));
                continue;
            }

            // 有序列表：1. / 2. / 12. （保留原编号，不重新数）
            if (TryOrderedItem(trimmed, out var marker, out var rest))
            {
                FlushParagraph();
                target.Blocks.Add(MakeListItem(marker + "  ", rest));
                continue;
            }

            // 无序列表：- / * / •
            if (TryBulletItem(trimmed, out var bulletRest))
            {
                FlushParagraph();
                target.Blocks.Add(MakeListItem("•  ", bulletRest));
                continue;
            }

            // 其余按段落累积（相邻行合并成一段，单换行不硬断 —— 中文排版里那多半是软换行）
            paragraph.Add(trimmed);
        }

        FlushParagraph();
    }

    /// <summary>
    /// 把 Markdown 渲染进一个 <see cref="StackPanel"/>：文本块（段落 / 标题 / 列表）走
    /// <see cref="RichTextBlock"/>，表格走真正的 <see cref="Grid"/>（2026-10-23）。
    /// </summary>
    /// <param name="foreground">正文颜色。⚠️ StackPanel 没有 Foreground 可继承，必须显式往下传。</param>
    public static void Apply(
        StackPanel host, string markdown,
        Brush? foreground = null, double fontSize = 15, double lineHeight = 25)
    {
        host.Children.Clear();

        if (string.IsNullOrEmpty(markdown)) return;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');

        RichTextBlock? text = null;
        var paragraph = new List<string>();

        RichTextBlock EnsureText()
        {
            if (text is not null) return text;

            text = new RichTextBlock
            {
                FontSize = fontSize,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = lineHeight,
            };

            if (foreground is not null) text.Foreground = foreground;

            host.Children.Add(text);
            return text;
        }

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;

            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
            AppendInline(p, string.Join(" ", paragraph).Trim());
            EnsureText().Blocks.Add(p);
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            // 表格：本行以 | 开头，且下一行是 |---|---| 这种分隔行
            if (trimmed[0] == '|' && i + 1 < lines.Length && IsTableDivider(lines[i + 1]))
            {
                FlushParagraph();
                host.Children.Add(BuildTable(lines, i, foreground, out var consumed));
                i = consumed;
                text = null;      // 表格之后的文本另起一个 RichTextBlock
                continue;
            }

            if (trimmed[0] == '#')
            {
                FlushParagraph();
                EnsureText().Blocks.Add(MakeHeading(trimmed));
                continue;
            }

            if (TryOrderedItem(trimmed, out var marker, out var rest))
            {
                FlushParagraph();
                EnsureText().Blocks.Add(MakeListItem(marker + "  ", rest));
                continue;
            }

            if (TryBulletItem(trimmed, out var bulletRest))
            {
                FlushParagraph();
                EnsureText().Blocks.Add(MakeListItem("•  ", bulletRest));
                continue;
            }

            paragraph.Add(trimmed);
        }

        FlushParagraph();
    }

    /// <summary><c>#</c> / <c>##</c> / <c>###</c> 标题。</summary>
    private static Paragraph MakeHeading(string trimmed)
    {
        var level = 0;
        while (level < trimmed.Length && trimmed[level] == '#') level++;

        var p = new Paragraph
        {
            FontSize = level switch { 1 => 21.0, 2 => 18.0, _ => 16.0 },
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, level == 1 ? 12 : 9, 0, 3),
        };

        AppendInline(p, trimmed[level..].Trim());
        return p;
    }

    /// <summary>列表项：标记（原样保留）+ 内容。</summary>
    private static Paragraph MakeListItem(string marker, string rest)
    {
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, 3) };
        p.Inlines.Add(new Run { Text = marker });
        AppendInline(p, rest);
        return p;
    }

    private static bool TryBulletItem(string trimmed, out string rest)
    {
        rest = "";

        if (!trimmed.StartsWith("- ", StringComparison.Ordinal)
            && !trimmed.StartsWith("* ", StringComparison.Ordinal)
            && !trimmed.StartsWith("• ", StringComparison.Ordinal))
            return false;

        rest = trimmed[2..].Trim();
        return true;
    }

    // ══════════════════════════════════════════════════════
    //  表格
    // ══════════════════════════════════════════════════════

    /// <summary>是不是 <c>|---|:--:|---|</c> 这种分隔行（去掉竖线、冒号、横线和空白后什么都不剩）。</summary>
    private static bool IsTableDivider(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] != '|') return false;

        foreach (var ch in trimmed)
        {
            if (ch is '|' or '-' or ':' or ' ' or '\t') continue;
            return false;
        }

        return trimmed.Count(ch => ch == '-') >= 3;
    }

    /// <summary>切一行表格：去掉首尾竖线再按竖线分区。</summary>
    private static List<string> SplitRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|')) trimmed = trimmed[..^1];

        var cells = new List<string>();
        foreach (var part in trimmed.Split('|')) cells.Add(part.Trim());
        return cells;
    }

    /// <summary>
    /// 从 <paramref name="start"/> 起把一整张表建成圆角边框包着的 <see cref="Grid"/>。
    /// </summary>
    /// <param name="consumed">最后一行表格的下标（调用方据此跳过）。</param>
    private static Border BuildTable(string[] lines, int start, Brush? foreground, out int consumed)
    {
        var rows = new List<List<string>> { SplitRow(lines[start]) };

        var i = start + 2;      // 跳过表头与分隔行
        while (i < lines.Length)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed[0] != '|') break;

            rows.Add(SplitRow(trimmed));
            i++;
        }

        consumed = i - 1;

        var columns = 1;
        foreach (var row in rows) columns = Math.Max(columns, row.Count);

        // 列宽按内容长度分配：给每列一个下限，免得「软件」这种两字列被挤成一条缝
        var weights = new double[columns];
        foreach (var row in rows)
            for (var c = 0; c < columns; c++)
                weights[c] = Math.Max(weights[c], c < row.Count ? PlainText(row[c]).Length : 0);

        var total = 0.0;
        for (var c = 0; c < columns; c++)
        {
            weights[c] = Math.Max(weights[c], 4);
            total += weights[c];
        }

        var grid = new Grid();

        for (var c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(weights[c] / total, GridUnitType.Star),
            });

        for (var r = 0; r < rows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var stroke = Res("CardStrokeColorDefaultBrush");
        var headerFill = Res("CardBackgroundFillColorDefaultBrush");

        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                var text = c < rows[r].Count ? rows[r][c] : "";

                var cell = new Border
                {
                    Padding = new Thickness(10, 7, 10, 7),
                    Background = r == 0 ? headerFill : null,
                    BorderBrush = stroke,
                    BorderThickness = new Thickness(
                        0,
                        0,
                        c == columns - 1 ? 0 : 0.5,
                        r == rows.Count - 1 ? 0 : 0.5),
                    Child = new TextBlock
                    {
                        Text = PlainText(text),
                        FontSize = 13.5,
                        LineHeight = 20,
                        TextWrapping = TextWrapping.Wrap,
                        FontWeight = r == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                        Foreground = foreground,
                    },
                };

                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }

        return new Border
        {
            BorderThickness = new Thickness(0.5),
            BorderBrush = stroke,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 6, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = grid,
        };
    }

    /// <summary>去掉行内标记，表格单元格只显示纯文本（`**` / 反引号 / 链接语法）。</summary>
    private static string PlainText(string text)
    {
        var s = text.Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal);

        // [文字](url) → 文字（表格里放不下链接，留文字就够）
        var open = s.IndexOf('[');
        while (open >= 0)
        {
            var close = s.IndexOf(']', open + 1);
            var parenStart = close >= 0 ? s.IndexOf('(', close + 1) : -1;
            var parenEnd = parenStart >= 0 ? s.IndexOf(')', parenStart + 1) : -1;

            if (close < 0 || parenStart != close + 1 || parenEnd < 0) break;

            s = s[..open] + s[(open + 1)..close] + s[(parenEnd + 1)..];
            open = s.IndexOf('[', open);
        }

        return s.Trim();
    }

    /// <summary>取主题资源里的画刷（取不到就返回 null，UI 仍然可用）。</summary>
    private static Brush? Res(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;

    /// <summary>识别「1. 」开头的有序列表项，marker 原样保留（不重新编号）。</summary>
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

    /// <summary>
    /// 行内解析：只认 <c>**粗体**</c> 和 <c>`代码`</c>。
    /// 用最简单的分隔符切分 —— 成对的星号交替出现即为粗体区间。
    /// </summary>
    private static void AppendInline(Paragraph paragraph, string text)
    {
        if (text.Length == 0) return;

        var bold = text.Split("**");
        for (var i = 0; i < bold.Length; i++)
        {
            if (bold[i].Length == 0) continue;

            var isBold = i % 2 == 1;
            AppendCode(paragraph, bold[i], isBold);
        }
    }

    /// <summary>段内再切一层反引号，代码片段用等宽字体。</summary>
    private static void AppendCode(Paragraph paragraph, string text, bool bold)
    {
        var parts = text.Split('`');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;

            var run = new Run { Text = parts[i] };

            if (bold) run.FontWeight = FontWeights.SemiBold;
            if (i % 2 == 1) run.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas");

            paragraph.Inlines.Add(run);
        }
    }
}
