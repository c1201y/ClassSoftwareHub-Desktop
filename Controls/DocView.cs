using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services.Docs;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace ClassSoftwareHub.Desktop.Controls;














public sealed class DocView : UserControl
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly StackPanel _host = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    public DocView()
    {
        Content = _host;
    }

    public bool IsEmpty => _host.Children.Count == 0;

    
    public async Task RenderAsync(DocContent content, CancellationToken ct = default)
    {
        _host.Children.Clear();

        try
        {
            switch (content.Type)
            {
                case "pdf":
                    await RenderPdfAsync(content.FilePath, ct);
                    break;

                case "docx":
                {
                    var bytes = await File.ReadAllBytesAsync(content.FilePath, ct);
                    RenderBlocks(DocxDocParser.Parse(bytes), content.Article.Title);
                    break;
                }

                case "txt":
                {
                    var text = await File.ReadAllTextAsync(content.FilePath, ct);
                    RenderBlocks(PlainTextDocParser.Parse(text), content.Article.Title);
                    break;
                }

                default:
                {
                    var text = await File.ReadAllTextAsync(content.FilePath, ct);
                    RenderBlocks(MarkdownDocParser.Parse(text, content.ResolveResource), content.Article.Title);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Exception("docs", ex, $"渲染文档 {content.Article.Id}");
            _host.Children.Add(Notice("正文无法显示。可以返回列表重新打开一次，让这篇文档重新下载。"));
        }
    }

    public void Clear() => _host.Children.Clear();

    

    private void RenderBlocks(IReadOnlyList<DocBlock> blocks, string title)
    {
        
        var start = 0;
        if (blocks.Count > 0 && title.Trim().Length > 0 && IsTitleBlock(blocks[0], title.Trim()))
            start = 1;

        for (var i = start; i < blocks.Count; i++)
        {
            var block = blocks[i];
            switch (block)
            {
                case DocHeadingBlock heading:
                    _host.Children.Add(BuildHeading(heading));
                    break;

                case DocParagraphBlock paragraph:
                    _host.Children.Add(BuildText(paragraph.Runs));
                    break;

                case DocListBlock list:
                    AddList(list);
                    break;

                case DocQuoteBlock quote:
                    _host.Children.Add(BuildQuote(quote));
                    break;

                case DocCodeBlock code:
                    _host.Children.Add(BuildCode(code));
                    break;

                case DocTableBlock table:
                    AddTable(table);
                    break;

                case DocImageBlock image:
                    AddImage(image);
                    break;

                case DocDividerBlock:
                    _host.Children.Add(new Border
                    {
                        Height = 1,
                        Background = Res("CardStrokeColorDefaultBrush"),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Margin = new Thickness(0, 18, 0, 18),
                    });
                    break;
            }
        }
    }

    
    private static bool IsTitleBlock(DocBlock block, string title)
    {
        var text = block switch
        {
            DocHeadingBlock { Level: 1 } heading => JoinRuns(heading.Runs),
            DocParagraphBlock paragraph => JoinRuns(paragraph.Runs),
            _ => "",
        };

        var trimmed = text.Trim();
        return trimmed.Length > 0 && string.Equals(trimmed, title, StringComparison.CurrentCultureIgnoreCase);
    }

    private static string JoinRuns(IReadOnlyList<DocRun> runs)
    {
        var text = new StringBuilder();
        foreach (var run in runs) text.Append(run.Text);
        return text.ToString();
    }

    private static TextBlock BuildHeading(DocHeadingBlock heading)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            FontSize = heading.Level switch { 1 => 24, 2 => 20, 3 => 17.5, 4 => 16, _ => 15 },
            Margin = new Thickness(0, heading.Level <= 2 ? 24 : 16, 0, 8),
            IsTextSelectionEnabled = true,
        };

        AppendRuns(text.Inlines, heading.Runs);
        return text;
    }

    private static RichTextBlock BuildText(
        IReadOnlyList<DocRun> runs, double fontSize = 15, double bottomMargin = 10)
    {
        var text = new RichTextBlock
        {
            FontSize = fontSize,
            LineHeight = 27,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Margin = new Thickness(0, 0, 0, bottomMargin),
        };

        var paragraph = new Paragraph();
        AppendRuns(paragraph.Inlines, runs);
        text.Blocks.Add(paragraph);
        return text;
    }

    private void AddList(DocListBlock list)
    {
        foreach (var item in list.Items)
        {
            var row = new Grid { Margin = new Thickness(item.Level * 20, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            
            
            FrameworkElement marker = item.Checked is bool done
                ? new FontIcon
                {
                    Glyph = done ? "\uE73A" : "\uE739",
                    FontSize = 14,
                    MinWidth = 14,
                    Margin = new Thickness(0, 5, 8, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    Opacity = done ? 0.85 : 0.5,
                }
                : new TextBlock
                {
                    Text = item.Marker,
                    FontSize = 15,
                    LineHeight = 27,
                    MinWidth = 14,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    Opacity = 0.7,
                };

            var content = BuildText(item.Runs, bottomMargin: 0);

            Grid.SetColumn(marker, 0);
            Grid.SetColumn(content, 1);
            row.Children.Add(marker);
            row.Children.Add(content);
            _host.Children.Add(row);
        }

        _host.Children.Add(new Border { Height = 4 });
    }

    private static Border BuildQuote(DocQuoteBlock quote)
    {
        var panel = new StackPanel();
        foreach (var line in quote.Lines)
            panel.Children.Add(BuildText(line, fontSize: 14.5, bottomMargin: 4));

        return new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = Res("CardStrokeColorDefaultBrush"),
            Padding = new Thickness(14, 4, 8, 4),
            Margin = new Thickness(0, 4, 0, 14),
            Child = panel,
        };
    }

    private static Border BuildCode(DocCodeBlock code)
    {
        var stack = new StackPanel();

        
        if (code.Language is { Length: > 0 } language)
        {
            stack.Children.Add(new TextBlock
            {
                Text = language,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11.5,
                Opacity = 0.5,
                Margin = new Thickness(0, 0, 0, 6),
                IsTextSelectionEnabled = true,
            });
        }

        
        
        stack.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = new TextBlock
            {
                Text = code.Text,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                LineHeight = 20,
                TextWrapping = TextWrapping.NoWrap,
                IsTextSelectionEnabled = true,
            },
        });

        return new Border
        {
            Background = Res("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = Res("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10, 14, 12),
            Margin = new Thickness(0, 4, 0, 14),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = stack,
        };
    }

    

    
    
    
    
    
    
    
    
    
    private void AddTable(DocTableBlock table)
    {
        if (table.Rows.Count == 0) return;

        var columns = 0;
        foreach (var row in table.Rows) columns = Math.Max(columns, row.Cells.Count);
        if (columns == 0) return;

        var grid = new Grid();

        var weights = ColumnWeights(table.Rows, columns);
        for (var c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(weights[c], GridUnitType.Star),
            });

        for (var r = 0; r < table.Rows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var isHeader = r == 0;
            for (var c = 0; c < columns; c++)
            {
                var runs = c < table.Rows[r].Cells.Count
                    ? table.Rows[r].Cells[c].Runs
                    : Array.Empty<DocRun>();

                var align = c < table.Alignments.Count ? table.Alignments[c] : DocAlign.Left;
                var cell = BuildCell(runs, align, isHeader);

                
                
                cell.BorderThickness = new Thickness(
                    0,
                    0,
                    c == columns - 1 ? 0 : 1,
                    r == table.Rows.Count - 1 ? 0 : 1);

                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }

        _host.Children.Add(new Border
        {
            BorderBrush = Res("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 6, 0, 16),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = grid,
        });
    }

    private static Border BuildCell(
        IReadOnlyList<DocRun> runs, DocAlign align, bool header)
    {
        var text = new RichTextBlock
        {
            FontSize = 14,
            LineHeight = 22,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
            TextAlignment = align switch
            {
                DocAlign.Center => TextAlignment.Center,
                DocAlign.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            },
        };

        var paragraph = new Paragraph();

        
        
        if (header) paragraph.FontWeight = FontWeights.SemiBold;

        AppendRuns(paragraph.Inlines, runs);
        text.Blocks.Add(paragraph);

        return new Border
        {
            Padding = new Thickness(12, 9, 12, 10),
            Background = header ? Res("SubtleFillColorSecondaryBrush") : null,
            Child = text,
        };
    }

    private static double[] ColumnWeights(
        IReadOnlyList<DocTableRow> rows, int columns)
    {
        var longest = new double[columns];
        for (var c = 0; c < columns; c++) longest[c] = 1;

        foreach (var row in rows)
        {
            for (var c = 0; c < columns && c < row.Cells.Count; c++)
            {
                var width = 0;
                foreach (var run in row.Cells[c].Runs) width += DisplayWidth(run.Text);
                longest[c] = Math.Max(longest[c], width);
            }
        }

        var weights = new double[columns];
        for (var c = 0; c < columns; c++)
        {
            
            weights[c] = Math.Clamp(longest[c] / 12.0, 1.0, 3.0);
        }

        return weights;
    }

    
    private static int DisplayWidth(string text)
    {
        var width = 0;
        foreach (var ch in text) width += ch >= 0x2E80 ? 2 : 1;
        return width;
    }

    

    private void AddImage(DocImageBlock block)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 16) };

        var image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 760,
        };
        panel.Children.Add(image);

        if (block.Alt.Length > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = block.Alt,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.6,
                Margin = new Thickness(0, 6, 0, 0),
            });
        }

        _host.Children.Add(panel);
        _ = LoadImageAsync(block, image, panel);
    }

    private static async Task LoadImageAsync(DocImageBlock block, Image image, StackPanel panel)
    {
        var bitmap = await ReadBitmapAsync(block);
        if (bitmap is null)
        {
            panel.Children.Clear();
            panel.Children.Add(Notice(block.Alt.Length > 0 ? $"图片无法显示：{block.Alt}" : "图片无法显示"));
            return;
        }

        image.Source = bitmap;
    }

    private static async Task<BitmapImage?> ReadBitmapAsync(DocImageBlock block)
    {
        try
        {
            byte[]? data = block.Data;

            if (data is null && block.Source.Length > 0)
            {
                if (block.Source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || block.Source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    data = await Http.GetByteArrayAsync(block.Source);
                }
                else if (File.Exists(block.Source))
                {
                    data = await File.ReadAllBytesAsync(block.Source);
                }
            }

            if (data is null || data.Length == 0) return null;

            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(data.AsBuffer());
            stream.Seek(0);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch (Exception ex)
        {
            AppLog.Warn("docs", "图片读取失败：" + ex.Message);
            return null;
        }
    }

    

    private async Task RenderPdfAsync(string path, CancellationToken ct)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var random = file.AsRandomAccessStream();

        var document = await PdfDocument.LoadFromStreamAsync(random);
        if (document.PageCount == 0)
        {
            _host.Children.Add(Notice("这份 PDF 没有可显示的页面。"));
            return;
        }

        for (uint index = 0; index < document.PageCount; index++)
        {
            ct.ThrowIfCancellationRequested();

            using var page = document.GetPage(index);
            using var stream = new InMemoryRandomAccessStream();

            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = 1240 });
            stream.Seek(0);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);

            _host.Children.Add(new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 12),
            });
        }
    }

    

    private static void AppendRuns(InlineCollection inlines, IReadOnlyList<DocRun> runs)
    {
        foreach (var run in runs)
        {
            var pieces = run.Text.Split('\n');
            for (var i = 0; i < pieces.Length; i++)
            {
                if (i > 0) inlines.Add(new LineBreak());
                if (pieces[i].Length == 0) continue;

                var text = new Run { Text = pieces[i] };
                StyleRun(text, run);

                if (run.Link is { Length: > 0 } link)
                {
                    var hyperlink = new Hyperlink();
                    hyperlink.Inlines.Add(text);
                    hyperlink.Click += (_, _) => { _ = DocLinks.OpenAsync(link); };
                    inlines.Add(hyperlink);
                }
                else
                {
                    inlines.Add(text);
                }
            }
        }
    }

    private static void StyleRun(Run run, DocRun style)
    {
        if (style.Bold) run.FontWeight = FontWeights.SemiBold;
        if (style.Italic) run.FontStyle = Windows.UI.Text.FontStyle.Italic;
        if (style.Code) run.FontFamily = new FontFamily("Consolas");
        if (style.Strike) run.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
    }

    private static TextBlock Notice(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 13.5,
        Opacity = 0.65,
        Margin = new Thickness(0, 4, 0, 12),
    };

    private static Brush? Res(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;
}
