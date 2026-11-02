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

/// <summary>
/// 文档正文渲染（需求 15）。把 <see cref="DocContent"/> 变成原生控件：
/// 标题 / 段落 / 列表 / 引用 / 代码块 / 图片，PDF 则逐页渲染成图片。
///
/// <para>
/// ⛔ 不引第三方 Markdown / Word / PDF 库：Markdown 与 docx 自己解析（见同名解析器），
/// PDF 用系统自带的 <see cref="PdfDocument"/>，发布包不会因此变大。
/// </para>
/// <para>
/// 文字一律可选中（<c>IsTextSelectionEnabled</c>）—— 文章常常要照着做操作，不能选就得手抄。
/// 链接点击走 <see cref="DocLinks"/>，与外部 <c>csh://</c> 链接同一条路。
/// </para>
/// </summary>
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

    /// <summary>渲染一篇文章。同一个实例可以反复调用（会先清空）。</summary>
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

    // ── 块 ───────────────────────────────────────────────────────

    private void RenderBlocks(IReadOnlyList<DocBlock> blocks, string title)
    {
        // 页头已经是大标题，正文第一块若就是同一个标题（md/docx 的 H1、txt 的首行）就不再重复一遍
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

    /// <summary>正文第一块是不是"又把标题写了一遍"。</summary>
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

            var marker = new TextBlock
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
        return new Border
        {
            Background = Res("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = Res("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10, 14, 12),
            Margin = new Thickness(0, 4, 0, 14),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new TextBlock
            {
                Text = code.Text,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                LineHeight = 20,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            },
        };
    }

    // ── 图片 ─────────────────────────────────────────────────────

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

    // ── PDF ─────────────────────────────────────────────────────

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

    // ── 行内 ─────────────────────────────────────────────────────

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
