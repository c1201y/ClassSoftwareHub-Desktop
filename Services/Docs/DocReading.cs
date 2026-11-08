using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services.Docs;

/// <summary>
/// 把已缓存的正文转成纯文本（给 Hub AI 的 <c>read_document</c> 用）。
///
/// <para>
/// Markdown / 纯文本原样交出（模型读得懂 Markdown）；docx 由解析结果拼回带标题层级的文字；
/// PDF 是版式页面、没有可提取的文字层，如实说明读不了，不硬编内容。
/// </para>
/// </summary>
public static class DocReading
{
    public static async Task<string> ToTextAsync(DocContent content, CancellationToken ct = default)
    {
        switch (content.Type)
        {
            case "markdown":
            case "txt":
                return await File.ReadAllTextAsync(content.FilePath, ct);

            case "docx":
            {
                var bytes = await File.ReadAllBytesAsync(content.FilePath, ct);
                return FromBlocks(DocxDocParser.Parse(bytes));
            }

            default:
                return "（这是一份 PDF 文档，正文是版式页面，没有可直接读取的文字层。）";
        }
    }

    private static string FromBlocks(IReadOnlyList<DocBlock> blocks)
    {
        var text = new StringBuilder();

        foreach (var block in blocks)
        {
            switch (block)
            {
                case DocHeadingBlock heading:
                    text.Append('#', Math.Clamp(heading.Level, 1, 6))
                        .Append(' ')
                        .AppendLine(Plain(heading.Runs));
                    break;

                case DocParagraphBlock paragraph:
                    text.AppendLine(Plain(paragraph.Runs)).AppendLine();
                    break;

                case DocListBlock list:
                    foreach (var item in list.Items)
                    {
                        text.Append(' ', item.Level * 2);

                        // 任务列表的 Marker 是空的（界面上画勾选框），转文字时要还原成 - [ ] / - [x]
                        if (item.Checked is bool done)
                            text.Append(done ? "- [x] " : "- [ ] ");
                        else
                            text.Append(item.Marker).Append(' ');

                        text.AppendLine(Plain(item.Runs));
                    }
                    text.AppendLine();
                    break;

                case DocTableBlock table:
                    // 交回 Markdown 表格形式：模型对这种结构最熟，也不会丢掉列与列的对应关系
                    foreach (var row in table.Rows)
                    {
                        text.Append("| ");
                        foreach (var cell in row.Cells)
                            text.Append(Plain(cell.Runs)).Append(" | ");
                        text.AppendLine();
                    }
                    text.AppendLine();
                    break;

                case DocQuoteBlock quote:
                    foreach (var line in quote.Lines)
                        text.Append("> ").AppendLine(Plain(line));
                    text.AppendLine();
                    break;

                case DocCodeBlock code:
                    text.AppendLine("```").AppendLine(code.Text).AppendLine("```").AppendLine();
                    break;

                case DocImageBlock image:
                    text.AppendLine($"[图片{(image.Alt.Length > 0 ? "：" + image.Alt : "")}]");
                    break;

                case DocDividerBlock:
                    text.AppendLine("---");
                    break;
            }
        }

        return text.ToString().Trim();
    }

    private static string Plain(IReadOnlyList<DocRun> runs) =>
        string.Concat(runs.Select(run => run.Text));
}
