using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Services;


public sealed record PageFileDrive(
    string Drive,
    string Label,
    int FreeMb,
    int TotalMb,
    PageFilePlan Plan,
    long FileBytes)
{
    
    public string Display => string.IsNullOrWhiteSpace(Label) ? Drive : $"{Drive}  {Label}";

    
    public string FileText => FileBytes > 0 ? AppLog.Size(FileBytes) : "";

    
    public string FreeText => $"可用 {FreeMb:N0} MB";

    
    
    
    
    
    
    
    
    public int UsableMb => FreeMb + (int)Math.Min(int.MaxValue - FreeMb, FileBytes / 1024 / 1024);
}


public sealed record PageFileApplyResult(bool Ok, string? Error, bool Applied = false);















public enum OsVolumeLock
{
    
    Absent,

    
    Locked,

    
    Unlocked,

    
    Unknown,
}


















public static class PageFileService
{
    
    public const string AdminSwitch = "--pagefile-admin";

    
    public const string MemoryKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

    
    
    
    
    
    
    
    
    public const string OsVolumeName = "PagefileOnOsVolume";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    
    public static string SystemDrive =>
        PageFileRules.NormalizeDrive(Path.GetPathRoot(Environment.SystemDirectory));

    

    
    
    
    
    
    
    
    public static List<string> RawEntries()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MemoryKey, writable: false);
            if (key?.GetValue("PagingFiles") is not { } raw) return new List<string>();

            return raw switch
            {
                string[] many => many.Where(s => !string.IsNullOrWhiteSpace(s)).ToList(),
                string single when single.Trim().Length > 0 => new List<string> { single },
                _ => new List<string>(),
            };
        }
        catch (Exception ex)
        {
            AppLog.Exception("pagefile", ex, "读取 PagingFiles");
            return new List<string>();
        }
    }

    
    public static bool IsAutomatic() => PageFileRules.IsAutomatic(RawEntries());

    
    public static List<PageFilePlan> CurrentPlans() =>
        PageFileRules.Parse(RawEntries(), SystemDrive);

    
    
    
    
    public static OsVolumeLock ReadOsVolumeLock()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MemoryKey, writable: false);
            if (key?.GetValue(OsVolumeName) is not { } raw) return OsVolumeLock.Absent;

            return Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture) == 0
                ? OsVolumeLock.Unlocked
                : OsVolumeLock.Locked;
        }
        catch (Exception ex)
        {
            AppLog.Exception("pagefile", ex, "读取 PagefileOnOsVolume");
            return OsVolumeLock.Unknown;
        }
    }

    
    
    
    
    private static void BackupOsVolumeLock()
    {
        try
        {
            var state = ReadOsVolumeLock();
            var file = Path.Combine(AppPaths.DataDir, "pagefile-on-os-volume-backup.json");

            File.WriteAllText(file, JsonSerializer.Serialize(new
            {
                savedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                existed = state != OsVolumeLock.Absent && state != OsVolumeLock.Unknown,
                value = state switch
                {
                    OsVolumeLock.Locked => 1,
                    OsVolumeLock.Unlocked => 0,
                    _ => (int?)null,
                },
                key = @"HKEY_LOCAL_MACHINE\" + MemoryKey,
                name = OsVolumeName,
            }, Json));

            AppLog.Info("pagefile", $"已备份原状态：{state}（{file}）");
        }
        catch (Exception ex)
        {
            
            AppLog.Exception("pagefile", ex, "备份 PagefileOnOsVolume");
        }
    }

    
    
    
    
    
    
    public static List<PageFileDrive> Drives()
    {
        var plans = CurrentPlans();
        var list = new List<PageFileDrive>();

        DriveInfo[] all;
        try { all = DriveInfo.GetDrives(); }
        catch (Exception ex)
        {
            AppLog.Exception("pagefile", ex, "枚举驱动器");
            return list;
        }

        foreach (var info in all)
        {
            try
            {
                if (info.DriveType != DriveType.Fixed || !info.IsReady) continue;

                var drive = PageFileRules.NormalizeDrive(info.Name);
                if (drive.Length == 0) continue;

                var plan = plans.FirstOrDefault(p => p.Drive == drive)
                           ?? new PageFilePlan(drive, PageFileMode.None);

                var freeMb = (int)Math.Min(int.MaxValue, info.AvailableFreeSpace / 1024 / 1024);
                var totalMb = (int)Math.Min(int.MaxValue, info.TotalSize / 1024 / 1024);

                string label;
                try { label = info.VolumeLabel ?? ""; }
                catch { label = ""; }        

                long bytes = 0;
                try
                {
                    var file = new FileInfo($@"{drive}\pagefile.sys");
                    if (file.Exists) bytes = file.Length;
                }
                catch
                {
                    
                }

                list.Add(new PageFileDrive(drive, label, freeMb, totalMb, plan, bytes));
            }
            catch (Exception ex)
            {
                AppLog.Warn("pagefile", $"跳过一个驱动器：{ex.Message}");
            }
        }

        return list.OrderBy(d => d.Drive, StringComparer.Ordinal).ToList();
    }

    
    
    
    
    
    
    
    public static List<PageFilePlan> OrphanPlans()
    {
        var visible = Drives().Select(d => d.Drive).ToHashSet(StringComparer.Ordinal);
        return CurrentPlans().Where(p => !visible.Contains(p.Drive)).ToList();
    }

    
    public static long AllocatedBytes() => Drives().Sum(d => d.FileBytes);

    

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    public static async Task<PageFileApplyResult> ApplyAsync(
        bool automatic,
        IReadOnlyList<PageFilePlan> plans,
        bool plansChanged,
        bool? osVolumeLock,
        CancellationToken ct = default)
    {
        if (plansChanged && !automatic && PageFileRules.ValidateAll(plans) is { } invalid)
            return new PageFileApplyResult(false, invalid);

        var manifest = Path.Combine(Path.GetTempPath(), $"csh-pagefile-{Guid.NewGuid():N}.json");
        var outcome = manifest + ".result";

        try
        {
            await File.WriteAllTextAsync(manifest,
                JsonSerializer.Serialize(new PageFileTask(automatic, plans.ToList(), plansChanged, osVolumeLock), Json), ct);

            AppLog.Step("pagefile", Describe(automatic, plans, plansChanged, osVolumeLock));

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return new PageFileApplyResult(false, "拿不到本程序路径，无法提权。");

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"\"{AdminSwitch}\" \"{manifest}\"",
                UseShellExecute = true,
                Verb = "runas",                       
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var proc = await Task.Run(() => Process.Start(psi), ct);
            if (proc is null) return new PageFileApplyResult(false, "提权进程没有启动。");

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                try { await proc.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    AppLog.Warn("pagefile", "提权写入超时（2 分钟）");
                    return new PageFileApplyResult(false, "提权写入超时，请重试。");
                }
            }

            
            if (!File.Exists(outcome))
                return new PageFileApplyResult(false, "提权进程没有写回结果。");

            var result = JsonSerializer.Deserialize<PageFileResult>(await File.ReadAllTextAsync(outcome, ct));

            if (result is null)
                return new PageFileApplyResult(false, "提权结果无法解析。");

            AppLog.Info("pagefile", result.Ok ? "写入并校验通过（重启后生效）" : "写入未通过校验：" + result.Error);

            return new PageFileApplyResult(result.Ok, result.Error, Applied: true);
        }
        catch (Exception ex)
        {
            
            AppLog.Warn("pagefile", "提权未执行：" + ex.Message);
            return new PageFileApplyResult(false, "没有拿到管理员授权，改动未执行。");
        }
        finally
        {
            try { if (File.Exists(manifest)) File.Delete(manifest); } catch { }
            try { if (File.Exists(outcome)) File.Delete(outcome); } catch { }
        }
    }

    
    private static string Describe(
        bool automatic, IReadOnlyList<PageFilePlan> plans, bool plansChanged, bool? osVolumeLock)
    {
        var parts = new List<string>();

        if (plansChanged)
        {
            parts.Add(automatic
                ? "自动管理所有驱动器"
                : string.Join("，", plans.Where(p => p.Mode != PageFileMode.None).Select(p => $"{p.Drive}={p.SizeText}")));
        }

        if (osVolumeLock is { } lockIt)
            parts.Add(lockIt ? "系统盘锁定=开启" : "系统盘锁定=解除");

        return "应用：" + (parts.Count == 0 ? "（无改动）" : string.Join("；", parts));
    }

    
    
    
    
    
    public static void ApplyElevated(string manifestPath)
    {
        PageFileResult result;

        try
        {
            AppLog.Info("pagefile", $"提权助手启动，清单：{manifestPath}");

            var task = JsonSerializer.Deserialize<PageFileTask>(File.ReadAllText(manifestPath));

            if (task is null)
            {
                result = new PageFileResult(false, "提权清单是空的");
            }
            else
            {
                result = Write(task);
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("pagefile", ex, "提权助手执行");
            result = new PageFileResult(false, ex.Message);
        }

        try
        {
            File.WriteAllText(manifestPath + ".result", JsonSerializer.Serialize(result, Json));
        }
        catch (Exception ex)
        {
            AppLog.Exception("pagefile", ex, "写提权结果");
        }
    }

    private static PageFileResult Write(PageFileTask task)
    {
        try
        {
            if (task.PlansChanged)
            {
                var entries = task.Automatic
                    ? new List<string> { PageFileRules.AutomaticEntry }
                    : PageFileRules.BuildEntries(task.Plans, SystemDrive);

                if (!task.Automatic && entries.Count == 0)
                    return new PageFileResult(false, "目标配置里没有任何分页文件，已拒绝写入。");

                using (var key = Registry.LocalMachine.OpenSubKey(MemoryKey, writable: true))
                {
                    if (key is null) return new PageFileResult(false, "打不开分页文件配置所在的注册表键。");

                    key.SetValue("PagingFiles", entries.ToArray(), RegistryValueKind.MultiString);
                }

                
                var after = PageFileRules.Parse(RawEntries(), SystemDrive);

                var ok = task.Automatic
                    ? IsAutomatic()
                    : PageFileRules.SameAs(task.Plans, after);

                if (!ok)
                    return new PageFileResult(false, "写入后重读的配置与目标不一致，可能被组策略或安全软件拦下。");
            }

            
            
            if (task.OsVolumeLock is { } wantLock)
            {
                BackupOsVolumeLock();

                using (var key = Registry.LocalMachine.OpenSubKey(MemoryKey, writable: true))
                {
                    if (key is null) return new PageFileResult(false, "打不开分页文件配置所在的注册表键。");

                    key.SetValue(OsVolumeName, wantLock ? 1 : 0, RegistryValueKind.DWord);
                }

                var state = ReadOsVolumeLock();
                var locked = state == OsVolumeLock.Locked;

                if (state == OsVolumeLock.Unknown)
                    return new PageFileResult(false, "写入后读不回来，无法确认是否生效。");

                if (locked != wantLock)
                    return new PageFileResult(false, "写入后重读的值与目标不一致，可能被组策略或安全软件拦下。");
            }

            return new PageFileResult(true, null);
        }
        catch (Exception ex)
        {
            AppLog.Exception("pagefile", ex, "写入分页文件配置");
            return new PageFileResult(false, ex.Message);
        }
    }

    
    private sealed record PageFileTask(
        bool Automatic,
        List<PageFilePlan> Plans,
        bool PlansChanged = true,
        bool? OsVolumeLock = null);

    
    private sealed record PageFileResult(bool Ok, string? Error);
}
