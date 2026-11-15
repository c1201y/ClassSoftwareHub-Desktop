using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>一个驱动器的分页文件设置方式（与系统「虚拟内存」对话框上的三个单选一一对应）。</summary>
public enum PageFileMode
{
    /// <summary>无分页文件 —— 注册表里**没有**这个盘的条目。</summary>
    None,

    /// <summary>系统管理的大小 —— 条目写成 <c>0 0</c>。</summary>
    SystemManaged,

    /// <summary>自定义大小 —— 条目写成 <c>初始 最大</c>。</summary>
    Custom,
}

/// <summary>一个驱动器的分页文件配置。<paramref name="Drive"/> 一律是 <c>C:</c> 这种两字符形式。</summary>
public sealed record PageFilePlan(string Drive, PageFileMode Mode, int MinMb = 0, int MaxMb = 0)
{
    /// <summary>列表里那一列「分页文件大小」，照系统对话框的写法。</summary>
    public string SizeText => Mode switch
    {
        PageFileMode.None => "无",
        PageFileMode.SystemManaged => "系统管理",
        _ => $"{MinMb} - {MaxMb}",
    };
}

/// <summary>
/// 分页文件（虚拟内存）配置的解析与生成（2026-10-31，需求清单第 19 条）。
///
/// <para>
/// 注册表里就一个值：<c>HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PagingFiles</c>，
/// 类型 <c>REG_MULTI_SZ</c>，每个驱动器一条，形如 <c>g:\pagefile.sys 14336 14337</c>。
/// </para>
///
/// <para>
/// 三个容易搞错、且错了就看不出问题的地方，都收在这个文件里：
/// <list type="number">
///   <item><b>盘符大小写</b>：本机注册表里写的是小写 <c>g:\pagefile.sys</c>，界面按大写显示，
///         解析时统一成大写，否则「G: 盘明明配了却显示无」；</item>
///   <item><b><c>?</c> 前缀</b>：勾选「自动管理」时系统写的是 <c>?:\pagefile.sys 0 0</c>，
///         <c>?</c> 指系统盘。不认这一条，自动管理就会被当成「C 盘没配分页文件」；</item>
///   <item><b>「无」不是写 0</b>：某个盘不要分页文件，做法是**整条不写**，
///         而不是写 <c>0 0</c>（那是「系统管理」的意思，正好相反）。</item>
/// </list>
/// </para>
///
/// <para>
/// ⚠️ 纯函数、不碰注册表：写错一个数字的后果是用户点完 UAC、重启之后才发现机器不对劲，
/// 那不是能靠肉眼复查发现的问题，只能靠测试钉住。
/// </para>
/// </summary>
public static class PageFileRules
{
    /// <summary>分页文件下限。系统对话框里那句「允许的最小值：16 MB」就是这个数。</summary>
    public const int MinAllowedMb = 16;

    /// <summary>「自动管理」时系统写在注册表里的那一条。</summary>
    public const string AutomaticEntry = @"?:\pagefile.sys 0 0";

    /// <summary>把 <c>c:</c> / <c>c</c> 统一成 <c>C:</c>；认不出来返回空串。</summary>
    public static string NormalizeDrive(string? drive)
    {
        var text = (drive ?? "").Trim();
        if (text.Length == 0) return "";

        var letter = char.ToUpperInvariant(text[0]);
        if (letter is < 'A' or > 'Z') return "";
        return $"{letter}:";
    }

    /// <summary>
    /// 解析注册表里的一条。<paramref name="systemDrive"/> 是把 <c>?</c> 翻译成的实际盘符。
    /// 认不出来（路径不是 <c>X:\pagefile.sys</c> 这种）返回 <c>null</c>，交给调用方忽略 ——
    /// 猜一个盘符出来比忽略更糟。
    /// </summary>
    public static PageFilePlan? ParseEntry(string? entry, string systemDrive)
    {
        var text = (entry ?? "").Trim();
        if (text.Length == 0) return null;

        var bits = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (bits.Length == 0) return null;

        var path = bits[0];
        if (path.Length < 3 || path[1] != ':') return null;

        // "?" 是系统盘的占位符（自动管理时由系统写入）
        var drive = path[0] == '?' ? NormalizeDrive(systemDrive) : NormalizeDrive(path[..2]);
        if (drive.Length == 0) return null;

        var min = bits.Length > 1 && int.TryParse(bits[1], out var a) ? a : 0;
        var max = bits.Length > 2 && int.TryParse(bits[2], out var b) ? b : 0;

        // 负数只可能来自手工改坏，按 0 处理（0 0 == 系统管理，语义上不至于更糟）
        if (min < 0) min = 0;
        if (max < 0) max = 0;

        return min == 0 && max == 0
            ? new PageFilePlan(drive, PageFileMode.SystemManaged)
            : new PageFilePlan(drive, PageFileMode.Custom, min, max);
    }

    /// <summary>解析整个 <c>PagingFiles</c>。同一盘出现多条时**只认第一条**（后面的 Windows 也不会用）。</summary>
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

    /// <summary>
    /// 是不是「自动管理所有驱动器的分页文件大小」。
    /// <para>
    /// 判据：整个值**只有一条**，且那一条是 <c>?:\pagefile.sys 0 0</c>。
    /// 任何手动配置（包括只有一个 C 盘系统管理）都写成具体盘符，所以不会误判。
    /// </para>
    /// </summary>
    public static bool IsAutomatic(IReadOnlyList<string>? entries)
    {
        if (entries is null || entries.Count != 1) return false;

        var text = (entries[0] ?? "").Trim();
        if (text.Length == 0) return false;
        if (text[0] != '?') return false;

        var plan = ParseEntry(text, "C:");
        return plan is { Mode: PageFileMode.SystemManaged };
    }

    /// <summary>
    /// 生成要写进 <c>PagingFiles</c> 的条目。
    /// <para>
    /// ⛔ 顺序固定为「系统盘打头，其余按盘符」：值本身顺序无关，但固定下来之后
    /// 「写完重读、逐条比对」才有稳定的判据，否则每次读回来的顺序不同会误判成没生效。
    /// </para>
    /// </summary>
    public static List<string> BuildEntries(IReadOnlyList<PageFilePlan> plans, string systemDrive)
    {
        var sys = NormalizeDrive(systemDrive);

        var ordered = plans
            .Where(p => p.Mode != PageFileMode.None)
            .Select(p => new { Drive = NormalizeDrive(p.Drive), Plan = p })
            .Where(x => x.Drive.Length > 0)
            .GroupBy(x => x.Drive)
            .Select(g => g.First().Plan)                  // 同盘重复时只留第一条
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

    /// <summary>
    /// 单个驱动器的校验。返回 <c>null</c> 表示没问题，否则是给用户看的一句话。
    /// <para>
    /// ⚠️ 上限卡在**该盘可用空间**：超过可用空间时 Windows 建不出分页文件，
    /// 而它不会报错，只会让这一盘实际没有分页文件 —— 用户以为设好了，直到内存吃紧才发现。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 整份配置的校验。返回 <c>null</c> 表示没问题。
    /// <para>
    /// ⛔ 至少要留一个分页文件：全关掉之后 Windows 仍能开机，但内存吃紧时进程会被直接结束，
    /// 崩溃转储也写不出来 —— 这类"看起来没事、出事时全是怪现象"的配置本项目一律不提供。
    /// </para>
    /// </summary>
    public static string? ValidateAll(IReadOnlyList<PageFilePlan> plans)
    {
        if (plans.All(p => p.Mode == PageFileMode.None))
            return "至少要保留一个驱动器的分页文件。";

        return null;
    }

    /// <summary>
    /// 两份配置是不是一样（写完重读，用它判断有没有真的生效）。
    /// <para>
    /// 只比「盘符 + 模式 + 两个数」，不比条目文本 —— 大小写、空格、条目顺序都不该算作差异。
    /// </para>
    /// </summary>
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
