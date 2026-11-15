using System;
using System.Collections.Generic;
using System.Text;

namespace ClassSoftwareHub.Desktop.Core.Dictionary;


public static class DictSources
{
    
    public const string Offline = "offline";

    
    public const string Youdao = "youdao";

    
    public const string FreeDictionary = "freedict";
}


public static class DictSourceModes
{
    
    public const string OfflineFirst = "offline-first";

    
    public const string OnlineFirst = "online-first";

    
    public const string OfflineOnly = "offline-only";

    
    public const string OnlineOnly = "online-only";

    public static bool IsValid(string? mode) =>
        mode is OfflineFirst or OnlineFirst or OfflineOnly or OnlineOnly;

    
    public static string Normalize(string? mode) => IsValid(mode) ? mode! : OfflineFirst;

    public static bool UsesOnline(string mode) => mode is OnlineFirst or OnlineOnly;

    public static bool UsesOffline(string mode) => mode is OfflineFirst or OfflineOnly;
}
















public static class LexiconFormat
{
    
    
    
    
    public static DictEntry? ParseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        if (line[0] == '#') return null;

        var parts = line.Split('\t');
        if (parts.Length < 2) return null;

        var word = parts[0].Trim();
        if (word.Length == 0) return null;

        var phonetic = parts[1].Trim();
        if (phonetic.Length > 0 && !phonetic.StartsWith('/')) phonetic = "/" + phonetic + "/";

        var senses = new List<DictSense>(parts.Length - 2);
        for (var i = 2; i < parts.Length; i++)
        {
            var raw = parts[i].Trim();
            if (raw.Length == 0) continue;
            senses.Add(SplitPos(raw));
        }

        if (senses.Count == 0) return null;

        return new DictEntry
        {
            Word = word,
            Phonetic = phonetic,
            Source = DictSources.Offline,
            Senses = senses
        };
    }

    
    
    
    
    
    
    
    
    public static DictSense SplitPos(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return new DictSense();

        var dot = trimmed.IndexOf(". ", StringComparison.Ordinal);
        if (dot is > 0 and <= 8)
        {
            var pos = trimmed[..(dot + 1)];
            if (IsPosToken(pos))
            {
                var body = trimmed[(dot + 2)..].Trim();
                if (body.Length > 0) return new DictSense { Pos = pos, Text = body };
            }
        }

        return new DictSense { Text = trimmed };
    }

    
    private static bool IsPosToken(string token)
    {
        if (token.Length is < 2 or > 6) return false;
        if (token[^1] != '.') return false;

        for (var i = 0; i < token.Length - 1; i++)
        {
            var c = token[i];
            if (c is < 'a' or > 'z') return false;
        }

        return true;
    }

    
    public static string NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "";
        return query.Trim().ToLowerInvariant();
    }

    
    
    
    
    
    
    
    
    public static bool HasChinese(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        foreach (var c in text)
        {
            if (c is >= '\u4E00' and <= '\u9FFF') return true;
            if (c is >= '\u3400' and <= '\u4DBF') return true;
        }

        return false;
    }

    
    public static string BuildLine(DictEntry entry)
    {
        var sb = new StringBuilder();
        sb.Append(entry.Word).Append('\t');
        sb.Append(entry.Phonetic.Trim('/')).Append('\t');
        for (var i = 0; i < entry.Senses.Count; i++)
        {
            if (i > 0) sb.Append('\t');
            var s = entry.Senses[i];
            if (s.Pos.Length > 0) sb.Append(s.Pos).Append(' ');
            sb.Append(s.Text);
        }

        return sb.ToString();
    }
}
