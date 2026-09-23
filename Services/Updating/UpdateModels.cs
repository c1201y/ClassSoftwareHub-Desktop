using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClassSoftwareHub.Desktop.Services.Updating;

/// <summary>更新通道。</summary>
public enum UpdateChannel
{
    /// <summary>正式版（对应 GitHub Release 的 Latest，非预发布）。</summary>
    Stable,

    /// <summary>预览版（对应 GitHub Release 的 Pre-release）。</summary>
    Insider,
}

/// <summary>通道的字符串形式（设置里存的就是这个）。</summary>
public static class UpdateChannels
{
    public const string Stable = "stable";
    public const string Insider = "insider";

    public static UpdateChannel Parse(string? value) =>
        string.Equals(value, Insider, StringComparison.OrdinalIgnoreCase) ? UpdateChannel.Insider : UpdateChannel.Stable;

    public static string ToText(UpdateChannel channel) => channel == UpdateChannel.Insider ? Insider : Stable;

    /// <summary>界面上的中文名。</summary>
    public static string ToDisplay(UpdateChannel channel) => channel == UpdateChannel.Insider ? "Insider" : "正式版";
}

/// <summary>一个可下载的更新文件。</summary>
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

/// <summary>一个发布（对应 GitHub 的一个 Release）。</summary>
public sealed record UpdateRelease(
    string Tag,
    string Version,
    UpdateChannel Channel,
    bool Prerelease,
    string Notes,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<UpdatePackage> Packages)
{
    /// <summary>取主安装包（按名字挑，见 GitHubReleaseSource.PickInstallers）。</summary>
    public UpdatePackage? Primary =>
        Packages.Count == 0 ? null : Packages[0];
}

/// <summary>一次检查更新的结果。</summary>
public sealed record UpdateCheckResult(
    bool HasUpdate,
    UpdateRelease? Release,
    string Message)
{
    public static UpdateCheckResult None(string message) => new(false, null, message);
}

/// <summary>
/// 版本号比较（够用的 SemVer 子集）：
/// 1.0.0 / 1.0.0-insider.3 / v1.2.3 / 1.2.3.4
/// 规则：数字段逐个比；数字相同的情况下，**带预发布后缀的小于不带后缀的**（正式 &gt; 预览）；
/// 两边都有后缀时按点分段比较（数字段按数字比，其它按字典序）。
/// </summary>
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
        if (pa.Length == 0) return 1;    // a 是正式版
        if (pb.Length == 0) return -1;   // b 是正式版

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
            else if (numA) c = -1;                 // 数字段 < 字母段
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
        // 桌面版显示前缀 dv
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
