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


public enum FeatureState
{
    Unknown,
    On,
    Off,
}







public sealed record PageFileEntry(string Raw, string Path, int MinMb, int MaxMb, long CurrentBytes)
{
    
    public bool SystemManaged => MinMb == 0 && MaxMb == 0;

    public string SizeText => CurrentBytes > 0 ? AppLog.Size(CurrentBytes) : "文件不存在";

    
    public string OccupancyText => CurrentBytes > 0
        ? $"当前占用 {AppLog.Size(CurrentBytes)}"
        : "文件不存在（配置有，但文件没建出来）";

    public string ConfigText => SystemManaged
        ? $"由系统管理（{Path}）"
        : $"自定义 {MinMb} – {MaxMb} MB（{Path}）";
}





public sealed record SysApplyResult(int Requested, IReadOnlyList<string> Failed, string? Error)
{
    public bool Ok => Error is null;

    public int Matched => Requested - Failed.Count;

    public bool AllMatched => Failed.Count == 0;
}
























public static class SystemFeatures
{
    private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Power";
    private const string BootPowerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
    private const string MemoryKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const string PowerSchemesKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
    private const string PowerSettingsKey = @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings";

    
    public static FeatureState Hibernate() =>
        ReadDword(Registry.LocalMachine, PowerKey, "HibernateEnabled");

    
    public static FeatureState FastStartup() =>
        ReadDword(Registry.LocalMachine, BootPowerKey, "HiberbootEnabled");

    
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
            _ => null,   
        };
    }

    
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

    

    
    
    
    public static async Task<SysApplyResult> ApplyAsync(IReadOnlyList<(SysFeatureDef Def, int Value)> changes)
    {
        if (changes.Count == 0) return new SysApplyResult(0, Array.Empty<string>(), null);

        var script = SysFeatureRules.BuildScript(changes);
        var path = Path.Combine(Path.GetTempPath(), $"csh-sys-{Guid.NewGuid():N}.cmd");

        try
        {
            
            await File.WriteAllTextAsync(path, script, Encoding.Default);

            AppLog.Step("sysfeatures", $"应用 {changes.Count} 项：{string.Join("、", changes.Select(c => c.Def.Id))}");

            var started = await RunElevatedAsync("cmd.exe", $"/c \"{path}\"");
            if (!started)
            {
                return new SysApplyResult(changes.Count, Array.Empty<string>(),
                    "没有拿到管理员授权，改动未执行。");
            }

            
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

    
    public static List<PageFileEntry> PageFiles()
    {
        var list = new List<PageFileEntry>();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MemoryKey, writable: false);
            if (key is null) return list;

            
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
            
            AppLog.Warn("sysfeatures", $"提权命令未执行（{Path.GetFileName(file)}）：{ex.Message}");
            return false;
        }
    }
}
