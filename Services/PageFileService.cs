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

/// <summary>一个驱动器的现场数据：卷标、空间、当前分页文件配置。</summary>
public sealed record PageFileDrive(
    string Drive,
    string Label,
    int FreeMb,
    int TotalMb,
    PageFilePlan Plan,
    long FileBytes)
{
    /// <summary>列表左边那一段：盘符 + 卷标（没有卷标就只显示盘符）。</summary>
    public string Display => string.IsNullOrWhiteSpace(Label) ? Drive : $"{Drive}  {Label}";

    /// <summary>当前这个分页文件实际占了多少（「文件不存在」时为空）。</summary>
    public string FileText => FileBytes > 0 ? AppLog.Size(FileBytes) : "";

    /// <summary>可用空间那句话。</summary>
    public string FreeText => $"可用 {FreeMb:N0} MB";

    /// <summary>
    /// 可用于分页文件的空间 = 可用空间 + **这个盘上现有分页文件占的大小**。
    /// <para>
    /// ⚠️ 不加上后者会误伤已经配好的盘：那 14 GB 本来就是 pagefile.sys 占的，
    /// 重设大小时系统会重新创建它，这部分空间是能回来的。少算这一步的话，
    /// 「G 盘配了 14336、只剩 1859 可用」会被判成非法 —— 用户什么都没改就报错。
    /// </para>
    /// </summary>
    public int UsableMb => FreeMb + (int)Math.Min(int.MaxValue - FreeMb, FileBytes / 1024 / 1024);
}

/// <summary>一次「应用更改」的结果。</summary>
public sealed record PageFileApplyResult(bool Ok, string? Error, bool Applied = false);

/// <summary>
/// 「分页文件锁定在系统盘」的当前状态，来自 <c>PagefileOnOsVolume</c>。
///
/// <para>
/// 这个值不是微软给用户的手动开关，而是 **BitLocker 在启用时自动写进去的**
/// （微软 KB 929820）：它的本意是防止分页文件被挪到未加密的卷上、把内存内容漏出去。
/// 值 = 1 时，会话管理器**只把系统盘当作分页文件的位置**。
/// </para>
/// <para>
/// 坑在于：BitLocker 关掉之后**这个值会留下**，于是机器上继续维持"系统盘必须有一份分页文件"，
/// 用户在界面上把分页文件全设到 D 盘也不生效 —— 系统会在 C 盘另建一个小的。
/// 2026-11-04 Nick 反馈的就是这一种。
/// </para>
/// </summary>
public enum OsVolumeLock
{
    /// <summary>注册表里没有这个值 —— 系统不干预，各盘按配置走。</summary>
    Absent,

    /// <summary>= 1。只允许系统盘放分页文件。</summary>
    Locked,

    /// <summary>= 0。显式解除。</summary>
    Unlocked,

    /// <summary>读不到（异常）。</summary>
    Unknown,
}


/// <summary>
/// 虚拟内存（分页文件）的读写（2026-10-31，需求清单第 19 条）。
///
/// <para>
/// 只动一个值：<c>PagingFiles</c>（<c>REG_MULTI_SZ</c>）。改它需要管理员权限，
/// 而且**必须重启才生效** —— 系统在启动阶段按这份配置创建（或删除）各盘的 pagefile.sys。
/// </para>
///
/// <para>
/// ⛔ 提权不走 <c>reg.exe</c>，也不用 <c>.reg</c> 文件，而是**让自己以管理员身份重入一次**：
/// 子进程用 <see cref="Registry"/> 直接写 <c>string[]</c>，类型由 API 保证，
/// 不必拼命令、不必转义、不必赌 <c>REG_MULTI_SZ</c> 在命令行里的分隔符写法
/// （那几种写法只能靠"改完看结果"验证，而这里改错要重启才看得见）。
/// 与 <c>StartupItems</c> / <c>LeftoverCleaner</c> 的提权助手是同一套模式。
/// </para>
/// </summary>
public static class PageFileService
{
    /// <summary>提权助手开关。由 <c>App</c> 在启动最前面接管，见 <see cref="ApplyElevated"/>。</summary>
    public const string AdminSwitch = "--pagefile-admin";

    /// <summary>分页文件配置所在的键（<c>PagingFiles</c> 在它下面）。</summary>
    public const string MemoryKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

    /// <summary>
    /// 「System 盘必须有分页文件」那个值。见 <see cref="OsVolumeLock"/>。
    /// <para>
    /// ⛔ 同一个键下的 <c>ExistingPageFiles</c> **一律不要碰**：微软文档写明它反映的是
    /// "当前实际使用位置"，是系统自己维护的结果，手改无效还可能引出新问题（Nick 2026-11-04 提醒）。
    /// 本程序从来只写 <c>PagingFiles</c> 和这一个值。
    /// </para>
    /// </summary>
    public const string OsVolumeName = "PagefileOnOsVolume";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>系统盘盘符，形如 <c>C:</c>。</summary>
    public static string SystemDrive =>
        PageFileRules.NormalizeDrive(Path.GetPathRoot(Environment.SystemDirectory));

    // ── 读 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 注册表里 <c>PagingFiles</c> 的原始条目。读不到返回空表（<b>不是</b> null）。
    /// <para>
    /// 正常是 <c>REG_MULTI_SZ</c>（读出来 <c>string[]</c>）；被手工改成单个 <c>REG_SZ</c>
    /// 的情况也存在，一并兼容 —— 否则这种机器上整页会显示"无分页文件"，而实际上是有的。
    /// </para>
    /// </summary>
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

    /// <summary>是不是「自动管理所有驱动器的分页文件大小」。</summary>
    public static bool IsAutomatic() => PageFileRules.IsAutomatic(RawEntries());

    /// <summary>当前完整的配置（每盘一条）。</summary>
    public static List<PageFilePlan> CurrentPlans() =>
        PageFileRules.Parse(RawEntries(), SystemDrive);

    /// <summary>
    /// 「分页文件锁定在系统盘」这个值（<c>PagefileOnOsVolume</c>，REG_DWORD）。
    /// 读不到返回 <see cref="OsVolumeLock.Unknown"/>，读得到就按 0/1 判。
    /// </summary>
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

    /// <summary>
    /// 改之前把原状态存一份到数据目录。这个值只有「有 / =0 / =1」三种，还原很容易，
    /// 但用户手动改过注册表之后很难想起来原来是多少 —— 留着这条能对照。
    /// </summary>
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
            // 备份失败不阻断写入 —— 值本身就三种，用户还能手动改回来
            AppLog.Exception("pagefile", ex, "备份 PagefileOnOsVolume");
        }
    }

    /// <summary>
    /// 枚举本机的固定磁盘，并附上它当前的分页文件配置。
    /// <para>
    /// 只收 <see cref="DriveType.Fixed"/>：U 盘、光驱、网络盘上放分页文件既不可靠也不常见。
    /// </para>
    /// </summary>
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
                catch { label = ""; }        // 个别卷读卷标会抛，不影响其它字段

                long bytes = 0;
                try
                {
                    var file = new FileInfo($@"{drive}\pagefile.sys");
                    if (file.Exists) bytes = file.Length;
                }
                catch
                {
                    // 读不到大小不影响"配置是什么"这件事
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

    /// <summary>
    /// 注册表里配了、但当前枚举不到的驱动器（盘拔掉了、盘符没分配、或者是未就绪的卷）。
    /// <para>
    /// ⚠️ 写入时这一份要**原样带回去**：<c>PagingFiles</c> 是整体覆盖写的，
    /// 不带回去就等于把这台的配置删了 —— 而用户根本没碰过那一行（界面上也看不到它）。
    /// </para>
    /// </summary>
    public static List<PageFilePlan> OrphanPlans()
    {
        var visible = Drives().Select(d => d.Drive).ToHashSet(StringComparer.Ordinal);
        return CurrentPlans().Where(p => !visible.Contains(p.Drive)).ToList();
    }

    /// <summary>当前已分配的总量（各盘分页文件的实际大小之和）。</summary>
    public static long AllocatedBytes() => Drives().Sum(d => d.FileBytes);

    // ── 写 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 应用一份完整配置。会弹一次 UAC，改完**重读注册表**判定成败（不看子进程退出码）。
    /// </summary>
    /// <param name="automatic">是否改为「自动管理所有驱动器」。</param>
    /// <param name="plans">手动配置时的完整列表（要含 <see cref="OrphanPlans"/> 里的项）。</param>
    /// <param name="plansChanged">
    /// 这次动没动分页文件本身。只改「系统盘锁定」时不写 <c>PagingFiles</c> ——
    /// 那时的 <paramref name="plans"/> 只是当前配置的回读，写一遍没有意义，
    /// 而且用户如果本来就把分页文件全关了，还会撞上"目标配置里没有任何分页文件"的拦截。
    /// </param>
    /// <param name="osVolumeLock">
    /// 要把 <c>PagefileOnOsVolume</c> 设成什么：<c>true</c> = 锁定（1），<c>false</c> = 解除（0），
    /// <c>null</c> = 不动。
    /// </param>
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
                Verb = "runas",                       // 会弹一次 UAC
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

            // 结果以助手写回的文件为准；没有文件 = 助手没跑完
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
            // 用户点「否」也走这里（Win32Exception: 操作已被用户取消）
            AppLog.Warn("pagefile", "提权未执行：" + ex.Message);
            return new PageFileApplyResult(false, "没有拿到管理员授权，改动未执行。");
        }
        finally
        {
            try { if (File.Exists(manifest)) File.Delete(manifest); } catch { }
            try { if (File.Exists(outcome)) File.Delete(outcome); } catch { }
        }
    }

    /// <summary>一行日志，说清这次要写什么。</summary>
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

    // ── 提权助手（在管理员令牌下跑在另一个进程里） ──────────────────────
    /// <summary>
    /// 由 <c>App</c> 在启动最前面接管：读清单 → 写注册表 → 重读校验 → 写回结果文件 → 退出。
    /// ⛔ 不建界面、不碰单实例、不弹窗。
    /// </summary>
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

                // 成败按**重读出来的配置**判定：写入调用不抛异常 ≠ Windows 采纳了
                var after = PageFileRules.Parse(RawEntries(), SystemDrive);

                var ok = task.Automatic
                    ? IsAutomatic()
                    : PageFileRules.SameAs(task.Plans, after);

                if (!ok)
                    return new PageFileResult(false, "写入后重读的配置与目标不一致，可能被组策略或安全软件拦下。");
            }

            // 解除（或恢复）系统盘的锁定。⚠️ 这一项和上面的 PagingFiles 是两件事：
            // 上面决定"哪些盘要分页文件"，这一项决定"系统允不允许不放在系统盘"。
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

    /// <summary>提权清单。显式字段名，避免缩略化之后两边对不上。</summary>
    private sealed record PageFileTask(
        bool Automatic,
        List<PageFilePlan> Plans,
        bool PlansChanged = true,
        bool? OsVolumeLock = null);

    /// <summary>提权结果。</summary>
    private sealed record PageFileResult(bool Ok, string? Error);
}
