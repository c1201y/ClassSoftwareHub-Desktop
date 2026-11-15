using System.Collections.Generic;

namespace ClassSoftwareHub.Desktop.Services.Docs;

/// <summary>
/// 文档正文的中间表示：解析器只产出这些块，渲染器只认这些块。
///
/// <para>
/// 为什么不直接把 Markdown 渲成控件：docx / txt 也要走同一套外观，
/// 中间隔一层之后三种格式的排版（标题分级、列表缩进、引用条、代码框）才是同一份代码。
/// </para>
/// </summary>
public abstract record DocBlock;

/// <summary>一段行内文字。<see cref="Link"/> 为空表示普通文字。</summary>
public sealed record DocRun(
    string Text,
    string? Link = null,
    bool Bold = false,
    bool Italic = false,
    bool Code = false,
    bool Strike = false);

/// <summary>标题。<paramref name="Level"/> 1~6（1 最大）。</summary>
public sealed record DocHeadingBlock(int Level, IReadOnlyList<DocRun> Runs) : DocBlock;

public sealed record DocParagraphBlock(IReadOnlyList<DocRun> Runs) : DocBlock;

/// <summary>
/// 列表里的一项。<paramref name="Marker"/> 是显示用的符号或原编号。
/// <paramref name="Checked"/> 不为空表示这是任务列表项（<c>- [ ]</c> / <c>- [x]</c>），
/// 此时 <paramref name="Marker"/> 不再显示，改画勾选框。
/// </summary>
public sealed record DocListItem(
    int Level, string Marker, IReadOnlyList<DocRun> Runs, bool? Checked = null);

public sealed record DocListBlock(bool Ordered, IReadOnlyList<DocListItem> Items) : DocBlock;

/// <summary>引用块，每一行一个段落。</summary>
public sealed record DocQuoteBlock(IReadOnlyList<IReadOnlyList<DocRun>> Lines) : DocBlock;

public sealed record DocCodeBlock(string Text, string? Language) : DocBlock;

/// <summary>表格单元格的横向对齐，对应 Markdown 分隔行里的 <c>:---</c> / <c>:---:</c> / <c>---:</c>。</summary>
public enum DocAlign
{
    Left,
    Center,
    Right,
}

public sealed record DocTableCell(IReadOnlyList<DocRun> Runs);

public sealed record DocTableRow(IReadOnlyList<DocTableCell> Cells);

/// <summary>
/// 表格。<see cref="Rows"/> 的第 0 行是表头（渲染时加粗并加底色）。
/// <see cref="Alignments"/> 按列给出对齐方式，长度不足时按左对齐处理。
///
/// <para>
/// 为什么单独建块而不是当成若干段落：表格的列宽要整表统一，行与行之间要能对齐边框，
/// 交给段落去拼是拼不出来的（2026-11-08 之前正是按段落显示，正文里直接露出竖线）。
/// </para>
/// </summary>
public sealed record DocTableBlock(
    IReadOnlyList<DocTableRow> Rows, IReadOnlyList<DocAlign> Alignments) : DocBlock;

/// <summary>
/// 图片。<see cref="Data"/> 不为空时直接用内存字节（docx 内嵌图片）；
/// 否则从 <see cref="Source"/> 读 —— 本地缓存路径或 http(s) 地址。
/// </summary>
public sealed record DocImageBlock(string Source, string Alt, byte[]? Data = null) : DocBlock;

public sealed record DocDividerBlock : DocBlock;
