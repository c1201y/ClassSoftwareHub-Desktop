using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ClassSoftwareHub.Desktop.Services.Docs;













internal static class DocInline
{
    
    private static readonly Regex BareUrl = new(
        @"(?<url>(?:https?://|csh://|ms-settings:)[^\s<>""'`]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string TrailingPunctuation = ".,;:!?，。；：！？、）】》」』”’*_~";

    
    
    public static List<DocRun> Parse(
        string? text, string? link = null, bool bold = false, bool italic = false, bool strike = false)
    {
        var runs = new List<DocRun>();
        var plain = new StringBuilder();
        var source = text ?? "";

        void Flush()
        {
            if (plain.Length == 0) return;
            AppendPlain(runs, plain.ToString(), link, bold, italic, strike);
            plain.Clear();
        }

        var i = 0;
        while (i < source.Length)
        {
            var ch = source[i];

            
            if (ch == '\\' && i + 1 < source.Length && IsEscapable(source[i + 1]))
            {
                plain.Append(source[i + 1]);
                i += 2;
                continue;
            }

            
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

            
            if (ch == '!' && i + 1 < source.Length && source[i + 1] == '['
                && TryReadLink(source, i + 1, out var alt, out _, out var afterImage))
            {
                Flush();
                runs.Add(new DocRun(alt.Length > 0 ? alt : "图片", link, bold, italic));
                i = afterImage;
                continue;
            }

            
            if (ch == '[' && TryReadLink(source, i, out var label, out var url, out var afterLink))
            {
                Flush();
                runs.AddRange(Parse(label, url, bold, italic, strike));
                i = afterLink;
                continue;
            }

            
            if (ch == '~' && i + 1 < source.Length && source[i + 1] == '~')
            {
                var end = source.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    Flush();
                    runs.AddRange(Parse(source[(i + 2)..end], link, bold, italic, true));
                    i = end + 2;
                    continue;
                }
            }

            
            if ((ch == '*' || ch == '_') && i + 1 < source.Length && source[i + 1] == ch)
            {
                var marker = new string(ch, 2);
                var end = source.IndexOf(marker, i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    Flush();
                    runs.AddRange(Parse(source[(i + 2)..end], link, true, italic, strike));
                    i = end + 2;
                    continue;
                }
            }

            
            if (ch == '*' || ch == '_')
            {
                var isWordUnderscore = ch == '_' && i > 0 && char.IsLetterOrDigit(source[i - 1]);
                var end = isWordUnderscore ? -1 : source.IndexOf(ch, i + 1);
                if (end > i + 1 && !char.IsWhiteSpace(source[i + 1]) && !char.IsWhiteSpace(source[end - 1]))
                {
                    Flush();
                    runs.AddRange(Parse(source[(i + 1)..end], link, bold, true, strike));
                    i = end + 1;
                    continue;
                }
            }

            
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

    
    private static void AppendPlain(
        List<DocRun> runs, string text, string? link, bool bold, bool italic, bool strike = false)
    {
        if (text.Length == 0) return;

        if (link is not null)
        {
            runs.Add(new DocRun(text, link, bold, italic, Strike: strike));
            return;
        }

        var cursor = 0;
        foreach (Match match in BareUrl.Matches(text))
        {
            var url = match.Value.TrimEnd(TrailingPunctuation.ToCharArray());
            if (url.Length == 0) continue;

            var start = match.Index;
            if (start > cursor)
                runs.Add(new DocRun(text[cursor..start], null, bold, italic, Strike: strike));
            runs.Add(new DocRun(url, url, bold, italic, Strike: strike));
            cursor = start + url.Length;
        }

        if (cursor < text.Length)
            runs.Add(new DocRun(text[cursor..], null, bold, italic, Strike: strike));
    }

    
    
    
    
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

        
        var space = url.IndexOf(' ');
        if (space > 0 && !url.StartsWith('<')) url = url[..space];
        if (url.StartsWith('<') && url.EndsWith('>')) url = url[1..^1];

        
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
