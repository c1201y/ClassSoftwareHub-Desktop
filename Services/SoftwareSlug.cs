using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;















public static class SoftwareSlug
{
    
    public const int MaxLength = 48;

    
    public static string FromName(string? name)
    {
        var text = (name ?? "").Trim();
        if (text.Length == 0) return "";

        var sb = new StringBuilder(text.Length * 2);
        var pendingDash = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                AppendToken(sb, ref pendingDash, char.ToLowerInvariant(ch).ToString());
            }
            else if (PinyinTable.IsHan(ch))
            {
                
                var start = i;
                while (i + 1 < text.Length && PinyinTable.IsHan(text[i + 1])) i++;

                var pinyin = PinyinTable.Translate(text[start..(i + 1)]);
                if (pinyin.Length > 0) AppendToken(sb, ref pendingDash, pinyin);
                else pendingDash = sb.Length > 0;
            }
            else
            {
                
                pendingDash = sb.Length > 0;
            }
        }

        var id = sb.ToString().Trim('-');
        return id.Length > MaxLength ? Shorten(id) : id;
    }

    
    public static string EnsureUnique(string? id, IEnumerable<string> taken)
    {
        var baseId = (id ?? "").Trim().Trim('-');
        if (baseId.Length == 0) return "";

        var used = new HashSet<string>(taken.Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(baseId)) return baseId;

        for (var suffix = 2; suffix <= 99; suffix++)
        {
            var candidate = $"{baseId}-{suffix}";
            if (!used.Contains(candidate)) return candidate;
        }

        
        return $"{baseId}-{DateTime.Now:MMddHHmm}";
    }

    
    public static HashSet<string> TakenIds()
        => App.Content.Apps
            .Select(app => app.Id)
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    
    public static bool IsTaken(string? id)
    {
        var text = (id ?? "").Trim();
        return text.Length > 0 && TakenIds().Contains(text);
    }

    
    public static bool IsValid(string? id)
    {
        var text = (id ?? "").Trim();
        if (text.Length == 0 || text.Length > MaxLength) return false;
        if (text[0] == '-' || text[^1] == '-') return false;

        foreach (var ch in text)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') continue;
            return false;
        }

        return true;
    }

    private static void AppendToken(StringBuilder sb, ref bool pendingDash, string token)
    {
        if (pendingDash && sb.Length > 0) sb.Append('-');
        pendingDash = false;
        sb.Append(token);
    }

    
    private static string Shorten(string id)
    {
        var cut = id[..MaxLength];
        var dash = cut.LastIndexOf('-');
        if (dash > MaxLength / 2) cut = cut[..dash];
        return cut.Trim('-');
    }
}
