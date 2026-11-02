using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ClassSoftwareHub.Desktop.Services.Docs;

/// <summary>
/// 行内解析：<c>**粗体**</c>、<c>*斜体*</c>、`` `代码` ``、<c>[文字](链接)</c>、<c>&lt;链接&gt;</c>，
/// 以及"裸链接自动变成链接"（<c>https://</c> / <c>csh://</c> / <c>ms-settings:</c>）。
///
/// <para>
/// 解析结果是 <see cref="DocRun"/> 列表 —— 文案与样式分开，渲染器按样式拼 Hyperlink / Run。
/// </para>
/// <para>
/// ⛔ 不追求完整的 CommonMark：教程文章用不到嵌套引用链接、引用式链接、脚注这些。
/// 遇到不认识的行内标记按原文显示，宁可不美化也不能把字吞掉。
/// </para>
/// </summary>
internal static class DocInline
{
    /// <summary>裸链接。<paramref name="Value"/> 右侧的标点稍后会被剥掉。</summary>
    private static readonly Regex BareUrl = new(
        @"(?<url>(?:https?://|csh://|ms-settings:)[^\s<>""'`]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string TrailingPunctuation = ".,;:!?，。；：！？、）】》」』”’*_~";

    /// <summary>把一段行内文本拆成 run。</summary>
    /// <param name="link">外层链接（递归时带上，段落里的子文本都继承它）。</param>
    public static List<DocRun> Parse(string? text, string? link = null, bool bold = false, bool italic = false)
    {
        var runs = new List<DocRun>();
        var plain = new StringBuilder();
        var source = text ?? "";

        void Flush()
        {
            if (plain.Length == 0) return;
            AppendPlain(runs, plain.ToString(), link, bold, italic);
            plain.Clear();
        }

        var i = 0;
        while (i < source.Length)
        {
            var ch = source[i];

            // 转义：\* \_ \` \[ 之类按字面输出
            if (ch == '\\' && i + 1 < source.Length && IsEscapable(source[i + 1]))
            {
                plain.Append(source[i + 1]);
                i += 2;
                continue;
            }

            // 行内代码：反引号里一律按字面，不再解析其他标记
            if (ch == '`')
            {
                var end = source.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    Flush();
                    runs.Add(new DocRun(source[(i + 1)..end], link, bold, italic, Code: true));
                    i = end + 1;
                    continue;
                }
            }

            // 图片（行内出现时退化成链接文字，真正的图片走块级）
            if (ch == '!' && i + 1 < source.Length && source[i + 1] == '['
                && TryReadLink(source, i + 1, out var alt, out _, out var afterImage))
            {
                Flush();
                runs.Add(new DocRun(alt.Length > 0 ? alt : "图片", link, bold, italic));
                i = afterImage;
                continue;
            }

            // 链接：[文字](地址)
            if (ch == '[' && TryReadLink(source, i, out var label, out var url, out var afterLink))
            {
                Flush();
                runs.AddRange(Parse(label, url, bold, italic));
                i = afterLink;
                continue;
            }

            // 加粗：**…** / __…__
            if ((ch == '*' || ch == '_') && i + 1 < source.Length && source[i + 1] == ch)
            {
                var marker = new string(ch, 2);
                var end = source.IndexOf(marker, i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    Flush();
                    runs.AddRange(Parse(source[(i + 2)..end], link, true, italic));
                    i = end + 2;
                    continue;
                }
            }

            // 斜体：*…* / _…_（下划线夹在单词中间不算，避免 snake_case 被吃掉）
            if (ch == '*' || ch == '_')
            {
                var isWordUnderscore = ch == '_' && i > 0 && char.IsLetterOrDigit(source[i - 1]);
                var end = isWordUnderscore ? -1 : source.IndexOf(ch, i + 1);
                if (end > i + 1 && !char.IsWhiteSpace(source[i + 1]) && !char.IsWhiteSpace(source[end - 1]))
                {
                    Flush();
                    runs.AddRange(Parse(source[(i + 1)..end], link, bold, true));
                    i = end + 1;
                    continue;
                }
            }

            // 自动链接：<https://…>
            if (ch == '<')
            {
                var end = source.IndexOf('>', i + 1);
                if (end > i + 1)
                {
                    var inner = source[(i + 1)..end].Trim();
                    if (IsUriLike(inner))
                    {
                        Flush();
                        runs.Add(new DocRun(inner, inner, bold, italic));
                        i = end + 1;
                        continue;
                    }
                }
            }

            plain.Append(ch);
            i++;
        }

        Flush();
        return runs;
    }

    /// <summary>裸链接识别（txt 和 Markdown 里的普通文字都走它）。</summary>
    private static void AppendPlain(
        List<DocRun> runs, string text, string? link, bool bold, bool italic)
    {
        if (text.Length == 0) return;

        if (link is not null)
        {
            runs.Add(new DocRun(text, link, bold, italic));
            return;
        }

        var cursor = 0;
        foreach (Match match in BareUrl.Matches(text))
        {
            var url = match.Value.TrimEnd(TrailingPunctuation.ToCharArray());
            if (url.Length == 0) continue;

            var start = match.Index;
            if (start > cursor) runs.Add(new DocRun(text[cursor..start], null, bold, italic));
            runs.Add(new DocRun(url, url, bold, italic));
            cursor = start + url.Length;
        }

        if (cursor < text.Length) runs.Add(new DocRun(text[cursor..], null, bold, italic));
    }

    /// <summary>
    /// 从 <paramref name="start"/>（必须是 <c>[</c>）读一个 <c>[文字](地址)</c>。
    /// 读不到返回 false，调用方按普通字符处理。
    /// </summary>
    public static bool TryReadLink(
        string text, int start, out string label, out string url, out int next)
    {
        label = "";
        url = "";
        next = start;

        if (start < 0 || start >= text.Length || text[start] != '[') return false;

        var close = FindClosing(text, start + 1, '[', ']');
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(') return false;

        var paren = FindClosing(text, close + 2, '(', ')');
        if (paren < 0) return false;

        label = text[(start + 1)..close];
        url = text[(close + 2)..paren].Trim();

        // [文字](地址 "标题") —— 标题部分丢掉
        var space = url.IndexOf(' ');
        if (space > 0 && !url.StartsWith('<')) url = url[..space];
        if (url.StartsWith('<') && url.EndsWith('>')) url = url[1..^1];

        // 地址里常见的转义写法
        url = url.Replace("\\_", "_");

        next = paren + 1;
        return true;
    }

    private static int FindClosing(string text, int start, char open, char close)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == open) depth++;
            else if (text[i] == close)
            {
                if (depth == 0) return i;
                depth--;
            }
        }

        return -1;
    }

    public static bool IsUriLike(string text) =>
        text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("csh://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

    private static bool IsEscapable(char ch) =>
        ch is '\\' or '*' or '_' or '`' or '[' or ']' or '(' or ')' or '#' or '!' or '<' or '>';
}
