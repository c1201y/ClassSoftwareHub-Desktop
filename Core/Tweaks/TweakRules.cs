using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ClassSoftwareHub.Desktop.Core;













public static class TweakRules
{
    
    
    
    
    
    
    
    
    
    
    
    
    
    public static int? ParseDword(object? raw) => raw switch
    {
        int i => i,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        string s when int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            => parsed,
        _ => null,
    };

    
    
    
    
    
    
    
    
    
    public static bool IsApplicable(TweakDef def, int build)
    {
        if (build <= 0) return true;

        if (def.MinBuild > 0 && build < def.MinBuild) return false;
        if (def.MaxBuild > 0 && build > def.MaxBuild) return false;
        return true;
    }

    
    
    
    
    
    
    
    
    
    
    
    
    public static string BuildRegText(IReadOnlyList<(TweakDef Def, int Value)> changes)
    {
        var text = new StringBuilder();
        text.AppendLine("Windows Registry Editor Version 5.00");
        text.AppendLine();

        foreach (var section in changes.GroupBy(c => (c.Def.RootName, c.Def.KeyPath)))
        {
            text.AppendLine($"[{section.Key.RootName}\\{section.Key.KeyPath}]");

            foreach (var (def, value) in section)
            {
                var name = EscapeValueName(def.ValueName);

                
                if (def.DeleteWhenOff && value == def.OffValue)
                {
                    text.AppendLine($"\"{name}\"=-");
                    continue;
                }

                text.AppendLine($"\"{name}\"=dword:{value:x8}");
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    
    
    
    
    
    private static string EscapeValueName(string name) =>
        name.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    
    
    
    
    
    
    
    
    
    
    
    
    public static List<TweakRefresh> RefreshesFor(IReadOnlyList<(TweakDef Def, int Value)> changes)
    {
        var result = new List<TweakRefresh>();

        if (changes.Any(c => c.Def.Refresh == TweakRefresh.Tray)) result.Add(TweakRefresh.Tray);
        if (changes.Any(c => c.Def.Refresh == TweakRefresh.Desktop)) result.Add(TweakRefresh.Desktop);

        return result;
    }

    
    
    
    
    
    
    
    
    
    
    
    public static Dictionary<string, int> RemainingAfterApply(
        IReadOnlyList<(TweakDef Def, int Value)> changes,
        IReadOnlyList<string> failed)
    {
        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (def, value) in changes)
            if (failed.Contains(def.Id))
                remaining[def.Id] = value;   

        return remaining;
    }
}
