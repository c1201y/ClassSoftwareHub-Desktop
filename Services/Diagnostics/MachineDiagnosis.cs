using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassSoftwareHub.Desktop.Services.Diagnostics;


public enum HealthLevel
{
    
    Good,

    
    Notice,

    
    Warn,
}


public sealed record HealthFinding(HealthLevel Level, string Title, string Detail);





















public static class MachineDiagnosis
{
    
    private const long TightFreeMb = 15 * 1024;

    
    private const long TightFreeMbOther = 5 * 1024;

    
    private const int DiskBusyPercent = 80;

    
    
    
    
    private const int DiskBusyPercentOther = 90;

    
    private const int ManyStartup = 25;

    
    private const int ManyProcesses = 350;

    public static IReadOnlyList<HealthFinding> Analyze(MachineSnapshot s)
    {
        var list = new List<HealthFinding>();

        if (s.IsEmpty)
        {
            list.Add(new HealthFinding(HealthLevel.Warn, "未能读取本机信息", "采集全部失败，请查看日志确认是否被安全软件拦截。"));
            return list;
        }

        
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

            
            if (s.MemoryLoadPercent >= 85)
            {
                list.Add(new HealthFinding(HealthLevel.Notice, "内存占用偏高",
                    $"当前占用 {s.MemoryLoadPercent}%，可用 {s.MemoryFreeMb / 1024.0:0.#} GB。若有长时间不用的程序，关闭后可以明显缓解。"));
            }
        }

        
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

        
        if (s.StartupCount > ManyStartup)
        {
            list.Add(new HealthFinding(HealthLevel.Notice, "开机启动项偏多",
                $"共 {s.StartupCount} 项。启动项越多，开机后一段时间内越容易卡顿；可以在「开机启动项」页里核对。"));
        }

        
        if (s.ProcessCount > ManyProcesses)
        {
            list.Add(new HealthFinding(HealthLevel.Notice, "后台进程较多",
                $"当前 {s.ProcessCount} 个进程。若其中多数并不需要使用，会持续占用内存与处理器。"));
        }

        
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
