using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassSoftwareHub.Desktop.Core;


public enum PageFileMode
{
    
    None,

    
    SystemManaged,

    
    Custom,
}


public sealed record PageFilePlan(string Drive, PageFileMode Mode, int MinMb = 0, int MaxMb = 0)
{
    
    public string SizeText => Mode switch
    {
        PageFileMode.None => "无",
        PageFileMode.SystemManaged => "系统管理",
        _ => $"{MinMb} - {MaxMb}",
    };
}


























public static class PageFileRules
{
    
    public const int MinAllowedMb = 16;

    
    public const string AutomaticEntry = @"?:\pagefile.sys 0 0";

    
    public static string NormalizeDrive(string? drive)
    {
        var text = (drive ?? "").Trim();
        if (text.Length == 0) return "";

        var letter = char.ToUpperInvariant(text[0]);
        if (letter is < 'A' or > 'Z') return "";
        return $"{letter}:";
    }

    
    
    
    
    
    public static PageFilePlan? ParseEntry(string? entry, string systemDrive)
    {
        var text = (entry ?? "").Trim();
        if (text.Length == 0) return null;

        var bits = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (bits.Length == 0) return null;

        var path = bits[0];
        if (path.Length < 3 || path[1] != ':') return null;

        
        var drive = path[0] == '?' ? NormalizeDrive(systemDrive) : NormalizeDrive(path[..2]);
        if (drive.Length == 0) return null;

        var min = bits.Length > 1 && int.TryParse(bits[1], out var a) ? a : 0;
        var max = bits.Length > 2 && int.TryParse(bits[2], out var b) ? b : 0;

        
        if (min < 0) min = 0;
        if (max < 0) max = 0;

        return min == 0 && max == 0
            ? new PageFilePlan(drive, PageFileMode.SystemManaged)
            : new PageFilePlan(drive, PageFileMode.Custom, min, max);
    }

    
    public static List<PageFilePlan> Parse(IReadOnlyList<string>? entries, string systemDrive)
    {
        var list = new List<PageFilePlan>();
        if (entries is null) return list;

        foreach (var entry in entries)
        {
            var plan = ParseEntry(entry, systemDrive);
            if (plan is null) continue;
            if (list.Any(p => p.Drive == plan.Drive)) continue;
            list.Add(plan);
        }

        return list;
    }

    
    
    
    
    
    
    
    public static bool IsAutomatic(IReadOnlyList<string>? entries)
    {
        if (entries is null || entries.Count != 1) return false;

        var text = (entries[0] ?? "").Trim();
        if (text.Length == 0) return false;
        if (text[0] != '?') return false;

        var plan = ParseEntry(text, "C:");
        return plan is { Mode: PageFileMode.SystemManaged };
    }

    
    
    
    
    
    
    
    public static List<string> BuildEntries(IReadOnlyList<PageFilePlan> plans, string systemDrive)
    {
        var sys = NormalizeDrive(systemDrive);

        var ordered = plans
            .Where(p => p.Mode != PageFileMode.None)
            .Select(p => new { Drive = NormalizeDrive(p.Drive), Plan = p })
            .Where(x => x.Drive.Length > 0)
            .GroupBy(x => x.Drive)
            .Select(g => g.First().Plan)                  
            .OrderBy(p => NormalizeDrive(p.Drive) == sys ? 0 : 1)
            .ThenBy(p => NormalizeDrive(p.Drive), StringComparer.Ordinal)
            .ToList();

        var entries = new List<string>();

        foreach (var plan in ordered)
        {
            var drive = NormalizeDrive(plan.Drive);

            entries.Add(plan.Mode == PageFileMode.SystemManaged
                ? $@"{drive}\pagefile.sys 0 0"
                : $@"{drive}\pagefile.sys {plan.MinMb} {plan.MaxMb}");
        }

        return entries;
    }

    
    
    
    
    
    
    
    public static string? Validate(PageFilePlan plan, int freeMb)
    {
        if (plan.Mode != PageFileMode.Custom) return null;

        if (plan.MinMb < MinAllowedMb)
            return $"初始大小不能小于 {MinAllowedMb} MB。";

        if (plan.MaxMb < MinAllowedMb)
            return $"最大值不能小于 {MinAllowedMb} MB。";

        if (plan.MaxMb < plan.MinMb)
            return "最大值不能小于初始大小。";

        if (freeMb > 0 && plan.MaxMb > freeMb)
            return $"最大值超过该盘的可用空间（{freeMb} MB）。";

        return null;
    }

    
    
    
    
    
    
    
    public static string? ValidateAll(IReadOnlyList<PageFilePlan> plans)
    {
        if (plans.All(p => p.Mode == PageFileMode.None))
            return "至少要保留一个驱动器的分页文件。";

        return null;
    }

    
    
    
    
    
    
    public static bool SameAs(IReadOnlyList<PageFilePlan> a, IReadOnlyList<PageFilePlan> b)
    {
        var left = a.Where(p => p.Mode != PageFileMode.None)
                    .ToDictionary(p => NormalizeDrive(p.Drive), p => p, StringComparer.Ordinal);

        var right = b.Where(p => p.Mode != PageFileMode.None)
                     .ToDictionary(p => NormalizeDrive(p.Drive), p => p, StringComparer.Ordinal);

        if (left.Count != right.Count) return false;

        foreach (var (drive, plan) in left)
            if (!right.TryGetValue(drive, out var other)) return false;
            else if (plan.Mode != other.Mode) return false;
            else if (plan.Mode == PageFileMode.Custom &&
                     (plan.MinMb != other.MinMb || plan.MaxMb != other.MaxMb)) return false;

        return true;
    }
}
