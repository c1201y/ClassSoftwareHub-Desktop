using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// Markdown 的页面呈现（块级排版）。与 <see cref="MarkdownText"/> 的分工：
/// 这里负责「一页文档长什么样」——标题阶梯、段距、引用、表格、代码块；
/// 那边负责 AI 回答那种短文本。
/// 2026-11-21 重写：先切块再渲染；<c>scale</c> 供沉浸模式放大用。
/// </summary>
public static class MarkdownView
{
    private const double BodySize = 16;
    private const double BodyLine = 28;

    public static void Render(StackPanel host, string markdown, Brush? foreground = null, double scale = 1)
    {
        host.Children.Clear();
        if (string.IsNullOrWhiteSpace(markdown)) return;

        if (scale <= 0) scale = 1;

        // WinUI 的 TextBox 交出的是纯 \r 换行，只换 \r\n 会把整篇当成一行。
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        RenderLines(host, lines, foreground, 0, scale);
    }

    private static void RenderLines(StackPanel host, string[] lines, Brush? foreground, double indent, double scale)
    {
        var i = 0;

        while (i < lines.Length)
        {
            var text = lines[i].Trim();

            if (text.Length == 0)
            {
                i++;
                continue;
            }

            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Add(lines[i]);
                    i++;
                }

                i++;
                host.Children.Add(CodeBlock(string.Join("\n", code), indent, scale));
                continue;
            }

            if (IsTableRow(text) && i + 1 < lines.Length && IsTableRow(lines[i + 1].Trim()))
            {
                i = AppendTable(host, lines, i, foreground, indent, scale) + 1;
                continue;
            }

            if (IsRule(text))
            {
                host.Children.Add(Rule(indent, scale));
                i++;
                continue;
            }

            if (IsHeading(text, out var level))
            {
                host.Children.Add(Heading(text[level..].Trim(), level, indent, foreground, scale));
                i++;
                continue;
            }

            if (text.StartsWith(">", StringComparison.Ordinal))
            {
                var inner = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith(">", StringComparison.Ordinal))
                {
                    var body = lines[i].TrimStart()[1..];
                    if (body.StartsWith(' ')) body = body[1..];
                    inner.Add(body);
                    i++;
                }

                host.Children.Add(QuoteBlock(inner.ToArray(), foreground, indent, scale));
                continue;
            }

            if ((text.StartsWith("![", StringComparison.Ordinal)
                 || text.StartsWith("<img", StringComparison.OrdinalIgnoreCase))
                && MarkdownText.TryImage(text, out var alt, out var source))
            {
                host.Children.Add(ImageBlock(alt, source, indent, scale));
                i++;
                continue;
            }

            if (TryBullet(text, out var bulletText))
            {
                host.Children.Add(ListItem(BulletMark(bulletText), TrimTask(bulletText), indent, foreground, scale));
                i++;
                continue;
            }

            if (TryOrdered(text, out var number, out var orderedText))
            {
                host.Children.Add(ListItem(number, orderedText, indent, foreground, scale));
                i++;
                continue;
            }

            // 普通段落：连续的非空行合成一段，遇到下一个块的起手就停下。
            var buffer = new List<string> { text };
            i++;

            while (i < lines.Length)
            {
                if (StartsBlock(lines, i)) break;
                buffer.Add(lines[i].Trim());
                i++;
            }

            host.Children.Add(Paragraph(JoinLines(buffer), foreground, indent, scale));
        }
    }

    // ── 块级元素 ────────────────────────────────────────────────

    private static RichTextBlock Heading(string text, int level, double indent, Brush? foreground, double scale)
    {
        var size = level switch
        {
            1 => 30.0,
            2 => 23.0,
            3 => 18.5,
            4 => 16.5,
            _ => 15.5,
        } * scale;

        var block = new RichTextBlock
        {
            FontSize = size,
            FontWeight = FontWeights.Bold,
            LineHeight = Math.Round(size * 1.4),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(indent, (level <= 2 ? 22 : 15) * scale, 0, (level <= 2 ? 9 : 6) * scale),
            IsTextSelectionEnabled = true,
        };

        if (foreground is not null) block.Foreground = foreground;

        var paragraph = new Paragraph();
        MarkdownText.AppendInline(paragraph, text);
        block.Blocks.Add(paragraph);
        return block;
    }

    private static RichTextBlock Paragraph(string text, Brush? foreground, double indent, double scale)
    {
        var block = new RichTextBlock
        {
            FontSize = BodySize * scale,
            LineHeight = BodyLine * scale,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(indent, 0, 0, 14 * scale),
            IsTextSelectionEnabled = true,
        };

        if (foreground is not null) block.Foreground = foreground;

        var paragraph = new Paragraph();
        MarkdownText.AppendInline(paragraph, text);
        block.Blocks.Add(paragraph);
        return block;
    }

    private static Grid ListItem(string marker, string text, double indent, Brush? foreground, double scale)
    {
        var row = new Grid { Margin = new Thickness(indent + 2 * scale, 0, 0, 6 * scale) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24 * scale) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var bullet = new TextBlock
        {
            Text = marker,
            FontSize = BodySize * scale,
            LineHeight = BodyLine * scale,
            Width = 24 * scale,
            Foreground = foreground,
        };

        var content = new RichTextBlock
        {
            FontSize = BodySize * scale,
            LineHeight = BodyLine * scale,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };

        if (foreground is not null) content.Foreground = foreground;

        var paragraph = new Paragraph();
        MarkdownText.AppendInline(paragraph, text);
        content.Blocks.Add(paragraph);

        Grid.SetColumn(content, 1);
        row.Children.Add(bullet);
        row.Children.Add(content);
        return row;
    }

    private static Border QuoteBlock(string[] inner, Brush? foreground, double indent, double scale)
    {
        var panel = new StackPanel();
        RenderLines(panel, inner, Res("TextFillColorSecondaryBrush") ?? foreground, 0, scale);

        return new Border
        {
            BorderBrush = Res("AccentFillColorDefaultBrush"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(16 * scale, 2, 0, 2),
            Margin = new Thickness(indent, 2, 0, 10 * scale),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = panel,
        };
    }

    private static Border CodeBlock(string code, double indent, double scale)
    {
        var body = new TextBlock
        {
            Text = code,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13.5 * scale,
            LineHeight = 22 * scale,
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true,
        };

        return new Border
        {
            Background = Res("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = Res("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14 * scale, 12 * scale, 14 * scale, 12 * scale),
            Margin = new Thickness(indent, 2, 0, 16 * scale),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = body,
            },
        };
    }

    private static FrameworkElement ImageBlock(string alt, string source, double indent, double scale)
    {
        try
        {
            var uri = source.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                      || source.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                ? new Uri(source)
                : new Uri(System.IO.Path.GetFullPath(source));

            var image = new Image
            {
                Source = new BitmapImage(uri) { DecodePixelWidth = (int)(1000 * scale) },
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                MaxWidth = 700 * scale,
            };

            return new Border
            {
                Child = image,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(indent, 4, 0, 16 * scale),
            };
        }
        catch
        {
            return Paragraph(alt.Length > 0 ? alt : source, null, indent, scale);
        }
    }

    private static Border Rule(double indent, double scale) => new()
    {
        Height = 1,
        Background = Res("DividerStrokeColorDefaultBrush"),
        Margin = new Thickness(indent, 20 * scale, 0, 20 * scale),
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    /// <summary>表格：表头浅底、单元格 12×8、细描边。返回最后消费到的行号。</summary>
    private static int AppendTable(
        StackPanel host, string[] lines, int start, Brush? foreground, double indent, double scale)
    {
        var rows = new List<List<string>> { SplitRow(lines[start]) };

        var i = start + 1;
        if (i < lines.Length && IsDividerRow(lines[i])) i++;

        while (i < lines.Length)
        {
            var text = lines[i].Trim();
            if (!IsTableRow(text)) break;
            rows.Add(SplitRow(text));
            i++;
        }

        var columns = 1;
        foreach (var row in rows) columns = Math.Max(columns, row.Count);

        var weights = new double[columns];
        var total = 0.0;

        foreach (var row in rows)
        {
            for (var c = 0; c < columns; c++)
            {
                var length = c < row.Count ? PlainText(row[c]).Length : 0;
                weights[c] = Math.Max(weights[c], Math.Max(length, 4));
            }
        }

        foreach (var weight in weights) total += weight;

        var grid = new Grid();
        for (var c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(weights[c] / total, GridUnitType.Star),
            });

        for (var r = 0; r < rows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var stroke = Res("CardStrokeColorDefaultBrush");
        var head = Res("ControlFillColorSecondaryBrush") ?? Res("CardBackgroundFillColorDefaultBrush");

        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                var text = c < rows[r].Count ? rows[r][c] : "";

                var cell = new Border
                {
                    Padding = new Thickness(12 * scale, 8 * scale, 12 * scale, 8 * scale),
                    Background = r == 0 ? head : null,
                    BorderBrush = stroke,
                    BorderThickness = new Thickness(
                        0,
                        r == rows.Count - 1 ? 0 : 0.5,
                        c == columns - 1 ? 0 : 0.5,
                        0),
                    Child = new TextBlock
                    {
                        Text = PlainText(text),
                        FontSize = 14 * scale,
                        LineHeight = 21 * scale,
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

        host.Children.Add(new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = stroke,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(indent, 2, 0, 16 * scale),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = grid,
        });

        return i - 1;
    }

    // ── 判定 ────────────────────────────────────────────────────

    private static bool StartsBlock(string[] lines, int i)
    {
        var text = lines[i].Trim();

        if (text.Length == 0) return true;
        if (text.StartsWith("```", StringComparison.Ordinal)) return true;
        if (text.StartsWith(">", StringComparison.Ordinal)) return true;
        if (IsRule(text)) return true;
        if (IsHeading(text, out _)) return true;
        if (TryBullet(text, out _) || TryOrdered(text, out _, out _)) return true;
        if (IsTableRow(text) && i + 1 < lines.Length && IsTableRow(lines[i + 1].Trim())) return true;
        if (text.StartsWith("![", StringComparison.Ordinal)) return true;
        if (text.StartsWith("<img", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static bool IsHeading(string text, out int level)
    {
        level = 0;
        while (level < text.Length && text[level] == '#') level++;

        return level is > 0 and <= 6
               && level < text.Length
               && text[level] == ' ';
    }

    /// <summary>分隔线：三个及以上的 - * _，允许中间有空格。</summary>
    private static bool IsRule(string text)
    {
        if (text.Length < 3) return false;

        var ch = text[0];
        if (ch is not ('-' or '*' or '_')) return false;

        var count = 0;
        foreach (var c in text)
        {
            if (c == ch) count++;
            else if (c is not ' ' and not '\t') return false;
        }

        return count >= 3;
    }

    private static bool IsTableRow(string text)
        => text.Length > 0 && (text[0] == '|' || text[0] == '｜');

    private static bool IsDividerRow(string line)
    {
        var text = line.Trim().Replace('｜', '|');
        if (text.Length == 0 || text[0] != '|') return false;

        foreach (var c in text)
        {
            if (c is '|' or '-' or ':' or ' ' or '\t') continue;
            return false;
        }

        return text.Contains("---", StringComparison.Ordinal);
    }

    private static bool TryBullet(string text, out string rest)
    {
        rest = "";

        if (!text.StartsWith("- ", StringComparison.Ordinal)
            && !text.StartsWith("* ", StringComparison.Ordinal)
            && !text.StartsWith("+ ", StringComparison.Ordinal)
            && !text.StartsWith("• ", StringComparison.Ordinal))
            return false;

        rest = text[2..].Trim();
        return rest.Length > 0;
    }

    private static bool TryOrdered(string text, out string marker, out string rest)
    {
        marker = "";
        rest = "";

        var i = 0;
        while (i < text.Length && char.IsDigit(text[i])) i++;

        if (i == 0 || i + 1 >= text.Length) return false;
        if (text[i] != '.' && text[i] != ')') return false;
        if (text[i + 1] != ' ') return false;

        marker = text[..(i + 1)];
        rest = text[(i + 1)..].Trim();
        return true;
    }

    private static string BulletMark(string text)
        => text.StartsWith("[ ]", StringComparison.Ordinal) ? "☐"
           : text.StartsWith("[x]", StringComparison.OrdinalIgnoreCase) ? "☑"
           : "•";

    private static string TrimTask(string text)
    {
        if (text.StartsWith("[ ]", StringComparison.Ordinal) && text.Length >= 3) return text[3..].TrimStart();
        if (text.StartsWith("[x]", StringComparison.OrdinalIgnoreCase) && text.Length >= 3) return text[3..].TrimStart();
        return text;
    }

    private static List<string> SplitRow(string line)
    {
        var text = line.Trim().Replace('｜', '|');
        if (text.StartsWith('|')) text = text[1..];
        if (text.EndsWith('|')) text = text[..^1];

        var cells = new List<string>();
        foreach (var part in text.Split('|')) cells.Add(part.Trim());
        return cells;
    }

    private static string PlainText(string text)
    {
        var value = text.Replace("**", "", StringComparison.Ordinal)
                        .Replace("__", "", StringComparison.Ordinal)
                        .Replace("~~", "", StringComparison.Ordinal)
                        .Replace("`", "", StringComparison.Ordinal);

        value = Regex.Replace(value, @"\[([^\]]+)\]\([^)]*\)", "$1");
        value = Regex.Replace(value, @"\*([^*\n]+)\*", "$1");
        return value.Trim();
    }

    /// <summary>中文软换行直接拼接，英文之间补一个空格。</summary>
    private static string JoinLines(List<string> lines)
    {
        if (lines.Count == 1) return lines[0];

        var builder = new System.Text.StringBuilder(lines[0]);

        for (var i = 1; i < lines.Count; i++)
        {
            var previous = builder[^1];
            var next = lines[i][0];

            if (previous < 128 && next < 128) builder.Append(' ');
            builder.Append(lines[i]);
        }

        return builder.ToString();
    }

    private static Brush? Res(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;
}
