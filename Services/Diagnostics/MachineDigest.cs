using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClassSoftwareHub.Desktop.Services.Diagnostics;

/// <summary>
/// 把 <see cref="MachineSnapshot"/> 压成**给模型看的那点字**（2026-10-18）。
///
/// <para>
/// 为什么要有这一层：原始快照里 293 个进程、147 个软件、6 块盘，直接序列化出去是几万 token，
/// 又贵又慢，而且模型会被噪声淹没。这里做三件事 ——
/// <b>限量</b>（样本只留几十条）、<b>统一单位</b>（全用 GB/MB，不混）、<b>固定字段</b>（每次结构一样，
/// 模型不用猜这次给的是哪种格式）。
/// </para>
/// <para>
/// ⚠️ 输出长度必须可控。目标是 400~700 字，超了就是样本给多了。
/// ⛔ 不要在这里加"建议"——判断属于 <see cref="MachineDiagnosis"/> 和模型，采集/摘要只管陈述。
/// </para>
/// </summary>
public static class MachineDigest
{
    /// <summary>给模型的软件名样本上限。给多了没用，模型记不住也读不完。</summary>
    private const int AppSampleLimit = 30;

    /// <summary>生成体检单纯文本（直接拼进 system prompt）。</summary>
    /// <param name="includeInstalledApps">
    /// 是否把**已装软件清单**发出去。<b>默认 false</b>，理由见方法注释。
    /// </param>
    /// <remarks>
    /// ⚠️⚠️ <b>已装软件清单默认不发送</b>（2026-10-18 实测发现的问题）：
    /// 清单里会出现游戏名、私人工具、公司内部软件，能直接推断出使用者身份和喜好，
    /// 而这份文本是发给**第三方模型服务商**的。只发数量（"已安装 147 款"）就够用了，
    /// 模型判断"装机是否臃肿"并不需要知道具体装了什么。
    /// 用户确实要发时，由设置页的「发送范围」显式打开（对应 <c>HubAiSendInstalledApps</c>）。
    /// <para>
    /// AppData 目录名保留发送：它只暴露"哪些软件占空间大"，敏感度低，而对"缓存大户"的判断价值高。
    /// </para>
    /// </remarks>
    public static string ToPrompt(MachineSnapshot s, bool includeInstalledApps = false)
    {
        var sb = new StringBuilder(1024);

        sb.AppendLine($"[本机情况 · {s.CollectedAt:yyyy-MM-dd HH:mm}]");

        // ── 处理器 ──
        if (s.CpuName.Length > 0)
        {
            sb.Append("处理器：").Append(s.CpuName);
            if (s.CpuThreads > 0) sb.Append('，').Append(s.CpuThreads).Append(" 线程");
            if (s.CpuLoadPercent >= 0) sb.Append("，当前占用 ").Append(s.CpuLoadPercent.ToString("0")).Append('%');
            sb.AppendLine();
        }

        // ── 内存 ──
        if (s.MemoryTotalMb > 0)
        {
            sb.Append("内存：").Append(Gb(s.MemoryTotalMb)).Append(" 总计，可用 ")
              .Append(Gb(s.MemoryFreeMb)).Append("（占用 ").Append(s.MemoryLoadPercent).AppendLine("%）");
        }

        // ── 系统 ──
        if (s.OsName.Length > 0)
        {
            sb.Append("系统：").Append(s.OsName);
            if (s.OsDisplayVersion.Length > 0) sb.Append(' ').Append(s.OsDisplayVersion);
            if (s.OsBuild > 0) sb.Append("（构建 ").Append(s.OsBuild).Append('）');
            sb.Append(s.Is64BitOs ? "，64 位" : "，32 位");
            sb.AppendLine();
        }

        // ── 磁盘 ──
        if (s.Disks.Count > 0)
        {
            sb.Append("磁盘：");
            sb.Append(string.Join("；", s.Disks.Take(3).Select(d =>
                $"{d.Drive} 总 {Gb(d.TotalMb)}，可用 {Gb(d.FreeMb)}（{d.UsedPercent}% 已用）")));
            if (s.Disks.Count > 3) sb.Append($"；另有 {s.Disks.Count - 3} 块未列出");
            sb.AppendLine();
        }

        // ── 进程 ──
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

        // ── 启动项 / 软件 ──
        if (s.StartupCount > 0) sb.Append("开机启动项：").Append(s.StartupCount).AppendLine(" 项");

        if (s.InstalledAppCount > 0)
        {
            sb.Append("已安装软件：").Append(s.InstalledAppCount).Append(" 款");
            sb.AppendLine(includeInstalledApps ? "" : "（清单未发送）");
        }

        // ⚠️ 只有显式允许时才列软件名。默认不发 —— 清单能推出使用者身份与喜好，
        //    而这份文本要离开本机。
        if (includeInstalledApps && s.InstalledAppSample.Count > 0)
        {
            sb.Append("已安装软件样本（不完整）：")
              .AppendLine(string.Join("、", s.InstalledAppSample.Take(AppSampleLimit)));
        }

        if (s.AppDataTopFolders.Count > 0)
        {
            sb.Append("AppData 占用较大的目录：")
              .AppendLine(string.Join("、", s.AppDataTopFolders.Take(8)));
        }

        // ── 采集缺口：必须让模型知道，否则它会对着"读不到"编结论 ──
        if (s.Warnings.Count > 0)
        {
            sb.Append("⚠️ 以下项目本次未能读取，不要对它们下判断：")
              .AppendLine(string.Join("、", s.Warnings));
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>把 MB 转成 GB 文本。<c>16303 MB → 15.9 GB</c>。</summary>
    private static string Gb(long mb) => $"{mb / 1024.0:0.#} GB";
}
