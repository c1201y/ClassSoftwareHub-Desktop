using System;
using System.Collections.Generic;

namespace ClassSoftwareHub.Desktop.Services.Diagnostics;














public sealed class MachineSnapshot
{
    
    public string CpuName { get; init; } = "";

    
    public int CpuThreads { get; init; }

    
    public int CpuCores { get; init; }

    
    public int CpuMhz { get; init; }

    
    public double CpuLoadPercent { get; init; } = -1;

    
    public long MemoryTotalMb { get; init; }

    
    public long MemoryFreeMb { get; init; }

    
    public int MemoryLoadPercent { get; init; }

    
    public string OsName { get; init; } = "";

    
    public int OsBuild { get; init; }

    
    public string OsDisplayVersion { get; init; } = "";

    public bool Is64BitOs { get; init; }

    
    public List<DiskSnapshot> Disks { get; init; } = new();

    
    public int ProcessCount { get; init; }

    
    public List<ProcessSnapshot> TopProcesses { get; init; } = new();

    
    public int StartupCount { get; init; }

    
    public int InstalledAppCount { get; init; }

    
    public List<string> InstalledAppSample { get; init; } = new();

    
    public List<string> AppDataTopFolders { get; init; } = new();

    
    
    
    
    public List<string> Warnings { get; init; } = new();

    
    public long ElapsedMs { get; init; }

    public DateTime CollectedAt { get; init; } = DateTime.Now;

    
    public bool IsEmpty => CpuName.Length == 0 && MemoryTotalMb == 0 && Disks.Count == 0;
}


public sealed class DiskSnapshot
{
    public string Drive { get; init; } = "";
    public string Label { get; init; } = "";
    public string Format { get; init; } = "";
    public long TotalMb { get; init; }
    public long FreeMb { get; init; }

    
    public int UsedPercent => TotalMb <= 0 ? 0 : (int)Math.Round((TotalMb - FreeMb) * 100.0 / TotalMb);
}


public sealed class ProcessSnapshot
{
    public string Name { get; init; } = "";
    public long WorkingSetMb { get; init; }
}
