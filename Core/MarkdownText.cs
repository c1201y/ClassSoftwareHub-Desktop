using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace ClassSoftwareHub.Desktop.Core;






















public static class MarkdownText
{
    
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

            
            if (trimmed[0] == '#')
            {
                FlushParagraph();
                target.Blocks.Add(MakeHeading(trimmed));
                continue;
            }

            
            if (TryOrderedItem(trimmed, out var marker, out var rest))
            {
                FlushParagraph();
                target.Blocks.Add(MakeListItem(marker + "  ", rest));
                continue;
            }

            
            if (TryBulletItem(trimmed, out var bulletRest))
            {
                FlushParagraph();
                target.Blocks.Add(MakeListItem("•  ", bulletRest));
                continue;
            }

            
            paragraph.Add(trimmed);
        }

        FlushParagraph();
    }

    
    
    
    
    
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

            
            if (trimmed[0] == '|' && i + 1 < lines.Length && IsTableDivider(lines[i + 1]))
            {
                FlushParagraph();
                host.Children.Add(BuildTable(lines, i, foreground, out var consumed));
                i = consumed;
                text = null;      
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

    
    private static List<string> SplitRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|')) trimmed = trimmed[..^1];

        var cells = new List<string>();
        foreach (var part in trimmed.Split('|')) cells.Add(part.Trim());
        return cells;
    }

    
    
    
    
    private static Border BuildTable(string[] lines, int start, Brush? foreground, out int consumed)
    {
        var rows = new List<List<string>> { SplitRow(lines[start]) };

        var i = start + 2;      
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

    
    private static string PlainText(string text)
    {
        var s = text.Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal);

        
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

    
    private static Brush? Res(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;

    
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
