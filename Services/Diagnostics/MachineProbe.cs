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
















public static class MachineProbe
{
    
    private const int CpuSampleGapMs = 250;

    
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
                    
                    AppLog.Exception("hubai", ex, $"读磁盘 {d.Name}");
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add("磁盘");
            AppLog.Exception("hubai", ex, "枚举磁盘");
        }

        
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string ?? "";
            osBuild = ParseInt(key?.GetValue("CurrentBuildNumber") as string ?? key?.GetValue("CurrentBuild") as string);
            osVer = key?.GetValue("DisplayVersion") as string ?? "";

            
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
                        
                        Name = string.IsNullOrWhiteSpace(p.ProcessName) ? "(受保护进程)" : p.ProcessName,
                        WorkingSetMb = p.WorkingSet64 / 1024 / 1024,
                    });
                }
                catch
                {
                    
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

        
        try
        {
            startupCount = CountStartupEntries();
        }
        catch (Exception ex)
        {
            warnings.Add("启动项");
            AppLog.Exception("hubai", ex, "统计启动项");
        }

        
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

        
        
        AppLog.Info("hubai",
            $"快照摘要：CPU={cpuName} 线程={cpuThreads} 负载={cpuLoad:0.#}% " +
            $"内存={memTotal}MB 可用={memFree}MB 占用={memLoad}% " +
            $"磁盘={disks.Count}块 进程={processCount} 启动项={startupCount} 应用={appCount}");

        AppLog.Done("hubai", $"采集本机快照（{watch.ElapsedMilliseconds}ms）", watch.Elapsed);
        return snapshot;
    }

    
    
    

    
    
    
    
    
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

    
    
    
    
    private static string Squash(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        return string.Join(' ', raw.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private static int ParseInt(string? text)
        => int.TryParse(text, out var v) ? v : 0;

    
    
    

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
