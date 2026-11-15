using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>一个开机启动位置的存放形式。</summary>
public enum StartupSourceKind
{
    /// <summary>注册表 <c>Run</c> 键下的一个值。</summary>
    Registry,

    /// <summary>启动文件夹里的一个文件（通常是 <c>.lnk</c>）。</summary>
    Folder,
}

/// <summary>
/// 一个开机启动位置。
/// <para>
/// ⛔⛔ <b>「禁用」不删值、不动文件</b> —— 只写 <paramref name="ApprovalPath"/> 下那 12 字节的状态位。
/// 这是 Windows 自己（任务管理器 / 设置 → 应用 → 启动）用的同一套机制，所以三边状态天然互通；
/// 而"删除值再自己备份"的做法会让任务管理器看不懂我们的禁用，也会跟软件自愈行为打架。
/// </para>
/// <para>
/// 状态位格式（社区长期沿用、本机实测一致）：<c>[状态字节][00 00 00][8 字节 FILETIME]</c>，
/// <c>0x02</c> = 启用、<c>0x03</c> = 禁用。<c>StartupApproved</c> 里没有这条记录 = 默认启用。
/// </para>
/// </summary>
/// <param name="Id">稳定标识，用于界面与提权清单之间对应。</param>
/// <param name="Label">界面上显示的来源名。</param>
/// <param name="ItemsPath">注册表键路径 或 文件夹路径。</param>
/// <param name="ApprovalPath">对应 <c>StartupApproved</c> 键路径。</param>
/// <param name="NeedsAdmin">是否为"所有用户"级（写状态位要管理员）。</param>
public sealed record StartupSource(
    string Id,
    string Label,
    StartupSourceKind Kind,
    string ItemsPath,
    string ApprovalPath,
    bool NeedsAdmin);

/// <summary>一条开机启动项（扫描结果里的一项）。</summary>
/// <param name="Name">注册表值名 或 启动文件夹里的文件名。</param>
/// <param name="Command">该项实际会执行的命令（启动文件夹项取快捷方式的目标）。</param>
/// <param name="Missing">命令行指向的文件已经不存在 —— 多半是软件卸载后留下的死项。</param>
public sealed record StartupItem(
    string Name,
    string Command,
    string SourceId,
    string SourceLabel,
    bool Enabled,
    bool NeedsAdmin,
    bool Missing)
{
    /// <summary>同一条目的稳定键：来源 + 名称（Windows 这两处都不区分大小写）。</summary>
    public string Key => SourceId + "|" + Name.ToLowerInvariant();
}

/// <summary>
/// 开机启动项的扫描与启停。
///
/// <para>
/// ⛔ <b>只列 4 个位置</b>：注册表 <c>Run</c>（当前用户 / 所有用户 / 32 位）+ 启动文件夹（当前用户 / 所有用户）。
/// <b>故意不列 <c>RunOnce</c></b> —— 那是"执行一次就自动消失"的一次性项，没有"开机自启"的含义，
/// 列出来只会让用户以为关不掉；也不列服务与计划任务（各自是独立的大功能）。
/// </para>
/// <para>
/// ⛔ <c>StartupApproved</c> 里会有一些<b>孤儿记录</b>：软件卸载了但状态位还留着（本机实测就有
/// Adobe / 钉钉 / 百度网盘 几条）。它们<b>不显示</b> —— 对应的程序都没了，列出来是噪音。
/// </para>
/// </summary>
public static class StartupItems
{
    /// <summary>提权助手开关。由 <c>App</c> 在启动最前面接管，见 <see cref="ApplyElevated"/>。</summary>
    public const string AdminSwitch = "--startup-admin";

    private static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string ProgramDataDir => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    private const string ExplorerKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>
    /// 扫描位置。⚠️ <b>顺序有意义</b>：界面按这个顺序分组排列，而且它固定不变 ——
    /// 排序若受"启用/禁用"影响，用户拨一下开关整行就会跳位置，那是很糟的手感。
    /// </summary>
    public static readonly StartupSource[] Sources =
    {
        new("hkcu-run", "当前用户 · 注册表", StartupSourceKind.Registry,
            @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            @"HKCU\" + ExplorerKey + @"\Run",
            NeedsAdmin: false),

        new("hklm-run", "所有用户 · 注册表", StartupSourceKind.Registry,
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            @"HKLM\" + ExplorerKey + @"\Run",
            NeedsAdmin: true),

        // ⚠️ 32 位视图的 Run 对应 StartupApproved 下的 Run32（64 位的是 Run）。
        new("hklm-run32", "所有用户 · 注册表（32 位）", StartupSourceKind.Registry,
            @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
            @"HKLM\" + ExplorerKey + @"\Run32",
            NeedsAdmin: true),

        new("user-startup", "当前用户 · 启动文件夹", StartupSourceKind.Folder,
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            @"HKCU\" + ExplorerKey + @"\StartupFolder",
            NeedsAdmin: false),

        new("common-startup", "所有用户 · 启动文件夹", StartupSourceKind.Folder,
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            @"HKLM\" + ExplorerKey + @"\StartupFolder",
            NeedsAdmin: true),
    };

    public static StartupSource? SourceOf(string id) => Sources.FirstOrDefault(s => s.Id == id);

    // ══════════════════════════════════════════════════════════════════════════
    //  扫描
    // ══════════════════════════════════════════════════════════════════════════

    public static Task<List<StartupItem>> ScanAsync(CancellationToken ct)
        => Task.Run(() => Scan(ct), ct);

    private static List<StartupItem> Scan(CancellationToken ct)
    {
        var list = new List<StartupItem>();

        foreach (var src in Sources)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (src.Kind == StartupSourceKind.Registry) ScanRegistry(src, list);
                else ScanFolder(src, list);
            }
            catch (Exception ex)
            {
                // 一个位置读不动（安全软件挂钩、权限不足）不该拖垮整次扫描
                AppLog.Exception("startup", ex, $"扫描开机启动项（{src.Label}）");
            }
        }

        // 按来源分组、组内按名字。⛔ 不含"是否启用"——见 Sources 的注释。
        var order = Sources.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i);
        return list
            .OrderBy(i => order.TryGetValue(i.SourceId, out var n) ? n : int.MaxValue)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void ScanRegistry(StartupSource src, List<StartupItem> sink)
    {
        if (!RegistryPath.TryParse(src.ItemsPath, out var hive, out var sub)) return;

        var approval = ReadApproval(src.ApprovalPath);

        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using var key = root.OpenSubKey(sub, writable: false);
        if (key is null) return;

        foreach (var name in key.GetValueNames())
        {
            if (name.Length == 0) continue;   // "(默认)"值不算

            // ⚠️ 原样取出、**不展开环境变量** —— 界面上要显示的就是用户/安装器当初写进去的那串东西
            var command = key.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";

            sink.Add(new StartupItem(
                Name: name,
                Command: command,
                SourceId: src.Id,
                SourceLabel: src.Label,
                Enabled: IsEnabled(approval, name),
                NeedsAdmin: src.NeedsAdmin,
                Missing: CommandSeemsMissing(command)));
        }
    }

    private static void ScanFolder(StartupSource src, List<StartupItem> sink)
    {
        if (!Directory.Exists(src.ItemsPath)) return;

        var approval = ReadApproval(src.ApprovalPath);

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(src.ItemsPath).ToList();
        }
        catch (Exception ex)
        {
            AppLog.Info("startup", $"列启动文件夹失败（{src.ItemsPath}）：{ex.GetType().Name}");
            return;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

            var target = ResolveShortcut(file);
            var command = target.Length > 0 ? target : file;

            sink.Add(new StartupItem(
                Name: name,
                Command: command,
                SourceId: src.Id,
                SourceLabel: src.Label,
                Enabled: IsEnabled(approval, name),
                NeedsAdmin: src.NeedsAdmin,
                // 快捷方式解析不出来时（target 为空）**不判死** —— 拿不到目标就没有依据说它丢了
                Missing: target.Length > 0 && CommandSeemsMissing(target)));
        }
    }

    /// <summary>
    /// 读一个 <c>StartupApproved</c> 键，返回「值名 → 是否启用」。
    /// ⛔ 键不存在、或某条记录格式不认识 ⇒ 都当<b>启用</b>（系统默认行为），别反过来猜成禁用。
    /// </summary>
    private static Dictionary<string, bool> ReadApproval(string approvalPath)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        if (!RegistryPath.TryParse(approvalPath, out var hive, out var sub)) return map;

        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = root.OpenSubKey(sub, writable: false);
            if (key is null) return map;

            foreach (var name in key.GetValueNames())
            {
                if (name.Length == 0) continue;
                if (key.GetValue(name) is not byte[] { Length: > 0 } bytes) continue;

                // 本机实测：0x02 = 启用、0x03 = 禁用，另有系统托管的 0x06（也已在运行）。
                // 规律是**最低位**：奇数 = 禁用。⛔ 别写成 `== 0x03`，那样 0x06 会被误判成禁用。
                map[name] = (bytes[0] & 0x01) == 0;
            }
        }
        catch (Exception ex)
        {
            AppLog.Info("startup", $"读启动状态失败（{approvalPath}）：{ex.GetType().Name}");
        }

        return map;
    }

    private static bool IsEnabled(Dictionary<string, bool> approval, string name)
        => !approval.TryGetValue(name, out var enabled) || enabled;

    // ══════════════════════════════════════════════════════════════════════════
    //  启用 / 禁用
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 切换一项的启停。返回空串 = 成功；否则是给用户看的一句话原因。
    /// <para>「所有用户」级的项普通权限写不动，会自动提权补刀（弹一次 UAC）。</para>
    /// </summary>
    public static async Task<string> SetEnabledAsync(StartupItem item, bool enabled, CancellationToken ct)
    {
        var src = SourceOf(item.SourceId);
        if (src is null) return "找不到这一项的来源位置";

        // 先按普通权限试。应用本身若正好是提权运行的，这一步就成了，不用多弹 UAC。
        if (TryApply(src, item.Name, enabled, out var note))
        {
            Log(src, item.Name, enabled);
            return "";
        }

        if (!src.NeedsAdmin) return note;

        // 所有用户级 —— 提权补刀
        return await ApplyElevatedAsync(src, item.Name, enabled, ct);
    }

    /// <summary>写状态位。失败时 <paramref name="note"/> 给出原因（尤其"需要管理员权限"要能识别出来）。</summary>
    private static bool TryApply(StartupSource src, string name, bool enabled, out string note)
    {
        note = "";

        try
        {
            if (!RegistryPath.TryParse(src.ApprovalPath, out var hive, out var sub))
            {
                note = "状态键路径解析失败";
                return false;
            }

            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = root.CreateSubKey(sub, writable: true);
            if (key is null)
            {
                note = "打不开状态键";
                return false;
            }

            var bytes = new byte[12];
            bytes[0] = enabled ? (byte)0x02 : (byte)0x03;

            if (!enabled)
            {
                // 后面 8 字节 = 禁用时间（FILETIME，UTC）。任务管理器也是这么写的。
                BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(bytes, 4);
            }

            key.SetValue(name, bytes, RegistryValueKind.Binary);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            note = "需要管理员权限";
            return false;
        }
        catch (System.Security.SecurityException)
        {
            note = "需要管理员权限";
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Exception("startup", ex, $"写启动状态（{name}）");
            note = ex.Message;
            return false;
        }
    }

    private static void Log(StartupSource src, string name, bool enabled)
        => AppLog.Info("startup", $"{(enabled ? "启用" : "禁用")}开机启动项「{name}」（{src.Label}）");

    // ── 提权链路（照「应用卸载」页那套：清单进、结果出、跑完即退） ──────────────

    private sealed record AdminTask(string SourceId, string Name, bool Enabled);

    private sealed record AdminResult(string SourceId, string Name, bool Enabled, bool Ok, string Note);

    private static readonly JsonSerializerOptions AdminJson = new() { WriteIndented = true };

    private static async Task<string> ApplyElevatedAsync(StartupSource src, string name, bool enabled, CancellationToken ct)
    {
        try
        {
            var dir = Path.Combine(SettingsStore.Dir, "startup");
            Directory.CreateDirectory(dir);

            var manifest = Path.Combine(dir, "admin-task.json");
            var outcome = manifest + ".result";
            TryDelete(outcome);

            await File.WriteAllTextAsync(manifest,
                JsonSerializer.Serialize(new AdminTask(src.Id, name, enabled), AdminJson), ct);

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                AppLog.Warning("startup", "拿不到本程序路径，无法提权");
                return "无法提权：拿不到本程序路径";
            }

            AppLog.Info("startup", $"提权改动启动项：{name}（{src.Label}）");

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"\"{AdminSwitch}\" \"{manifest}\"",
                UseShellExecute = true,
                Verb = "runas",            // 会弹一次 UAC
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var p = await Task.Run(() => Process.Start(psi), ct);
            if (p is null) return "提权进程没有启动";

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                try { await p.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    AppLog.Warning("startup", "提权改动启动项超时（2 分钟）");
                    return "提权操作超时";
                }
            }

            if (!File.Exists(outcome)) return "提权操作没有留下结果（可能被取消了授权）";

            var result = JsonSerializer.Deserialize<AdminResult>(await File.ReadAllTextAsync(outcome, ct));
            if (result is null) return "提权结果读不出来";
            if (result.Ok) Log(src, name, enabled);

            return result.Ok ? "" : (result.Note.Length > 0 ? result.Note : "提权后仍未生效");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            AppLog.Info("startup", "用户取消了提权");
            return "已取消管理员授权";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Exception("startup", ex, "提权改动启动项");
            return ex.Message;
        }
    }

    /// <summary>
    /// 提权助手：读清单、写状态位、把结果写回去。由 <c>App</c> 在启动最前面调用，
    /// 跑完直接 <c>Environment.Exit</c>，⛔ 不进 WinUI 的正常流程、不建窗口、不碰互斥体。
    /// </summary>
    public static void ApplyElevated(string manifestPath)
    {
        AdminResult result;

        try
        {
            AppLog.Info("startup", $"提权助手启动，清单：{manifestPath}");

            var task = JsonSerializer.Deserialize<AdminTask>(File.ReadAllText(manifestPath));
            if (task is null)
            {
                result = new AdminResult("", "", false, false, "提权清单是空的");
            }
            else
            {
                var src = SourceOf(task.SourceId);
                if (src is null)
                    result = new AdminResult(task.SourceId, task.Name, task.Enabled, false, "提权清单里的来源不认");
                else if (TryApply(src, task.Name, task.Enabled, out var note))
                    result = new AdminResult(task.SourceId, task.Name, task.Enabled, true, "");
                else
                    result = new AdminResult(task.SourceId, task.Name, task.Enabled, false, note);
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("startup", ex, "提权助手执行");
            result = new AdminResult("", "", false, false, ex.Message);
        }

        try
        {
            File.WriteAllText(manifestPath + ".result", JsonSerializer.Serialize(result, AdminJson));
        }
        catch (Exception ex)
        {
            AppLog.Exception("startup", ex, "写提权结果");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { AppLog.Info("startup", $"删旧结果文件失败：{ex.GetType().Name}"); }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  命令行 / 快捷方式解析（只为界面上"这是什么、指向哪儿"）
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 命令行指向的文件是不是已经没了。⚠️ <b>拿不准就返回 <c>false</c></b>：
    /// 只有命令行明确以可执行文件开头（带引号，或无引号但能试出一个真实存在的文件前缀）时才敢判。
    /// </summary>
    private static bool CommandSeemsMissing(string command)
    {
        var s = command.Trim();
        if (s.Length == 0) return false;

        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            if (end <= 1) return false;
            var quoted = s[1..end];
            return !File.Exists(quoted) && Path.IsPathRooted(quoted);
        }

        // ① 整串就是一个路径 —— 无引号写法里最常见的一种，⛔ 这一步不能省：
        //    "C:\Program Files\X\a.exe" 这种没有参数、但路径本身带空格，
        //    只靠"从右往左截空格"永远试不到整串，会被错判成死项（2026-10-16 实测踩到过）。
        if (File.Exists(s)) return false;

        // ② 带参数：从右往左在每个空格处截断，谁先命中真实文件就说明没丢
        var idx = s.Length;
        while (true)
        {
            idx = s.LastIndexOf(' ', idx - 1);
            if (idx <= 0) break;
            if (File.Exists(s[..idx])) return false;
        }

        var sp = s.IndexOf(' ');
        var head = sp > 0 ? s[..sp] : s;
        return !File.Exists(head) && Path.IsPathRooted(head);
    }

    /// <summary>解析 <c>.lnk</c> 的目标与参数。解析不出来返回空串（调用方退回显示文件名）。</summary>
    private static string ResolveShortcut(string lnkPath)
    {
        object? link = null;

        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(lnkPath, StgmRead);

            var path = new StringBuilder(1024);
            ((IShellLinkW)link).GetPath(path, path.Capacity, IntPtr.Zero, 0);
            if (path.Length == 0) return "";

            var args = new StringBuilder(1024);
            ((IShellLinkW)link).GetArguments(args, args.Capacity);

            return args.Length > 0 ? path.ToString() + " " + args : path.ToString();
        }
        catch (Exception ex)
        {
            AppLog.Info("startup", $"解析快捷方式失败（{lnkPath}）：{ex.GetType().Name}");
            return "";
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.ReleaseComObject(link);
        }
    }

    private const uint StgmRead = 0;

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink
    {
    }

    // ⚠️ vtable 顺序**必须**与 shell32 里的一致，方法一个都不能少、也不能调序 ——
    //    否则调用会落到别的函数上（这段是从 shell32 的标准定义照抄的，别凭记忆改）。
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
