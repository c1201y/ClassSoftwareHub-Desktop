using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>一个系统功能的开关状态。<see cref="Unknown"/> = 读不到（无权 / 键不存在），别当成"关"。</summary>
public enum FeatureState
{
    Unknown,
    On,
    Off,
}

/// <summary>一条分页文件（虚拟内存）配置。</summary>
/// <param name="Raw">注册表里的原始字符串，例如 <c>C:\pagefile.sys 0 0</c>。</param>
/// <param name="Path">分页文件路径。</param>
/// <param name="MinMb">初始大小（MB）；0 且 <paramref name="MaxMb"/> 也为 0 时表示由系统管理。</param>
/// <param name="MaxMb">最大大小（MB）。</param>
/// <param name="CurrentBytes">该文件此刻的实际大小；0 = 没有这个文件。</param>
public sealed record PageFileEntry(string Raw, string Path, int MinMb, int MaxMb, long CurrentBytes)
{
    /// <summary>是不是"由系统管理大小"（注册表里写成 <c>0 0</c>）。</summary>
    public bool SystemManaged => MinMb == 0 && MaxMb == 0;

    public string SizeText => CurrentBytes > 0 ? AppLog.Size(CurrentBytes) : "文件不存在";

    /// <summary>界面上那句"当前占用 …"（SettingsCard 的 Description）。</summary>
    public string OccupancyText => CurrentBytes > 0
        ? $"当前占用 {AppLog.Size(CurrentBytes)}"
        : "文件不存在（配置有，但文件没建出来）";

    public string ConfigText => SystemManaged
        ? $"由系统管理（{Path}）"
        : $"自定义 {MinMb} – {MaxMb} MB（{Path}）";
}

/// <summary>
/// 系统功能：休眠、快速启动、分页文件（2026-10-30，对应需求清单第 20 条，第 19 条先做只读）。
///
/// <para>
/// 三条边界：
/// <list type="number">
///   <item><b>状态一律从注册表读</b>，不解析 <c>powercfg /a</c> 的输出 —— 那份输出跟系统语言走，
///         中文系统里全是"待机 (S3)"这类字样，按文字匹配必然出岔子（项目里踩过一次，
///         见 <c>BrightnessService</c> 的注释）；</item>
///   <item><b>写入后才算数</b>：命令返回 0 不等于生效，改完重新读一遍注册表，界面按实际状态显示
///         （与 <c>FirewallGateway.Repair</c> 同一原则，那次吃过"日志写成功、规则其实没建"的亏）；</item>
///   <item><b>一次改动一次 UAC</b>：用 <c>Verb=runas</c> 起子进程，不常驻、不提权自身
///         （本程序是普通权限运行的，提权自身会带来一堆权限/路径问题）。</item>
/// </list>
/// </para>
///
/// <para>
/// ⛔ 分页文件**只读**：改它要管理员、要重建文件、还要重启才生效，误设成 0 会让系统在内存吃紧时直接崩进程。
/// 这一版只把现状摆清楚（配置、实际大小、是不是由系统管理），写入按需求清单第 19 条单独排期。
/// </para>
/// </summary>
public static class SystemFeatures
{
    private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Power";
    private const string BootPowerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
    private const string MemoryKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

    /// <summary>休眠是否开启（<c>HibernateEnabled</c>）。</summary>
    public static FeatureState Hibernate() =>
        ReadDword(Registry.LocalMachine, PowerKey, "HibernateEnabled");

    /// <summary>快速启动是否开启（<c>HiberbootEnabled</c>）。⚠️ 休眠关掉时它不可能为开。</summary>
    public static FeatureState FastStartup() =>
        ReadDword(Registry.LocalMachine, BootPowerKey, "HiberbootEnabled");

    /// <summary>休眠文件当前大小（字节）。0 = 文件不存在。</summary>
    public static long HibernateFileBytes()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))
                       ?? @"C:\";
            var info = new FileInfo(Path.Combine(root, "hiberfil.sys"));
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex)
        {
            AppLog.Warn("sysfeatures", "读取休眠文件大小失败：" + ex.Message);
            return 0;
        }
    }

    /// <summary>开关休眠。返回**实际生效后**的状态（不是"命令跑过了"）。</summary>
    public static async Task<FeatureState> SetHibernateAsync(bool on)
    {
        await RunElevatedAsync("powercfg.exe", $"/hibernate {(on ? "on" : "off")}");
        return Hibernate();
    }

    /// <summary>
    /// 开关快速启动。
    /// ⚠️ 它靠休眠文件工作：休眠关着的时候写这个值，系统不会真的快速启动 ——
    /// 所以调用方要在界面上说明（这里只能如实返回状态）。
    /// </summary>
    public static async Task<FeatureState> SetFastStartupAsync(bool on)
    {
        await RunElevatedAsync("reg.exe",
            $"add \"HKLM\\{BootPowerKey}\" /v HiberbootEnabled /t REG_DWORD /d {(on ? 1 : 0)} /f");

        return FastStartup();
    }

    /// <summary>读分页文件配置（虚拟内存）。读不到就返回空列表，界面显示"未配置"。</summary>
    public static List<PageFileEntry> PageFiles()
    {
        var list = new List<PageFileEntry>();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MemoryKey, writable: false);
            if (key is null) return list;

            // 正常是 REG_MULTI_SZ（读出来 string[]）；被手工改过时可能是单个 REG_SZ，一起兼容
            var raw = key.GetValue("PagingFiles");
            string[] entries = raw switch
            {
                string[] many => many,
                string single when single.Trim().Length > 0 => new[] { single },
                _ => Array.Empty<string>(),
            };

            foreach (var entry in entries)
            {
                var text = (entry ?? "").Trim();
                if (text.Length == 0) continue;

                // 形如 `C:\pagefile.sys 4096 8192`；段数可能少于 3（老配置或手工写的）
                var bits = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                var path = bits.Length > 0 ? bits[0] : text;

                var min = bits.Length > 1 && int.TryParse(bits[1], out var a) ? a : 0;
                var max = bits.Length > 2 && int.TryParse(bits[2], out var b) ? b : 0;

                long bytes = 0;
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists) bytes = info.Length;
                }
                catch
                {
                    // 读不到大小不影响"配置是什么"这件事
                }

                list.Add(new PageFileEntry(text, path, min, max, bytes));
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("sysfeatures", ex, "读取分页文件配置");
        }

        return list;
    }

    // ── 内部 ─────────────────────────────────────────────────────────────

    private static FeatureState ReadDword(RegistryKey root, string path, string name)
    {
        try
        {
            using var key = root.OpenSubKey(path, writable: false);
            if (key?.GetValue(name) is not int value) return FeatureState.Unknown;
            return value == 0 ? FeatureState.Off : FeatureState.On;
        }
        catch (Exception ex)
        {
            AppLog.Exception("sysfeatures", ex, $"读取 {name}");
            return FeatureState.Unknown;
        }
    }

    /// <summary>
    /// 跑一条需要管理员的命令。
    /// <para>
    /// ⚠️ <c>Verb=runas</c> 时**不要**依赖 <c>ExitCode</c>：经 Shell 启动的进程拿不到可靠退出码，
    /// 而且用户点了「否」和命令执行失败在代码里长得一样。一律改成"看改完之后的状态"。
    /// </para>
    /// ⚠️ 用户点「否」会抛 <see cref="System.ComponentModel.Win32Exception"/>（操作已被用户取消），
    ///    这里吞掉并记一条日志，由调用方按实际状态如实显示。
    /// </summary>
    private static async Task RunElevatedAsync(string file, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(file, arguments)
            {
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                AppLog.Warn("sysfeatures", $"未能启动 {file}");
                return;
            }

            await proc.WaitForExitAsync();
            AppLog.Info("sysfeatures", $"已执行：{Path.GetFileName(file)} {arguments}");
        }
        catch (Exception ex)
        {
            // 用户拒绝授权也走这里
            AppLog.Warn("sysfeatures", $"命令未执行（{Path.GetFileName(file)}）：{ex.Message}");
        }
    }
}
