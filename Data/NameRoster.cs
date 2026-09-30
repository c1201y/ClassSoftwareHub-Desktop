using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace ClassSoftwareHub.Desktop.Data;

/// <summary>
/// 名单读取（2026-09-27 Nick 要的「导入名字」）。
/// 支持 .txt / .csv / .tsv（纯文本一堆名字）与 .xlsx / .xlsm（Excel）。
///
/// 为什么自己解 xlsx：项目一直不带第三方包（见 MEMORY「音频层手写 Core Audio」的先例），
/// 而 xlsx 本身就是个 zip + 几个 XML —— 只需要读出"格子里写了什么字"，
/// 用 ZipArchive + XDocument 就够，不值得为它引一整个 Excel 库。
/// </summary>
public static class NameRoster
{
    /// <summary>文件选择器用的扩展名（与 <see cref="Read"/> 的分派保持一致）。</summary>
    public static readonly string[] TextExts = { ".txt", ".csv", ".tsv" };
    public static readonly string[] ExcelExts = { ".xlsx", ".xlsm" };

    /// <summary>一个人最多这么长 —— 超过基本是表格里的说明文字，不是名字。</summary>
    private const int MaxNameLength = 40;

    /// <summary>名单上限。</summary>
    public const int MaxNames = 2000;

    private static readonly string[] HeaderWords =
    {
        "姓名", "名字", "学生", "学生姓名", "名单", "人员", "成员", "name", "student", "名字名单",
    };

    /// <summary>按扩展名读一个文件，返回**去重且保序**的名字列表；读不出来就返回空表。</summary>
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

    /// <summary>是不是本工具认的文件。</summary>
    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return TextExts.Contains(ext) || ExcelExts.Contains(ext);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  写出去（2026-09-29 Nick：名单要能导出成 Excel / txt）
    //  ⚠️ 这个类刻意不引用任何 UI 类型（RosterEntry 放在工具页里），
    //     这样它还能被一个纯控制台工程直接链接起来做离线往返测试。
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 写成一个纯文本名单：一行一个名字。
    /// UTF-8 **带 BOM** + CRLF —— BOM 是为了记事本 / Excel 打开中文不乱码，CRLF 是 Windows 记事本的换行习惯
    /// （和 Assets/roster 里那份示例 txt 保持一致）。
    /// </summary>
    public static void WriteText(string path, IEnumerable<string> names)
    {
        var sb = new StringBuilder();
        foreach (var name in names) sb.Append(name).Append("\r\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>
    /// 写成一个最小可用的 .xlsx（「序号 | 姓名」两列）。
    ///
    /// 跟读取同一个思路：不引第三方库。xlsx 就是 zip + 几个 XML，这里只写"必须的那几个部件"：
    ///   [Content_Types].xml · _rels/.rels · xl/workbook.xml · xl/_rels/workbook.xml.rels · xl/worksheets/sheet1.xml
    /// 单元格用 <c>t="inlineStr"</c>（内联字符串），正好是 <see cref="CellText"/> 认的形式 ——
    /// **导出的文件必须能被 <see cref="Read"/> 原样读回来**，这是硬要求（否则"导出→再导入"就丢人）。
    /// </summary>
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

        // picker 已经替我们建了一个空文件 —— 直接 Create 会因"文件已存在"抛异常
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
        // 不带 BOM：OOXML 的部件都按 XML 声明走，BOM 反而让某些读取器挑刺
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(xml);
    }

    // ── 纯文本：一行一个，行内再用常见分隔符切开 ──────────────────
    private static List<string> ReadText(string path)
    {
        var result = new List<string>();
        try
        {
            // 中文 txt 在老机器上常是 GBK，让 .NET 自己嗅探 BOM；嗅不出来按 UTF-8 读
            var text = File.ReadAllText(path, Encoding.UTF8);
            foreach (var line in text.Split('\n'))
            {
                // 不按空格切：英文名 "John Smith" 会被切碎
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
            // 交给外层当"一个名字都没读到"处理
        }
        return result;
    }

    // ── Excel：读第一个工作表的"内容最多的那一列" ────────────────
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

            // 列号 + 文本，按文档顺序（即行序）收集
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

            // 名单几乎总是一整列，但**不能只按"格子多"挑** ——
            // 2026-09-29 实测（Nick 的「序号 | 姓名」示例表）：两列一样满，"取最左"直接选中了**序号列**，
            // 抽出来是 1、2、3… 一个名字都没有。挑列的优先级改成：
            //   ① 表头写着「姓名 / 名字 / 学生…」的那一列 —— 用户已经把答案写在那儿了
            //   ② 非数字内容最多的列（序号列基本都是数字，天然被排到后面）
            //   ③ 格子多的；仍并列取最左
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
            // 文件损坏 / 不是真正的 xlsx —— 外层会提示"没读到名字"
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
                // 一个 si 里的文字可能被拆成多个富文本 run，拼起来才是完整名字
                shared.Add(string.Concat(si.Descendants(ns + "t").Select(t => t.Value)));
            }
        }
        catch
        {
            // 共享字符串表读不出来：后面按"没有这张表"继续（普通值仍能读）
        }
        return shared;
    }

    /// <summary>取单元格的文本：共享字符串 / 内联字符串 / 公式结果 / 普通值。</summary>
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

    /// <summary>这一格是不是纯数字（序号列、学号列都是）—— 用来把"序号"列从候选里降权。</summary>
    private static bool IsNumeric(string text)
        => double.TryParse(text, System.Globalization.NumberStyles.Any,
                           System.Globalization.CultureInfo.InvariantCulture, out _);

    /// <summary>"B7" → 1（A=0）。</summary>
    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return 0;

        var index = 0;
        foreach (var ch in reference)
        {
            if (ch >= 'A' && ch <= 'Z') index = index * 26 + (ch - 'A' + 1);
            else if (ch >= 'a' && ch <= 'z') index = index * 26 + (ch - 'a' + 1);
            else break;   // 碰到数字（行号）就停
        }
        return Math.Max(0, index - 1);
    }

    /// <summary>去空、去重（保序）、砍掉明显不是名字的长文本、跳过表头、卡上限。</summary>
    private static List<string> Clean(List<string> raw)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in raw)
        {
            var name = item.Trim();
            if (name.Length == 0 || name.Length > MaxNameLength) continue;

            // 连一个字母 / 数字都没有的条目（"…"、"---"、"——"）是"下面还有更多"的示意标记，不是名字。
            // ⚠️ 只排除纯符号 —— **纯数字要留着**，有人就是拿一列学号当名单抽号的（2026-09-29）。
            if (!name.Any(char.IsLetterOrDigit)) continue;

            // 只跳过第一个 —— 表格第一行是表头很常见，但班里真有个叫"名单"的同学也不该被吞掉
            if (result.Count == 0 && HeaderWords.Contains(name.ToLowerInvariant())) continue;

            if (!seen.Add(name)) continue;

            result.Add(name);
            if (result.Count >= MaxNames) break;
        }

        return result;
    }
}
