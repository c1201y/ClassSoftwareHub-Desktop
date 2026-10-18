using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.Win32;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace ClassSoftwareHub.Desktop.Services.Diagnostics;

/// <summary>
/// 本机快照采集（2026-10-18）。**全程只读**：只读注册表、只读系统计数器、不写盘、不联网。
///
/// <para>
/// 设计约束，改之前先看：
/// <list type="number">
///   <item><b>不用 WMI</b>。班级电脑上 WMI 经常被安全软件拦，而且第一次查询要起服务、动辄几百毫秒。
///         这里能走注册表 / P/Invoke 的一律走它们。</item>
///   <item><b>每支探针独立 try/catch</b>，一支挂了不影响别的，失败项进 <see cref="MachineSnapshot.Warnings"/>。
///         ⛔ 绝不静默吞掉 —— 用户会以为电脑真的缺这块。</item>
///   <item><b>整体耗时控制在 ~500ms 内</b>。唯一的阻塞是 CPU 占用要采两次（间隔 250ms 才有意义），
///         所以 <see cref="CollectAsync"/> 整体丢进线程池，不占 UI 线程。</item>
/// </list>
/// </para>
/// </summary>
public static class MachineProbe
{
    /// <summary>CPU 占用两次采样的间隔。太短会被调度噪声带偏，太长用户等不起。</summary>
    private const int CpuSampleGapMs = 250;

    /// <summary>已装软件名最多带几个。几百条全列出去对模型只是噪声。</summary>
    private const int InstalledAppSampleLimit = 60;

    public static Task<MachineSnapshot> CollectAsync(CancellationToken token = default)
        => Task.Run(() => Collect(token), token);

    public static MachineSnapshot Collect(CancellationToken token = default)
    {
        var watch = Stopwatch.StartNew();
        var warnings = new List<string>();

        AppLog.Begin("hubai", "采集本机快照");

        var cpuName = "";
        int cpuThreads = 0, cpuCores = 0, cpuMhz = 0;
        double cpuLoad = -1;
        long memTotal = 0, memFree = 0;
        int memLoad = 0;
        var osName = "";
        int osBuild = 0;
        var osVer = "";
        var disks = new List<DiskSnapshot>();
        int processCount = 0;
        var topProcesses = new List<ProcessSnapshot>();
        int startupCount = 0;
        int appCount = 0;
        var appSample = new List<string>();
        var appDataFolders = new List<string>();

        // ── ① CPU 型号 / 主频（注册表，最快，无依赖） ──
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key is not null)
            {
                cpuName = Squash(key.GetValue("ProcessorNameString") as string);
                if (key.GetValue("~MHz") is int mhz) cpuMhz = mhz;
            }
            cpuThreads = Environment.ProcessorCount;
        }
        catch (Exception ex)
        {
            warnings.Add("CPU 型号");
            AppLog.Exception("hubai", ex, "读 CPU 型号");
        }

        // ── ② 内存总量 / 占用（GlobalMemoryStatusEx，一次调用拿全） ──
        try
        {
            var st = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref st))
            {
                memTotal = (long)(st.ullTotalPhys / 1024 / 1024);
                memFree = (long)(st.ullAvailPhys / 1024 / 1024);
                memLoad = (int)st.dwMemoryLoad;
            }
            else
            {
                warnings.Add("内存");
            }
        }
        catch (Exception ex)
        {
            warnings.Add("内存");
            AppLog.Exception("hubai", ex, "读内存状态");
        }

        // ── ③ CPU 占用（两次采样算差值） ──
        try
        {
            cpuLoad = SampleCpuLoad(token);
            if (cpuLoad < 0) warnings.Add("CPU 占用");
        }
        catch (Exception ex)
        {
            warnings.Add("CPU 占用");
            AppLog.Exception("hubai", ex, "采样 CPU 占用");
        }

        // ── ④ 磁盘（复用 DiskUsage，跳过光驱/网络盘） ──
        try
        {
            foreach (var d in DiskUsage.FixedDrives())
            {
                try
                {
                    disks.Add(new DiskSnapshot
                    {
                        Drive = d.Name,
                        Label = SafeVolumeLabel(d),
                        Format = d.DriveFormat,
                        TotalMb = d.TotalSize / 1024 / 1024,
                        FreeMb = d.AvailableFreeSpace / 1024 / 1024,
                    });
                }
                catch (Exception ex)
                {
                    // 单块盘读不到（BitLocker 锁定等）不该拖垮整份快照
                    AppLog.Exception("hubai", ex, $"读磁盘 {d.Name}");
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add("磁盘");
            AppLog.Exception("hubai", ex, "枚举磁盘");
        }

        // ── ⑤ 系统版本 ──
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string ?? "";
            osBuild = ParseInt(key?.GetValue("CurrentBuildNumber") as string ?? key?.GetValue("CurrentBuild") as string);
            osVer = key?.GetValue("DisplayVersion") as string ?? "";

            // ⚠️ Win11 的 ProductName 仍然写着 "Windows 10"（微软没改），只能靠构建号判断
            osName = (product.Length > 0 && osBuild >= 22000)
                ? product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal)
                : product;
            if (osName.Length == 0) osName = RuntimeInformation.OSDescription;
        }
        catch (Exception ex)
        {
            warnings.Add("系统版本");
            AppLog.Exception("hubai", ex, "读系统版本");
            osName = RuntimeInformation.OSDescription;
        }

        // ── ⑥ 进程（数量 + 内存前几名） ──
        try
        {
            var all = Process.GetProcesses();
            processCount = all.Length;

            var ranked = new List<ProcessSnapshot>();
            foreach (var p in all)
            {
                try
                {
                    ranked.Add(new ProcessSnapshot
                    {
                        // ⚠️ 有些系统进程的 ProcessName 为空串（受保护进程），补一个占位
                        Name = string.IsNullOrWhiteSpace(p.ProcessName) ? "(受保护进程)" : p.ProcessName,
                        WorkingSetMb = p.WorkingSet64 / 1024 / 1024,
                    });
                }
                catch
                {
                    // 单个进程读不到工作集是常态（权限不够），跳过即可，不算快照失败
                }
                finally
                {
                    p.Dispose();
                }
            }

            topProcesses = ranked
                .OrderByDescending(x => x.WorkingSetMb)
                .Take(5)
                .ToList();
        }
        catch (Exception ex)
        {
            warnings.Add("进程");
            AppLog.Exception("hubai", ex, "枚举进程");
        }

        token.ThrowIfCancellationRequested();

        // ── ⑦ 启动项（只数条数，不列举 —— 列举出来的名字对"评估性能"没有额外价值） ──
        try
        {
            startupCount = CountStartupEntries();
        }
        catch (Exception ex)
        {
            warnings.Add("启动项");
            AppLog.Exception("hubai", ex, "统计启动项");
        }

        // ── ⑧ AppData 大头目录（只给名字，大小要全盘扫，太慢） ──
        try
        {
            appDataFolders = AppDataScan.TopFolders()
                .Select(f => f.Name)
                .Where(n => n.Length > 0)
                .Take(8)
                .ToList();
        }
        catch (Exception ex)
        {
            warnings.Add("AppData 目录");
            AppLog.Exception("hubai", ex, "列 AppData 目录");
        }

        // ── ⑨ 已装软件（最慢的一支，放最后，前面几支先把界面喂饱） ──
        try
        {
            var apps = InstalledApps.Enumerate();
            appCount = apps.Count;
            appSample = apps
                .Select(a => a.Name)
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(InstalledAppSampleLimit)
                .ToList();
        }
        catch (Exception ex)
        {
            warnings.Add("已装软件");
            AppLog.Exception("hubai", ex, "枚举已装软件");
        }

        watch.Stop();

        var snapshot = new MachineSnapshot
        {
            CpuName = cpuName,
            CpuThreads = cpuThreads,
            CpuCores = cpuCores,
            CpuMhz = cpuMhz,
            CpuLoadPercent = cpuLoad,
            MemoryTotalMb = memTotal,
            MemoryFreeMb = memFree,
            MemoryLoadPercent = memLoad,
            OsName = osName,
            OsBuild = osBuild,
            OsDisplayVersion = osVer,
            Is64BitOs = Environment.Is64BitOperatingSystem,
            Disks = disks,
            ProcessCount = processCount,
            TopProcesses = topProcesses,
            StartupCount = startupCount,
            InstalledAppCount = appCount,
            InstalledAppSample = appSample,
            AppDataTopFolders = appDataFolders,
            Warnings = warnings,
            ElapsedMs = watch.ElapsedMilliseconds,
        };

        if (warnings.Count > 0)
            AppLog.Warn("hubai", $"快照有 {warnings.Count} 项没取到：{string.Join("、", warnings)}");

        // 摘要进日志：用户报「读出来的数不对」时，这是唯一能离线复核的东西。
        // ⚠️ 只记硬件与数量，**不记已装软件名**（那是用户隐私）。
        AppLog.Info("hubai",
            $"快照摘要：CPU={cpuName} 线程={cpuThreads} 负载={cpuLoad:0.#}% " +
            $"内存={memTotal}MB 可用={memFree}MB 占用={memLoad}% " +
            $"磁盘={disks.Count}块 进程={processCount} 启动项={startupCount} 应用={appCount}");

        AppLog.Done("hubai", $"采集本机快照（{watch.ElapsedMilliseconds}ms）", watch.Elapsed);
        return snapshot;
    }

    // ══════════════════════════════════════════════════════
    //  各支实现
    // ══════════════════════════════════════════════════════

    /// <summary>
    /// CPU 占用：<c>GetSystemTimes</c> 采两次算差值。
    /// ⚠️ kernel 时间里**包含 idle**，所以 total = kernel + user，忙 = total − idle。
    /// 只调一次是没有意义的（那给的是开机以来的平均值），必须是差值。
    /// </summary>
    private static double SampleCpuLoad(CancellationToken token)
    {
        if (!GetSystemTimes(out var idle1, out var kernel1, out var user1)) return -1;

        token.WaitHandle.WaitOne(CpuSampleGapMs);
        token.ThrowIfCancellationRequested();

        if (!GetSystemTimes(out var idle2, out var kernel2, out var user2)) return -1;

        var idle = ToLong(idle2) - ToLong(idle1);
        var total = (ToLong(kernel2) - ToLong(kernel1)) + (ToLong(user2) - ToLong(user1));
        if (total <= 0) return -1;

        var load = (1.0 - (double)idle / total) * 100.0;
        return Math.Clamp(load, 0, 100);
    }

    private static long ToLong(ComTypes.FILETIME ft)
        => ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    /// <summary>数 HKCU / HKLM × Run / RunOnce 四处的值个数。</summary>
    private static int CountStartupEntries()
    {
        var count = 0;

        foreach (var (hive, path) in new[]
                 {
                     (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"),
                     (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
                     (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
                     (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
                 })
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                count += key?.ValueCount ?? 0;
            }
            catch (Exception ex)
            {
                // 单个位置读不到（组策略锁死 Run 键）不影响已数到的部分
                AppLog.Exception("hubai", ex, $"读启动项 {path}");
            }
        }

        return count;
    }

    private static string SafeVolumeLabel(DriveInfo d)
    {
        try { return d.VolumeLabel ?? ""; }
        catch { return ""; }
    }

    /// <summary>
    /// 注册表里的 CPU 名常见「12th Gen Intel(R) Core(TM) i5-12400    @ 2.50GHz」这种：
    /// 前后有空格、中间还有连续空格，直接显示会歪。统一压成单个空格。
    /// </summary>
    private static string Squash(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        return string.Join(' ', raw.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private static int ParseInt(string? text)
        => int.TryParse(text, out var v) ? v : 0;

    // ══════════════════════════════════════════════════════
    //  P/Invoke
    // ══════════════════════════════════════════════════════

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out ComTypes.FILETIME lpIdleTime,
        out ComTypes.FILETIME lpKernelTime,
        out ComTypes.FILETIME lpUserTime);
}
