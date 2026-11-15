using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClassSoftwareHub.Desktop.Services.Updating;


public enum UpdateChannel
{
    
    Stable,

    
    Insider,
}


public static class UpdateChannels
{
    public const string Stable = "stable";
    public const string Insider = "insider";

    public static UpdateChannel Parse(string? value) =>
        string.Equals(value, Insider, StringComparison.OrdinalIgnoreCase) ? UpdateChannel.Insider : UpdateChannel.Stable;

    public static string ToText(UpdateChannel channel) => channel == UpdateChannel.Insider ? Insider : Stable;

    
    public static string ToDisplay(UpdateChannel channel) => channel == UpdateChannel.Insider ? "Insider" : "正式版";
}


public sealed record UpdatePackage(
    string Name,
    Uri Url,
    long Size,
    string Sha256,
    string Md5)
{
    public string SizeText => Size <= 0 ? "" :
        Size >= 1024L * 1024 * 1024 ? (Size / 1024d / 1024 / 1024).ToString("0.##") + " GB" :
        Size >= 1024L * 1024 ? (Size / 1024d / 1024).ToString("0.#") + " MB" :
        (Size / 1024d).ToString("0.#") + " KB";
}


public sealed record UpdateRelease(
    string Tag,
    string Version,
    UpdateChannel Channel,
    bool Prerelease,
    string Notes,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<UpdatePackage> Packages)
{
    
    public UpdatePackage? Primary =>
        Packages.Count == 0 ? null : Packages[0];
}


public sealed record UpdateCheckResult(
    bool HasUpdate,
    UpdateRelease? Release,
    string Message)
{
    public static UpdateCheckResult None(string message) => new(false, null, message);
}







public static class VersionCompare
{
    public static int Compare(string? a, string? b)
    {
        var (na, pa) = Split(a);
        var (nb, pb) = Split(b);

        var len = Math.Max(na.Count, nb.Count);
        for (var i = 0; i < len; i++)
        {
            var x = i < na.Count ? na[i] : 0;
            var y = i < nb.Count ? nb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }

        if (pa.Length == 0 && pb.Length == 0) return 0;
        if (pa.Length == 0) return 1;    
        if (pb.Length == 0) return -1;   

        var sa = pa.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var sb = pb.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var slen = Math.Max(sa.Length, sb.Length);
        for (var i = 0; i < slen; i++)
        {
            if (i >= sa.Length) return -1;
            if (i >= sb.Length) return 1;
            var numA = long.TryParse(sa[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var va);
            var numB = long.TryParse(sb[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var vb);
            int c;
            if (numA && numB) c = va.CompareTo(vb);
            else if (numA) c = -1;                 
            else if (numB) c = 1;
            else c = string.Compare(sa[i], sb[i], StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return 0;
    }

    public static bool IsNewer(string? candidate, string? current) => Compare(candidate, current) > 0;

    private static (List<int> numbers, string prerelease) Split(string? version)
    {
        var numbers = new List<int>();
        var pre = "";
        if (string.IsNullOrWhiteSpace(version)) return (numbers, pre);

        var v = version.Trim();
        if (v.StartsWith('v') || v.StartsWith('V')) v = v[1..];
        
        if (v.StartsWith("dv", StringComparison.OrdinalIgnoreCase)) v = v[2..];

        var dash = v.IndexOf('-');
        var core = dash < 0 ? v : v[..dash];
        if (dash >= 0) pre = v[(dash + 1)..];

        foreach (var part in core.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
            numbers.Add(int.TryParse(digits, out var n) ? n : 0);
        }
        return (numbers, pre);
    }
}
