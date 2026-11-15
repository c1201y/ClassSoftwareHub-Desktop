using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClassSoftwareHub.Desktop.Services.Diagnostics;















public static class MachineDigest
{
    
    private const int AppSampleLimit = 30;

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    public static string ToPrompt(MachineSnapshot s, bool includeInstalledApps = false)
    {
        var sb = new StringBuilder(1024);

        sb.AppendLine($"[本机情况 · {s.CollectedAt:yyyy-MM-dd HH:mm}]");

        
        if (s.CpuName.Length > 0)
        {
            sb.Append("处理器：").Append(s.CpuName);
            if (s.CpuThreads > 0) sb.Append('，').Append(s.CpuThreads).Append(" 线程");
            if (s.CpuLoadPercent >= 0) sb.Append("，当前占用 ").Append(s.CpuLoadPercent.ToString("0")).Append('%');
            sb.AppendLine();
        }

        
        if (s.MemoryTotalMb > 0)
        {
            sb.Append("内存：").Append(Gb(s.MemoryTotalMb)).Append(" 总计，可用 ")
              .Append(Gb(s.MemoryFreeMb)).Append("（占用 ").Append(s.MemoryLoadPercent).AppendLine("%）");
        }

        
        if (s.OsName.Length > 0)
        {
            sb.Append("系统：").Append(s.OsName);
            if (s.OsDisplayVersion.Length > 0) sb.Append(' ').Append(s.OsDisplayVersion);
            if (s.OsBuild > 0) sb.Append("（构建 ").Append(s.OsBuild).Append('）');
            sb.Append(s.Is64BitOs ? "，64 位" : "，32 位");
            sb.AppendLine();
        }

        
        if (s.Disks.Count > 0)
        {
            sb.Append("磁盘：");
            sb.Append(string.Join("；", s.Disks.Take(3).Select(d =>
                $"{d.Drive} 总 {Gb(d.TotalMb)}，可用 {Gb(d.FreeMb)}（{d.UsedPercent}% 已用）")));
            if (s.Disks.Count > 3) sb.Append($"；另有 {s.Disks.Count - 3} 块未列出");
            sb.AppendLine();
        }

        
        if (s.ProcessCount > 0)
        {
            sb.Append("后台进程：").Append(s.ProcessCount).Append(" 个");
            if (s.TopProcesses.Count > 0)
            {
                sb.Append("，内存占用前三：")
                  .Append(string.Join("、", s.TopProcesses.Take(3)
                      .Select(p => $"{p.Name} {p.WorkingSetMb} MB")));
            }
            sb.AppendLine();
        }

        
        if (s.StartupCount > 0) sb.Append("开机启动项：").Append(s.StartupCount).AppendLine(" 项");

        if (s.InstalledAppCount > 0)
        {
            sb.Append("已安装软件：").Append(s.InstalledAppCount).Append(" 款");
            sb.AppendLine(includeInstalledApps ? "" : "（清单未发送，⛔ 不要据此判断某个软件有没有安装）");
        }

        
        
        if (includeInstalledApps && s.InstalledAppSample.Count > 0)
        {
            
            
            sb.Append("已安装软件样本（共 ").Append(s.InstalledAppCount).Append(" 款，下面只列 ")
              .Append(Math.Min(AppSampleLimit, s.InstalledAppSample.Count))
              .AppendLine(" 项；没列出来的不代表没装，⛔ 不要据此断言某个软件没安装）：")
              .AppendLine(string.Join("、", s.InstalledAppSample.Take(AppSampleLimit)));
        }

        if (s.AppDataTopFolders.Count > 0)
        {
            sb.Append("AppData 占用较大的目录：")
              .AppendLine(string.Join("、", s.AppDataTopFolders.Take(8)));
        }

        
        if (s.Warnings.Count > 0)
        {
            sb.Append("⚠️ 以下项目本次未能读取，不要对它们下判断：")
              .AppendLine(string.Join("、", s.Warnings));
        }

        return sb.ToString().TrimEnd();
    }

    
    private static string Gb(long mb) => $"{mb / 1024.0:0.#} GB";
}
