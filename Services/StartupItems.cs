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


public enum StartupSourceKind
{
    
    Registry,

    
    Folder,
}


















public sealed record StartupSource(
    string Id,
    string Label,
    StartupSourceKind Kind,
    string ItemsPath,
    string ApprovalPath,
    bool NeedsAdmin);





public sealed record StartupItem(
    string Name,
    string Command,
    string SourceId,
    string SourceLabel,
    bool Enabled,
    bool NeedsAdmin,
    bool Missing)
{
    
    public string Key => SourceId + "|" + Name.ToLowerInvariant();
}














public static class StartupItems
{
    
    public const string AdminSwitch = "--startup-admin";

    private static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string ProgramDataDir => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    private const string ExplorerKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    
    
    
    
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
                
                AppLog.Exception("startup", ex, $"扫描开机启动项（{src.Label}）");
            }
        }

        
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
            if (name.Length == 0) continue;   

            
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
                
                Missing: target.Length > 0 && CommandSeemsMissing(target)));
        }
    }

    
    
    
    
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

    
    
    

    
    
    
    
    public static async Task<string> SetEnabledAsync(StartupItem item, bool enabled, CancellationToken ct)
    {
        var src = SourceOf(item.SourceId);
        if (src is null) return "找不到这一项的来源位置";

        
        if (TryApply(src, item.Name, enabled, out var note))
        {
            Log(src, item.Name, enabled);
            return "";
        }

        if (!src.NeedsAdmin) return note;

        
        return await ApplyElevatedAsync(src, item.Name, enabled, ct);
    }

    
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
                Verb = "runas",            
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

        
        
        
        if (File.Exists(s)) return false;

        
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
