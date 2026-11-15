using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace ClassSoftwareHub.Desktop.Data;









public static class NameRoster
{
    
    public static readonly string[] TextExts = { ".txt", ".csv", ".tsv" };
    public static readonly string[] ExcelExts = { ".xlsx", ".xlsm" };

    
    private const int MaxNameLength = 40;

    
    public const int MaxNames = 2000;

    private static readonly string[] HeaderWords =
    {
        "姓名", "名字", "学生", "学生姓名", "名单", "人员", "成员", "name", "student", "名字名单",
    };

    
    public static List<string> Read(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        List<string> raw = ext switch
        {
            ".xlsx" or ".xlsm" => ReadXlsx(path),
            _ => ReadText(path),
        };
        return Clean(raw);
    }

    
    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return TextExts.Contains(ext) || ExcelExts.Contains(ext);
    }

    
    
    
    
    

    
    
    
    
    
    public static void WriteText(string path, IEnumerable<string> names)
    {
        var sb = new StringBuilder();
        foreach (var name in names) sb.Append(name).Append("\r\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    
    
    
    
    
    
    
    
    public static void WriteXlsx(string path, IReadOnlyList<string> names)
    {
        var ns = (XNamespace)MainNs;

        var sheetData = new XElement(ns + "sheetData");
        sheetData.Add(new XElement(ns + "row", new XAttribute("r", 1),
            TextCell("A1", "序号"), TextCell("B1", "姓名")));
        for (var i = 0; i < names.Count; i++)
        {
            sheetData.Add(new XElement(ns + "row", new XAttribute("r", i + 2),
                NumberCell($"A{i + 2}", i + 1), TextCell($"B{i + 2}", names[i])));
        }

        var worksheet = new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(ns + "worksheet", sheetData));

        var relNs = (XNamespace)RelNs;
        var workbook = new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(ns + "workbook",
                new XAttribute(XNamespace.Xmlns + "r", RelNs),
                new XElement(ns + "sheets",
                    new XElement(ns + "sheet",
                        new XAttribute("name", "名单"),
                        new XAttribute("sheetId", "1"),
                        new XAttribute(relNs + "id", "rId1")))));

        
        if (File.Exists(path)) File.Delete(path);

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        AddXml(zip, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "</Types>");

        AddXml(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>");

        AddDocument(zip, "xl/workbook.xml", workbook);

        AddXml(zip, "xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "</Relationships>");

        AddDocument(zip, "xl/worksheets/sheet1.xml", worksheet);
    }

    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private static XElement TextCell(string reference, string text)
    {
        var ns = (XNamespace)MainNs;
        return new XElement(ns + "c",
            new XAttribute("r", reference),
            new XAttribute("t", "inlineStr"),
            new XElement(ns + "is", new XElement(ns + "t", text)));
    }

    private static XElement NumberCell(string reference, int value)
    {
        var ns = (XNamespace)MainNs;
        return new XElement(ns + "c", new XAttribute("r", reference),
            new XElement(ns + "v", value));
    }

    private static void AddDocument(ZipArchive zip, string entryName, XDocument document)
    {
        using var stream = zip.CreateEntry(entryName).Open();
        document.Save(stream);
    }

    private static void AddXml(ZipArchive zip, string entryName, string xml)
    {
        using var stream = zip.CreateEntry(entryName).Open();
        
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(xml);
    }

    
    private static List<string> ReadText(string path)
    {
        var result = new List<string>();
        try
        {
            
            var text = File.ReadAllText(path, Encoding.UTF8);
            foreach (var line in text.Split('\n'))
            {
                
                foreach (var piece in line.Split(new[] { ',', '，', ';', '；', '\t', '、', '|' },
                                                 StringSplitOptions.RemoveEmptyEntries))
                {
                    var name = piece.Trim().Trim('"', '\uFEFF');
                    if (name.Length > 0) result.Add(name);
                }
            }
        }
        catch
        {
            
        }
        return result;
    }

    
    private static List<string> ReadXlsx(string path)
    {
        var result = new List<string>();
        try
        {
            using var zip = ZipFile.OpenRead(path);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

            var shared = ReadSharedStrings(zip, ns);

            var sheet = zip.Entries.FirstOrDefault(e =>
                e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) &&
                e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
            if (sheet is null) return result;

            
            var cells = new List<(int Column, string Text)>();
            using (var stream = sheet.Open())
            {
                var doc = XDocument.Load(stream);
                foreach (var c in doc.Descendants(ns + "c"))
                {
                    var text = CellText(c, ns, shared);
                    if (text.Length == 0) continue;
                    cells.Add((ColumnIndex((string?)c.Attribute("r")), text));
                }
            }

            if (cells.Count == 0) return result;

            
            
            
            
            
            
            var byColumn = cells.GroupBy(c => c.Column).ToList();

            var column = byColumn.FirstOrDefault(g =>
                HeaderWords.Contains(g.First().Text.Trim().ToLowerInvariant()));

            column ??= byColumn
                .OrderByDescending(g => g.Count(c => !IsNumeric(c.Text)))
                .ThenByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .First();

            result.AddRange(column.Select(c => c.Text));
        }
        catch
        {
            
        }
        return result;
    }

    private static List<string> ReadSharedStrings(ZipArchive zip, XNamespace ns)
    {
        var shared = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return shared;

        try
        {
            using var stream = entry.Open();
            var doc = XDocument.Load(stream);
            foreach (var si in doc.Root?.Elements(ns + "si") ?? Enumerable.Empty<XElement>())
            {
                
                shared.Add(string.Concat(si.Descendants(ns + "t").Select(t => t.Value)));
            }
        }
        catch
        {
            
        }
        return shared;
    }

    
    private static string CellText(XElement cell, XNamespace ns, List<string> shared)
    {
        var type = (string?)cell.Attribute("t");
        string text;

        if (type == "s")
        {
            var raw = cell.Element(ns + "v")?.Value;
            text = int.TryParse(raw, out var index) && index >= 0 && index < shared.Count ? shared[index] : "";
        }
        else if (type == "inlineStr")
        {
            text = string.Concat(cell.Descendants(ns + "t").Select(t => t.Value));
        }
        else
        {
            text = cell.Element(ns + "v")?.Value ?? "";
        }

        return text.Trim();
    }

    
    private static bool IsNumeric(string text)
        => double.TryParse(text, System.Globalization.NumberStyles.Any,
                           System.Globalization.CultureInfo.InvariantCulture, out _);

    
    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return 0;

        var index = 0;
        foreach (var ch in reference)
        {
            if (ch >= 'A' && ch <= 'Z') index = index * 26 + (ch - 'A' + 1);
            else if (ch >= 'a' && ch <= 'z') index = index * 26 + (ch - 'a' + 1);
            else break;   
        }
        return Math.Max(0, index - 1);
    }

    
    private static List<string> Clean(List<string> raw)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in raw)
        {
            var name = item.Trim();
            if (name.Length == 0 || name.Length > MaxNameLength) continue;

            
            
            if (!name.Any(char.IsLetterOrDigit)) continue;

            
            if (result.Count == 0 && HeaderWords.Contains(name.ToLowerInvariant())) continue;

            if (!seen.Add(name)) continue;

            result.Add(name);
            if (result.Count >= MaxNames) break;
        }

        return result;
    }
}
