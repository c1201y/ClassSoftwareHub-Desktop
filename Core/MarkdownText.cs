using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClassSoftwareHub.Desktop.Core;






















public static class MarkdownText
{
    
    public static void Apply(RichTextBlock target, string markdown)
    {
        target.Blocks.Clear();

        if (string.IsNullOrEmpty(markdown)) return;

        // WinUI 的 TextBox 交出的是纯 \r 换行，只换 \r\n 会把整篇当成一行（2026-11-21 的导出事故）。
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
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

        // WinUI 的 TextBox 交出的是纯 \r 换行，只换 \r\n 会把整篇当成一行（2026-11-21 的导出事故）。
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

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

            
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Add(lines[i]);
                    i++;
                }

                host.Children.Add(BuildCodeBlock(string.Join("\n", code)));
                text = null;
                continue;
            }

            if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                FlushParagraph();
                var quote = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith(">", StringComparison.Ordinal))
                {
                    quote.Add(lines[i].TrimStart()[1..].Trim());
                    i++;
                }

                i--;
                host.Children.Add(BuildQuote(string.Join(" ", quote), foreground, fontSize, lineHeight));
                text = null;
                continue;
            }

            if (trimmed is "---" or "***" or "___")
            {
                FlushParagraph();
                host.Children.Add(new Border
                {
                    Height = 1,
                    Background = Res("DividerStrokeColorDefaultBrush"),
                    Margin = new Thickness(0, 10, 0, 10),
                });
                text = null;
                continue;
            }

            if (IsTableLine(trimmed) && i + 1 < lines.Length && IsTableLine(lines[i + 1].Trim()))
            {
                FlushParagraph();
                host.Children.Add(BuildTable(lines, i, foreground, out var consumed));
                i = consumed;
                text = null;      
                continue;
            }

            // 独占一行的图片：Markdown 写法 ![说明](地址)，或从 Word/网页粘贴的 <img src="…">。
            if ((trimmed.StartsWith("![", StringComparison.Ordinal)
                 || trimmed.StartsWith("<img", StringComparison.OrdinalIgnoreCase))
                && TryImage(trimmed, out var alt, out var source))
            {
                FlushParagraph();
                host.Children.Add(BuildImage(alt, source, foreground, fontSize));
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
                EnsureText().Blocks.Add(MakeListItem(marker + "  ", rest, IndentOf(lines[i]) * 14));
                continue;
            }

            if (TryBulletItem(trimmed, out var bulletRest))
            {
                FlushParagraph();
                var bullet = bulletRest.StartsWith("[ ]", StringComparison.Ordinal) ? "☐  "
                    : bulletRest.StartsWith("[x]", StringComparison.OrdinalIgnoreCase) ? "☑  "
                    : "•  ";
                EnsureText().Blocks.Add(MakeListItem(bullet, TrimTaskBox(bulletRest), IndentOf(lines[i]) * 14));
                continue;
            }

            paragraph.Add(trimmed);
        }

        FlushParagraph();
    }

    
    /// <summary>代码块：等宽字体 + 卡片底 + 横向滚动，长行不折行。</summary>
    private static Border BuildCodeBlock(string code)
    {
        var body = new TextBlock
        {
            Text = code,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            LineHeight = 20,
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true,
        };

        return new Border
        {
            Background = Res("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = Res("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(0.5),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 6, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = body,
            },
        };
    }

    /// <summary>引用块：左侧强调色竖线 + 次要色文字。</summary>
    private static Border BuildQuote(string quote, Brush? foreground, double fontSize, double lineHeight)
    {
        var text = new RichTextBlock
        {
            FontSize = fontSize,
            LineHeight = lineHeight,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("TextFillColorSecondaryBrush") ?? foreground,
        };
        var p = new Paragraph();
        AppendInline(p, quote);
        text.Blocks.Add(p);

        return new Border
        {
            BorderBrush = Res("AccentFillColorDefaultBrush"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 2, 0, 2),
            Margin = new Thickness(0, 4, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = text,
        };
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

    
    private static Paragraph MakeListItem(string marker, string rest, double indent = 0)
    {
        var p = new Paragraph
        {
            Margin = new Thickness(indent, 0, 0, 3),
        };
        p.Inlines.Add(new Run { Text = marker });
        AppendInline(p, rest);
        return p;
    }

    /// <summary>独占一行的图片：![说明](地址) 或 &lt;img src="地址"&gt;。导出那边共用同一套判定。</summary>
    internal static bool TryImage(string line, out string alt, out string source)
    {
        alt = "";
        source = "";

        var markdown = System.Text.RegularExpressions.Regex.Match(
            line, @"^!\[([^\]]*)\]\(([^)\s]+)\)$");

        if (markdown.Success)
        {
            alt = markdown.Groups[1].Value;
            source = markdown.Groups[2].Value;
            return source.Length > 0;
        }

        var tag = System.Text.RegularExpressions.Regex.Match(
            line, @"<img\b[^>]*?\bsrc\s*=\s*[""'](?<src>[^""']+)[""'][^>]*>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!tag.Success) return false;

        source = System.Net.WebUtility.HtmlDecode(tag.Groups["src"].Value).Trim();
        if (source.Length == 0) return false;

        // 能取到 alt 或 title 就当说明文字。
        var altMatch = System.Text.RegularExpressions.Regex.Match(
            line, @"\balt\s*=\s*[""'](?<alt>[^""']*)[""']",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        alt = altMatch.Success ? System.Net.WebUtility.HtmlDecode(altMatch.Groups["alt"].Value) : "";
        return true;
    }

    private static FrameworkElement BuildImage(string alt, string source, Brush? foreground, double fontSize)
    {
        try
        {
            var uri = source.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                || source.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                ? new Uri(source)
                : new Uri(System.IO.Path.GetFullPath(source));

            var image = new Image
            {
                Source = new BitmapImage(uri) { DecodePixelWidth = 900 },
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                MaxWidth = 680,
            };

            return new Border
            {
                Child = image,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 8, 0, 12),
            };
        }
        catch
        {
            // 地址解析不了就退回说明文字，不让整页渲染失败。
            return new TextBlock
            {
                Text = alt.Length > 0 ? alt : source,
                FontSize = fontSize,
                Foreground = foreground,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 8),
            };
        }
    }

    private static string TrimTaskBox(string text)
    {
        if (text.StartsWith("[ ]", StringComparison.Ordinal)) return text[3..].TrimStart();
        if (text.StartsWith("[x]", StringComparison.OrdinalIgnoreCase)) return text[3..].TrimStart();
        return text;
    }

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
        var trimmed = line.Trim().Replace('｜', '|');
        if (trimmed.Length == 0 || trimmed[0] != '|') return false;

        foreach (var ch in trimmed)
        {
            if (ch is '|' or '-' or ':' or ' ' or '\t') continue;
            return false;
        }

        return trimmed.Count(ch => ch == '-') >= 3;
    }

    /// <summary>表格行：以半角或全角竖线开头。中文输入法下常打出全角 ｜。</summary>
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

    
    
    
    
    private static Border BuildTable(string[] lines, int start, Brush? foreground, out int consumed)
    {
        var rows = new List<List<string>> { SplitRow(lines[start]) };

        var i = start + 1;

        // 对齐行（| --- | :--: |）不是数据行，有就跳过。
        if (i < lines.Length && IsTableDivider(lines[i].Trim())) i++;

        // 只要后面还是竖线行就继续收，不再强制要求有对齐行 ——
        // 从 Word、网页粘贴过来的表经常没有对齐行（2026-11-21）。
        while (i < lines.Length)
        {
            var trimmed = lines[i].Trim();
            if (!IsTableLine(trimmed)) break;

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

    
    
    
    
    /// <summary>行内标记渲染：粗体、斜体、删除线、行内代码、链接。<see cref="MarkdownView"/> 复用同一套。</summary>
    internal static void AppendInline(Paragraph paragraph, string text)
    {
        if (text.Length == 0) return;

        var pattern = new System.Text.RegularExpressions.Regex(
            @"\*\*(?<bold>[^*]+)\*\*|\*(?<italic>[^*]+)\*|`(?<code>[^`]+)`|\[(?<lt>[^\]]+)\]\((?<lu>[^)]+)\)");

        var last = 0;
        foreach (System.Text.RegularExpressions.Match m in pattern.Matches(text))
        {
            if (m.Index > last) paragraph.Inlines.Add(new Run { Text = text[last..m.Index] });

            if (m.Groups["bold"].Success)
            {
                paragraph.Inlines.Add(new Run
                {
                    Text = m.Groups["bold"].Value,
                    FontWeight = FontWeights.SemiBold,
                });
            }
            else if (m.Groups["italic"].Success)
            {
                paragraph.Inlines.Add(new Run
                {
                    Text = m.Groups["italic"].Value,
                    FontStyle = Windows.UI.Text.FontStyle.Italic,
                });
            }
            else if (m.Groups["code"].Success)
            {
                paragraph.Inlines.Add(new Run
                {
                    Text = m.Groups["code"].Value,
                    FontFamily = new FontFamily("Consolas"),
                });
            }
            else if (m.Groups["lt"].Success)
            {
                var link = new Hyperlink
                {
                    NavigateUri = Uri.TryCreate(m.Groups["lu"].Value.Trim(), UriKind.Absolute, out var uri) ? uri : null,
                    Foreground = Res("AccentTextFillColorPrimaryBrush"),
                };
                link.Inlines.Add(new Run { Text = m.Groups["lt"].Value });
                paragraph.Inlines.Add(link);
            }

            last = m.Index + m.Length;
        }

        if (last < text.Length) paragraph.Inlines.Add(new Run { Text = text[last..] });
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
