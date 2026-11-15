using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassSoftwareHub.Desktop.Services.Diagnostics;

/// <summary>一条体检结论的轻重。</summary>
public enum HealthLevel
{
    /// <summary>正常，不需要用户做什么。</summary>
    Good,

    /// <summary>可以留意，但不必紧张。</summary>
    Notice,

    /// <summary>确实需要处理，否则会影响使用。</summary>
    Warn,
}

/// <summary>一条体检结论。<see cref="Detail"/> 里带具体数字，别写成空泛的形容。</summary>
public sealed record HealthFinding(HealthLevel Level, string Title, string Detail);

/// <summary>
/// 本地规则判断（2026-10-18）：**不接模型也能给出的结论**。
///
/// <para>
/// 存在的意义有两个：一是没有配置密钥的用户也得看到点有用的东西；二是它是模型输出的"下限"——
/// 模型抽风时，界面至少还有这几条准的。
/// </para>
/// <para>
/// ⛔ 两条自我约束，改规则前先看：
/// <list type="number">
///   <item><b>少报不滥报</b>。规则只在确实会影响使用时才出一条，不做"建议定期体检"这类废话。
///         一次给出五六条会让用户觉得处处是毛病，反而不看了。</item>
///   <item><b>不做推荐</b>。"该装什么软件""该删哪个文件"属于模型和用户，这里只描述状态。</item>
/// </list>
/// </para>
/// <para>
/// ⚠️ 所有阈值都是经验值，面向班级电脑（4~8 GB 内存、机械盘、长期不关机）。
/// 改阈值时把理由写进注释，别只改数字。
/// </para>
/// </summary>
public static class MachineDiagnosis
{
    /// <summary>系统盘剩余空间低于这个值就报紧张 —— Windows 功能更新本身要 8~10 GB 周转。</summary>
    private const long TightFreeMb = 15 * 1024;

    /// <summary>非系统盘剩余低于这个值就提醒。数据盘不吃系统更新的亏，门槛可以低得多。</summary>
    private const long TightFreeMbOther = 5 * 1024;

    /// <summary>系统盘已用超过这个百分比就提醒。</summary>
    private const int DiskBusyPercent = 80;

    /// <summary>
    /// 非系统盘已用的提醒线。**比系统盘宽松 10 个点**（Nick 2026-10-18 定：其他盘也要看）：
    /// 数据盘装到八成是常态，系统盘装到八成就要出事了——更新失败、休眠文件写不下、虚拟内存吃紧。
    /// </summary>
    private const int DiskBusyPercentOther = 90;

    /// <summary>启动项超过这个数量，开机时间通常已经能感觉出来。</summary>
    private const int ManyStartup = 25;

    /// <summary>后台进程数超过这个值，多半是装了一堆常驻监控。</summary>
    private const int ManyProcesses = 350;

    public static IReadOnlyList<HealthFinding> Analyze(MachineSnapshot s)
    {
        var list = new List<HealthFinding>();

        if (s.IsEmpty)
        {
            list.Add(new HealthFinding(HealthLevel.Warn, "未能读取本机信息", "采集全部失败，请查看日志确认是否被安全软件拦截。"));
            return list;
        }

        // ── 内存 ──
        if (s.MemoryTotalMb > 0)
        {
            var gb = s.MemoryTotalMb / 1024.0;

            if (gb < 6)
            {
                list.Add(new HealthFinding(HealthLevel.Warn, "内存偏小",
                    $"总容量 {gb:0.#} GB。同时打开浏览器与教学软件时容易出现卡顿，建议优先选用轻量软件，并关闭暂时不用的程序。"));
            }
            else if (gb < 8)
            {
                list.Add(new HealthFinding(HealthLevel.Notice, "内存余量不大",
                    $"总容量 {gb:0.#} GB。日常使用够用，但同时运行多个较大的程序时会比较吃力。"));
            }

            // 占用高单独报，和容量小不是一回事（16 GB 也可能被浏览器吃满）
            if (s.MemoryLoadPercent >= 85)
            {
                list.Add(new HealthFinding(HealthLevel.Notice, "内存占用偏高",
                    $"当前占用 {s.MemoryLoadPercent}%，可用 {s.MemoryFreeMb / 1024.0:0.#} GB。若有长时间不用的程序，关闭后可以明显缓解。"));
            }
        }

        // ── 系统盘 ──
        var systemDisk = s.Disks.FirstOrDefault(d => d.Drive.StartsWith(@"C:", StringComparison.OrdinalIgnoreCase))
                         ?? s.Disks.FirstOrDefault();

        if (systemDisk is not null && systemDisk.TotalMb > 0)
        {
            if (systemDisk.FreeMb < TightFreeMb)
            {
                list.Add(new HealthFinding(HealthLevel.Warn, "系统盘空间紧张",
                    $"{systemDisk.Drive} 仅剩 {systemDisk.FreeMb / 1024.0:0.#} GB（{systemDisk.UsedPercent}% 已用）。" +
                    "系统更新与临时文件需要周转空间，空间过小会导致更新失败。"));
            }
            else if (systemDisk.UsedPercent >= DiskBusyPercent)
            {
                list.Add(new HealthFinding(HealthLevel.Notice, "系统盘占用较高",
                    $"{systemDisk.Drive} 已用 {systemDisk.UsedPercent}%，剩余 {systemDisk.FreeMb / 1024.0:0.#} GB。"));
            }
        }

        // ── 其他磁盘（2026-10-18 加）：系统盘健康不等于整机没空间问题 ──
        // ⚠️ 合并成**一条**：三块盘各报一条，用户会以为整机到处是毛病。
        var crowded = s.Disks
            .Where(d => d.TotalMb > 0 && !ReferenceEquals(d, systemDisk))
            .Where(d => d.UsedPercent >= DiskBusyPercentOther || d.FreeMb < TightFreeMbOther)
            .ToList();

        if (crowded.Count > 0)
        {
            list.Add(new HealthFinding(HealthLevel.Notice, "其他磁盘空间偏紧",
                string.Join("；", crowded.Select(d =>
                    $"{d.Drive} 剩余 {d.FreeMb / 1024.0:0.#} GB（{d.UsedPercent}% 已用）"))
                + "。这些盘不参与系统更新，但放不下东西时会表现为无法保存文件。"));
        }

        // ── 启动项 ──
        if (s.StartupCount > ManyStartup)
        {
            list.Add(new HealthFinding(HealthLevel.Notice, "开机启动项偏多",
                $"共 {s.StartupCount} 项。启动项越多，开机后一段时间内越容易卡顿；可以在「开机启动项」页里核对。"));
        }

        // ── 后台进程 ──
        if (s.ProcessCount > ManyProcesses)
        {
            list.Add(new HealthFinding(HealthLevel.Notice, "后台进程较多",
                $"当前 {s.ProcessCount} 个进程。若其中多数并不需要使用，会持续占用内存与处理器。"));
        }

        // ── 采集缺口：不报的话用户会以为没问题的项其实是没读到 ──
        if (s.Warnings.Count > 0)
        {
            list.Add(new HealthFinding(HealthLevel.Notice, "部分项目未能读取",
                $"{string.Join("、", s.Warnings)}。这些项目本次没有数据，判断时已跳过。"));
        }

        if (list.Count == 0)
        {
            list.Add(new HealthFinding(HealthLevel.Good, "各项指标正常",
                $"内存 {s.MemoryTotalMb / 1024.0:0.#} GB、系统盘剩余 {systemDisk?.FreeMb / 1024.0:0.#} GB，未发现明显瓶颈。"));
        }

        return list;
    }
}
