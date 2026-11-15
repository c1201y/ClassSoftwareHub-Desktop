using System;
using System.Collections.Generic;
using System.Linq;
using ClassSoftwareHub.Desktop.Data;

namespace ClassSoftwareHub.Desktop.Services;


public enum CheckStatus
{
    
    Installed,

    
    Outdated,

    
    Newer,

    
    Unknown,

    
    Missing,
}


public sealed record CheckItem(
    SoftwareApp App,
    CheckStatus Status,
    string InstalledName,
    string InstalledVersion,
    string Note);


public sealed record CheckSummary(int Total, int Installed, int Outdated, int Newer, int Unknown, int Missing);










public static class MachineCheck
{
    
    private readonly record struct Candidate(InstalledApp App, string Norm, bool Store);

    
    
    
    
    
    
    public static List<CheckItem> Run(
        IEnumerable<SoftwareApp> apps,
        IReadOnlyList<InstalledApp> installed,
        IReadOnlyList<InstalledApp>? storePackages = null)
    {
        
        
        
        
        
        var index = installed
            .Where(a => InstalledApps.Normalize(a.Name).Length > 0)
            .Select(a => new Candidate(a, InstalledApps.Normalize(a.Name), false))
            .ToList();

        
        if (storePackages is not null)
        {
            index.AddRange(storePackages
                .Where(a => InstalledApps.Normalize(a.Name).Length > 0)
                .Select(a => new Candidate(a, InstalledApps.Normalize(a.Name), true)));
        }

        var results = new List<CheckItem>();

        foreach (var app in apps)
        {
            var norm = InstalledApps.Normalize(app.Name);
            var hit = norm.Length > 0 ? BestMatch(norm, index) : null;

            
            
            
            
            if (hit is null && norm.Length > 0)
                hit = BestAliasMatch(app.Name, norm, index);

            if (hit is null)
            {
                var missingNote = !string.IsNullOrWhiteSpace(app.Store)
                    ? "清单标记为商店版；本机安装记录与本账户商店应用均未匹配到。商店版安装在其它账户下时无法查询，可在「Microsoft Store → 库」中确认"
                    : Weight(norm) < 4
                        ? "软件名称过短，无法可靠匹配，请在「程序和功能」中人工核对"
                        : "安装记录与商店应用中均未匹配到（免安装版、已更名的软件不在这两处记录中）";
                results.Add(new CheckItem(app, CheckStatus.Missing, "", "", missingNote));
                continue;
            }

            var installedVersion = hit.Value.App.Version;
            var (status, note) = Judge(app.Version, installedVersion, hit.Value.App.Name);
            if (hit.Value.Store) note += "（匹配到的是 Microsoft Store 应用）";
            results.Add(new CheckItem(app, status, hit.Value.App.Name, installedVersion, note));
        }

        
        return results
            .OrderBy(r => Rank(r.Status))
            .ThenBy(r => r.App.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    private static int Rank(CheckStatus s) => s switch
    {
        CheckStatus.Outdated => 0,   
        CheckStatus.Missing => 1,
        CheckStatus.Unknown => 2,
        CheckStatus.Installed => 3,
        CheckStatus.Newer => 4,
        _ => 5,
    };

    
    private static Candidate? BestMatch(string norm, List<Candidate> index)
    {
        Candidate? best = null;
        var bestScore = 0d;

        foreach (var candidate in index)
        {
            var normInstalled = candidate.Norm;
            double score;

            if (normInstalled == norm)
            {
                score = 100;   
            }
            else if (normInstalled.Contains(norm, StringComparison.Ordinal) ||
                     norm.Contains(normInstalled, StringComparison.Ordinal))
            {
                
                
                var (shorter, longer) = norm.Length <= normInstalled.Length
                    ? (norm, normInstalled)
                    : (normInstalled, norm);
                var shortWeight = Weight(shorter);
                if (shortWeight < 4) continue;
                var ratio = shortWeight / (double)Weight(longer);
                if (ratio < 0.6) continue;
                score = ratio;
            }
            else
            {
                continue;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    
    
    
    
    
    
    private static Candidate? BestAliasMatch(string rawName, string norm, List<Candidate> index)
    {
        foreach (var segment in rawName.Split(AliasSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var alias = InstalledApps.Normalize(segment);
            if (alias.Length == 0 || alias == norm) continue;
            if (Weight(alias) < 4) continue;

            foreach (var candidate in index)
                if (candidate.Norm == alias) return candidate;
        }

        return null;
    }

    private static readonly char[] AliasSeparators = { ' ', '\u3000', '·', '・', '/', '｜', '|', '-' };

    
    
    
    
    
    private static int Weight(string s)
    {
        var w = 0;
        foreach (var ch in s) w += IsCjk(ch) ? 2 : 1;
        return w;
    }

    private static bool IsCjk(char c) =>
        c is >= '\u3040' and <= '\u30FF'   
          or >= '\u3400' and <= '\u4DBF'   
          or >= '\u4E00' and <= '\u9FFF'   
          or >= '\uAC00' and <= '\uD7AF'   
          or >= '\uF900' and <= '\uFAFF';  

    private static (CheckStatus, string) Judge(string catalogVersion, string installedVersion, string installedName)
    {
        var shown = string.IsNullOrWhiteSpace(installedVersion) ? "未记录" : installedVersion;

        if (string.IsNullOrWhiteSpace(installedVersion))
            return (CheckStatus.Unknown, $"已安装（{installedName}），但安装记录中缺少版本号，无法比对");

        var c = ParseNumeric(catalogVersion);
        var i = ParseNumeric(installedVersion);

        if (c is null || i is null)
            return (CheckStatus.Unknown, $"已安装（{installedName} {shown}），清单版本号非数字格式，无法比对");

        var cmp = CompareNumeric(i, c);
        return cmp switch
        {
            0 => (CheckStatus.Installed, $"版本一致（{shown}）"),
            < 0 => (CheckStatus.Outdated, $"本机 {shown}，低于清单版本 {catalogVersion}，建议升级"),
            _ => (CheckStatus.Newer, $"本机 {shown}，高于清单版本 {catalogVersion}"),
        };
    }

    
    private static int[]? ParseNumeric(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;

        var v = version.Trim();
        if (v.StartsWith('v') || v.StartsWith('V')) v = v[1..];

        var parts = v.Split('.');
        var nums = new int[parts.Length];
        for (var k = 0; k < parts.Length; k++)
        {
            if (parts[k].Length == 0) return null;
            foreach (var ch in parts[k])
                if (!char.IsDigit(ch)) return null;
            if (!int.TryParse(parts[k], out nums[k])) return null;
        }
        return nums.Length == 0 ? null : nums;
    }

    private static int CompareNumeric(int[] a, int[] b)
    {
        for (var k = 0; k < Math.Max(a.Length, b.Length); k++)
        {
            var x = k < a.Length ? a[k] : 0;
            var y = k < b.Length ? b[k] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    public static CheckSummary Summarize(IEnumerable<CheckItem> items)
    {
        var list = items as IList<CheckItem> ?? items.ToList();
        return new CheckSummary(
            list.Count,
            list.Count(x => x.Status is CheckStatus.Installed or CheckStatus.Newer),
            list.Count(x => x.Status == CheckStatus.Outdated),
            list.Count(x => x.Status == CheckStatus.Newer),
            list.Count(x => x.Status == CheckStatus.Unknown),
            list.Count(x => x.Status == CheckStatus.Missing));
    }
}
