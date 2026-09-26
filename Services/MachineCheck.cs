using System;
using System.Collections.Generic;
using System.Linq;
using ClassSoftwareHub.Desktop.Data;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>一款软件在这台电脑上的核实结果。</summary>
public enum CheckStatus
{
    /// <summary>已安装，版本与清单一致。</summary>
    Installed,

    /// <summary>已安装，但本机版本低于清单版本（建议升级）。</summary>
    Outdated,

    /// <summary>已安装，且版本高于清单（无需处理）。</summary>
    Newer,

    /// <summary>已安装，但有一侧版本号无法解析，无法比对。</summary>
    Unknown,

    /// <summary>安装记录中没有该软件（界面上显示「未安装」）。</summary>
    Missing,
}

/// <summary>一条核实结果。</summary>
public sealed record CheckItem(
    SoftwareApp App,
    CheckStatus Status,
    string InstalledName,
    string InstalledVersion,
    string Note);

/// <summary>核实汇总。</summary>
public sealed record CheckSummary(int Total, int Installed, int Outdated, int Newer, int Unknown, int Missing);

/// <summary>
/// 「本机核实」：拿内容包里的软件清单，逐条跟本机已装软件对一遍。
///
/// 匹配方式：名称归一化后的精确/包含匹配（`InstalledApps.Normalize`）。
/// ⚠️ 这是**模糊匹配**，不是权威判定 —— 同一款软件在不同渠道的显示名差异很大
/// （"Google Chrome" / "Chrome" / "Google Chrome (x64)"），所以：
///   · 认出来是"参考"，认不出来**不等于没装**（绿色版、商店版、改名版都查不到）
///   · 界面上必须把这个口径写出来，不能让老师以为这是权威报告
/// </summary>
public static class MachineCheck
{
    public static List<CheckItem> Run(IEnumerable<SoftwareApp> apps, IReadOnlyList<InstalledApp> installed)
    {
        // 预处理：归一化一次，别在双重循环里反复算。
        // ⚠️ 这里**不按长度过滤**。踩过的坑：一开始写成 `Norm.Length >= 3` 才收进索引，
        //    结果「钉钉」「微信」这类两字中文名归一化后正好 2 个字，被整条滤掉 ——
        //    于是本机明明装着，界面却永远显示"没查到"。老师一眼就能看出不对。
        //    长度只用来限制**包含式**匹配，精确相等不看长度。
        var index = installed
            .Select(a => (App: a, Norm: InstalledApps.Normalize(a.Name)))
            .Where(x => x.Norm.Length > 0)
            .ToList();

        var results = new List<CheckItem>();

        foreach (var app in apps)
        {
            var norm = InstalledApps.Normalize(app.Name);
            var hit = norm.Length > 0 ? BestMatch(norm, index) : null;

            if (hit is null)
            {
                var missingNote = !string.IsNullOrWhiteSpace(app.Store)
                    ? "该软件通过 Microsoft Store 分发，不在安装记录中，可在「Microsoft Store → 库」中核对"
                    : Weight(norm) < 4
                        ? "软件名称过短，无法可靠匹配，请在「程序和功能」中人工核对"
                        : "安装记录中没有该软件（免安装版、商店版及改过名的软件不会出现）";
                results.Add(new CheckItem(app, CheckStatus.Missing, "", "", missingNote));
                continue;
            }

            var installedVersion = hit.Value.App.Version;
            var (status, note) = Judge(app.Version, installedVersion, hit.Value.App.Name);
            results.Add(new CheckItem(app, status, hit.Value.App.Name, installedVersion, note));
        }

        // 排序：先按「要不要管」排（版本旧的最前，其次没装），同类按名称
        return results
            .OrderBy(r => Rank(r.Status))
            .ThenBy(r => r.App.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    private static int Rank(CheckStatus s) => s switch
    {
        CheckStatus.Outdated => 0,   // 最需要关注
        CheckStatus.Missing => 1,
        CheckStatus.Unknown => 2,
        CheckStatus.Installed => 3,
        CheckStatus.Newer => 4,
        _ => 5,
    };

    /// <summary>在已装列表里挑最像的那一条。优先精确相等；否则取包含关系里重合度最高的。</summary>
    private static (InstalledApp App, string Norm)? BestMatch(
        string norm, List<(InstalledApp App, string Norm)> index)
    {
        (InstalledApp App, string Norm)? best = null;
        var bestScore = 0d;

        foreach (var (app, normInstalled) in index)
        {
            double score;

            if (normInstalled == norm)
            {
                score = 100;   // 归一化后完全一样，直接用（再短也认：「钉钉」==「钉钉」就是它）
            }
            else if (normInstalled.Contains(norm, StringComparison.Ordinal) ||
                     norm.Contains(normInstalled, StringComparison.Ordinal))
            {
                // 包含式匹配：用「短的占长的比例」当重合度，太短或比例太低（"微信" 撞 "微信输入法"）不认。
                // ⚠️ 不能按字符数比：中文两个字就顶英文四个字（见 Weight）。
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
                best = (app, normInstalled);
            }
        }

        return best;
    }

    /// <summary>
    /// 名称的「有效长度」：中日韩文字一个字按两个字算。
    /// 理由是信息量 —— 「钉钉」「微信」两个汉字跟四个字母差不多具体，
    /// 一律按字符数比会把这些正常名字当成"太短、不可信"而漏掉。
    /// </summary>
    private static int Weight(string s)
    {
        var w = 0;
        foreach (var ch in s) w += IsCjk(ch) ? 2 : 1;
        return w;
    }

    private static bool IsCjk(char c) =>
        c is >= '\u3040' and <= '\u30FF'   // 日文假名
          or >= '\u3400' and <= '\u4DBF'   // 扩展 A
          or >= '\u4E00' and <= '\u9FFF'   // 基本汉字
          or >= '\uAC00' and <= '\uD7AF'   // 韩文
          or >= '\uF900' and <= '\uFAFF';  // 兼容汉字

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

    /// <summary>只认「纯点分数字」（允许开头带一个 v）。"跟随官网" / "上次更新日期..." 一律返回 null。</summary>
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
