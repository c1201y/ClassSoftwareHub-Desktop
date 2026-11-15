using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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

    /// <summary>界面上那句"当前占用 …"。</summary>
    public string OccupancyText => CurrentBytes > 0
        ? $"当前占用 {AppLog.Size(CurrentBytes)}"
        : "文件不存在（配置有，但文件没建出来）";

    public string ConfigText => SystemManaged
        ? $"由系统管理（{Path}）"
        : $"自定义 {MinMb} – {MaxMb} MB（{Path}）";
}

/// <summary>一次「应用更改」的结果。</summary>
/// <param name="Requested">本次提交了几项。</param>
/// <param name="Failed">改完重读后仍与目标不一致的项 Id。</param>
/// <param name="Error">整批失败的原因（提权被拒、脚本没跑起来）；单项没生效不算在这里。</param>
public sealed record SysApplyResult(int Requested, IReadOnlyList<string> Failed, string? Error)
{
    public bool Ok => Error is null;

    public int Matched => Requested - Failed.Count;

    public bool AllMatched => Failed.Count == 0;
}

/// <summary>
/// 系统功能：HKLM 与 powercfg 的读取、批量写入（2026-10-31 重做，需求清单第 20 条）。
///
/// <para>
/// 四条边界：
/// <list type="number">
///   <item><b>状态一律从注册表读</b>，不解析 <c>powercfg /a</c> 或 <c>/query</c> 的输出 ——
///         那份输出跟系统语言走，中文系统里全是"待机 (S3)"这类字样，按文字匹配必然出岔子
///         （项目里踩过一次，见 <c>BrightnessService</c> 的注释）；</item>
///   <item><b>写入后才算数</b>：命令返回 0 不等于生效，整批跑完重新读一遍注册表，
///         界面按实际状态显示（与 <c>FirewallGateway.Repair</c> 同一原则）；</item>
///   <item><b>整批一次 UAC</b>：所有改动拼成一个临时脚本，用 <c>Verb=runas</c> 跑一次
///         （2026-10-31 Nick 定「排队 + 一次弹」）。逐项提权的写法已经去掉 ——
///         十几项逐个弹窗，用户点到第五次就放弃了；</item>
///   <item><b>不提权自身</b>：本程序保持普通权限运行，提权只发生在那个临时脚本上。</item>
/// </list>
/// </para>
///
/// <para>
/// ⛔ 分页文件仍然是**只读**：改它要重建文件并重启，误设成 0 会让系统在内存吃紧时直接杀进程。
/// 页面已按 Nick 2026-10-31 的决定把这一项撤下，写入逻辑留到单独的分页文件页再做。
/// </para>
/// </summary>
public static class SystemFeatures
{
    private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Power";
    private const string BootPowerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
    private const string MemoryKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const string PowerSchemesKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
    private const string PowerSettingsKey = @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings";

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

    // ── 读 ───────────────────────────────────────────────────────────────

    /// <summary>读一个 HKLM 下的 DWORD；键或值不存在返回 <c>null</c>（≠ 0）。</summary>
    public static int? ReadRegistryInt(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path, writable: false);
            return key?.GetValue(name) is int value ? value : null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("sysfeatures", $"读取 {path}\\{name} 失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 读当前电源方案里某一项设置的交流电取值（秒数 / 枚举值）。
    ///
    /// <para>
    /// 三级回落（2026-10-31 实测：本机"关闭硬盘""合盖"在活动方案下根本没有这个值）：
    /// <list type="number">
    ///   <item>活动方案 → 子组 → 设置 → <c>ACSettingIndex</c>；</item>
    ///   <item>同上但取 <c>DCSettingIndex</c>（台式机没有交流电差异时常见）；</item>
    ///   <item><c>PowerSettings\子组\设置\DefaultPowerSchemeValues\ACSettingIndex</c>（系统默认值）。</item>
    /// </list>
    /// 三级都读不到才返回 <c>null</c>。
    /// </para>
    /// </summary>
    public static int? ReadPowerSetting(string subGroup, string setting)
    {
        try
        {
            var active = ReadString(PowerSchemesKey, "ActivePowerScheme");
            if (!string.IsNullOrEmpty(active))
            {
                var path = $@"{PowerSchemesKey}\{active.Trim('{', '}')}\{subGroup}\{setting}";

                using var key = Registry.LocalMachine.OpenSubKey(path, writable: false);
                if (key?.GetValue("ACSettingIndex") is int ac) return ac;
                if (key?.GetValue("DCSettingIndex") is int dc) return dc;

                // 有的系统上子键名带大括号，再试一次
                using var key2 = Registry.LocalMachine.OpenSubKey(
                    $@"{PowerSchemesKey}\{{{active.Trim('{', '}')}}}\{subGroup}\{setting}", writable: false);
                if (key2?.GetValue("ACSettingIndex") is int ac2) return ac2;
                if (key2?.GetValue("DCSettingIndex") is int dc2) return dc2;
            }

            using var fallback = Registry.LocalMachine.OpenSubKey(
                $@"{PowerSettingsKey}\{subGroup}\{setting}\DefaultPowerSchemeValues", writable: false);
            if (fallback?.GetValue("ACSettingIndex") is int def) return def;
        }
        catch (Exception ex)
        {
            AppLog.Warn("sysfeatures", $"读取电源设置 {setting} 失败：{ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// 当前电源计划：0 = 平衡、1 = 高性能、2 = 节能；识别不出来返回 <c>null</c>。
    /// </summary>
    public static int? ReadActivePlan()
    {
        var active = ReadString(PowerSchemesKey, "ActivePowerScheme");
        if (string.IsNullOrEmpty(active)) return null;

        var guid = active.Trim('{', '}').ToLowerInvariant();

        return guid switch
        {
            Data.SysFeatures.SystemFeatureCatalog.PlanHighPerf => 1,
            Data.SysFeatures.SystemFeatureCatalog.PlanSaver => 2,
            Data.SysFeatures.SystemFeatureCatalog.PlanBalanced => 0,
            _ => null,   // 用户自建的方案：不认识就如实返回 null
        };
    }

    /// <summary>读一个 REG_SZ。</summary>
    private static string? ReadString(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path, writable: false);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex)
        {
            AppLog.Warn("sysfeatures", $"读取 {path}\\{name} 失败：{ex.Message}");
            return null;
        }
    }

    // ── 写 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 应用一批改动：拼脚本 → 一次 UAC → 逐项重读核对。
    /// </summary>
    public static async Task<SysApplyResult> ApplyAsync(IReadOnlyList<(SysFeatureDef Def, int Value)> changes)
    {
        if (changes.Count == 0) return new SysApplyResult(0, Array.Empty<string>(), null);

        var script = SysFeatureRules.BuildScript(changes);
        var path = Path.Combine(Path.GetTempPath(), $"csh-sys-{Guid.NewGuid():N}.cmd");

        try
        {
            // cmd 在中文系统上按 GBK 读脚本最稳；内容本来就全是 ASCII，加不加 BOM 都不影响
            await File.WriteAllTextAsync(path, script, Encoding.Default);

            AppLog.Step("sysfeatures", $"应用 {changes.Count} 项：{string.Join("、", changes.Select(c => c.Def.Id))}");

            var started = await RunElevatedAsync("cmd.exe", $"/c \"{path}\"");
            if (!started)
            {
                return new SysApplyResult(changes.Count, Array.Empty<string>(),
                    "没有拿到管理员授权，改动未执行。");
            }

            // 成败一律按改完重读判定，不看命令返回值（runas 拿不到可靠的 ExitCode）
            var failed = SysFeatureRules.FailedAfterApply(changes);

            AppLog.Info("sysfeatures", failed.Count == 0
                ? $"全部生效（{changes.Count} 项）"
                : $"有 {failed.Count} 项未生效：{string.Join("、", failed)}");

            return new SysApplyResult(changes.Count, failed, null);
        }
        catch (Exception ex)
        {
            AppLog.Exception("sysfeatures", ex, "应用系统功能");
            return new SysApplyResult(changes.Count, Array.Empty<string>(), ex.Message);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    /// <summary>读分页文件配置。页面已撤下这一项，留给之后单独的分页文件页。</summary>
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
    /// 以管理员身份跑一条命令，返回**是否真的启动了**（不代表命令成功）。
    /// <para>
    /// ⚠️ <c>Verb=runas</c> 时拿不到可靠的 <c>ExitCode</c>，而且用户点「否」和命令执行失败
    /// 在代码里长得一样。所以这里只回答"有没有起来"，成败交给调用方重读注册表。
    /// </para>
    /// </summary>
    private static async Task<bool> RunElevatedAsync(string file, string arguments)
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
            if (proc is null) return false;

            await proc.WaitForExitAsync();
            return true;
        }
        catch (Exception ex)
        {
            // 用户点「否」走这里（Win32Exception: 操作已被用户取消）
            AppLog.Warn("sysfeatures", $"提权命令未执行（{Path.GetFileName(file)}）：{ex.Message}");
            return false;
        }
    }
}
