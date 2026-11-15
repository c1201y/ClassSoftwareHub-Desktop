using System.Collections.Generic;

namespace ClassSoftwareHub.Desktop.Services.Docs;









public abstract record DocBlock;


public sealed record DocRun(
    string Text,
    string? Link = null,
    bool Bold = false,
    bool Italic = false,
    bool Code = false,
    bool Strike = false);


public sealed record DocHeadingBlock(int Level, IReadOnlyList<DocRun> Runs) : DocBlock;

public sealed record DocParagraphBlock(IReadOnlyList<DocRun> Runs) : DocBlock;






public sealed record DocListItem(
    int Level, string Marker, IReadOnlyList<DocRun> Runs, bool? Checked = null);

public sealed record DocListBlock(bool Ordered, IReadOnlyList<DocListItem> Items) : DocBlock;


public sealed record DocQuoteBlock(IReadOnlyList<IReadOnlyList<DocRun>> Lines) : DocBlock;

public sealed record DocCodeBlock(string Text, string? Language) : DocBlock;


public enum DocAlign
{
    Left,
    Center,
    Right,
}

public sealed record DocTableCell(IReadOnlyList<DocRun> Runs);

public sealed record DocTableRow(IReadOnlyList<DocTableCell> Cells);










public sealed record DocTableBlock(
    IReadOnlyList<DocTableRow> Rows, IReadOnlyList<DocAlign> Alignments) : DocBlock;





public sealed record DocImageBlock(string Source, string Alt, byte[]? Data = null) : DocBlock;

public sealed record DocDividerBlock : DocBlock;
