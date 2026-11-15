using System.Collections.Generic;

namespace ClassSoftwareHub.Desktop.Services.Docs;





public static class PlainTextDocParser
{
    public static IReadOnlyList<DocBlock> Parse(string? text)
    {
        var blocks = new List<DocBlock>();
        if (string.IsNullOrWhiteSpace(text)) return blocks;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var current = new List<DocRun>();

        void Flush()
        {
            if (current.Count > 0) blocks.Add(new DocParagraphBlock(current));
            current = new List<DocRun>();
        }

        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
            {
                Flush();
                continue;
            }

            if (current.Count > 0) current.Add(new DocRun("\n"));
            current.AddRange(DocInline.Parse(line.TrimEnd()));
        }

        Flush();
        return blocks;
    }
}
