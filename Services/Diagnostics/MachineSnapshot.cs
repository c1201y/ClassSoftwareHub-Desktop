using System;
using System.Collections.Generic;

namespace ClassSoftwareHub.Desktop.Services.Diagnostics;

/// <summary>
/// 一次「体检」的**只读**快照（2026-10-18）。
///
/// <para>
/// 这是 Hub AI 的数据地基：采集层填它，摘要层把它压成给模型看的那点字，界面原样显示它。
/// ⛔ 这里**只放事实**，不放下任何结论 —— "该换内存了""磁盘太满"这类判断属于界面/模型，
/// 一旦写进采集层，以后想改口径就得动底层。
/// </para>
/// <para>
/// ⚠️ 所有容量字段统一用 MB（整数），时间统一本地时间。模型对单位很敏感，
/// 同一份快照里混用 GB/MB 会让它算错数量级。
/// </para>
/// </summary>
public sealed class MachineSnapshot
{
    /// <summary>CPU 型号，例如「12th Gen Intel(R) Core(TM) i5-12400」。</summary>
    public string CpuName { get; init; } = "";

    /// <summary>逻辑处理器数（含超线程）。</summary>
    public int CpuThreads { get; init; }

    /// <summary>物理核心数。取不到时为 0。</summary>
    public int CpuCores { get; init; }

    /// <summary>标称主频（MHz）。取不到时为 0。</summary>
    public int CpuMhz { get; init; }

    /// <summary>CPU 当前占用百分比（0~100）。<b>-1 = 本次没取到</b>，别当成 0% 用。</summary>
    public double CpuLoadPercent { get; init; } = -1;

    /// <summary>物理内存总量（MB）。</summary>
    public long MemoryTotalMb { get; init; }

    /// <summary>可用物理内存（MB）。</summary>
    public long MemoryFreeMb { get; init; }

    /// <summary>内存占用百分比（0~100），直接取系统给的值。</summary>
    public int MemoryLoadPercent { get; init; }

    /// <summary>系统显示名，例如「Windows 11 专业版」。</summary>
    public string OsName { get; init; } = "";

    /// <summary>构建号，例如 26100。判断 Win10/Win11 就看它（≥22000 是 11）。</summary>
    public int OsBuild { get; init; }

    /// <summary>显示版本，例如「24H2」。可能为空。</summary>
    public string OsDisplayVersion { get; init; } = "";

    public bool Is64BitOs { get; init; }

    /// <summary>固定磁盘（不含光驱、网络盘）。</summary>
    public List<DiskSnapshot> Disks { get; init; } = new();

    /// <summary>当前进程总数。</summary>
    public int ProcessCount { get; init; }

    /// <summary>内存占用最高的几个进程（按工作集降序，最多 5 个）。</summary>
    public List<ProcessSnapshot> TopProcesses { get; init; } = new();

    /// <summary>开机启动项条数（HKCU/HKLM 的 Run + RunOnce）。</summary>
    public int StartupCount { get; init; }

    /// <summary>已装软件总数（桌面程序 + 商店应用，已去重）。</summary>
    public int InstalledAppCount { get; init; }

    /// <summary>已装软件名样本（最多 60 个）—— 全量几百条塞给模型没意义，只给样本。</summary>
    public List<string> InstalledAppSample { get; init; } = new();

    /// <summary>AppData 下体积最大的几个目录名（不含大小，大小要另外扫，很慢）。</summary>
    public List<string> AppDataTopFolders { get; init; } = new();

    /// <summary>
    /// 采集失败的项目。⚠️ **不要吞掉**：班级电脑上安全软件常常拦注册表/进程枚举，
    /// 界面必须能说清"哪一项没取到"，否则用户会以为电脑真的缺这块。
    /// </summary>
    public List<string> Warnings { get; init; } = new();

    /// <summary>采集耗时（毫秒）。用来盯"开页卡一下"是不是采集拖的。</summary>
    public long ElapsedMs { get; init; }

    public DateTime CollectedAt { get; init; } = DateTime.Now;

    /// <summary>是不是什么都没采到（全失败时界面直接报错，别显示一张空表）。</summary>
    public bool IsEmpty => CpuName.Length == 0 && MemoryTotalMb == 0 && Disks.Count == 0;
}

/// <summary>一块磁盘的容量事实。</summary>
public sealed class DiskSnapshot
{
    public string Drive { get; init; } = "";
    public string Label { get; init; } = "";
    public string Format { get; init; } = "";
    public long TotalMb { get; init; }
    public long FreeMb { get; init; }

    /// <summary>已用百分比（0~100）。总量为 0 时返回 0。</summary>
    public int UsedPercent => TotalMb <= 0 ? 0 : (int)Math.Round((TotalMb - FreeMb) * 100.0 / TotalMb);
}

/// <summary>一个进程的内存事实。</summary>
public sealed class ProcessSnapshot
{
    public string Name { get; init; } = "";
    public long WorkingSetMb { get; init; }
}
