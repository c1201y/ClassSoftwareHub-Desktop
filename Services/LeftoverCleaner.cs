using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Services;


public enum CleanStatus
{
    
    Removed = 0,

    
    RemovedNoBackup = 1,

    
    NeedsAdmin = 2,

    
    Failed = 3,

    
    Blocked = 4,
}


public sealed record CleanupItemResult(LeftoverItem Item, CleanStatus Status, string Note);


public sealed class CleanupReport
{
    public string QuarantineDir { get; init; } = "";
    public string RegistryBackup { get; init; } = "";
    public string LogFile { get; init; } = "";
    public bool QuarantineUsable { get; init; }
    public List<CleanupItemResult> Results { get; init; } = new();

    public int Count(CleanStatus s) => Results.Count(r => r.Status == s);
    public int RemovedTotal => Count(CleanStatus.Removed) + Count(CleanStatus.RemovedNoBackup);
    public bool AnyRemoved => RemovedTotal > 0;
}





























public static class LeftoverCleaner
{
    
    public static string QuarantineRoot => Path.Combine(SettingsStore.Dir, "quarantine");

    
    private const long CrossVolumeBackupLimit = 2L * 1024 * 1024 * 1024;

    
    
    

    
    
    
    
    public static async Task<CleanupReport> CleanAsync(
        IReadOnlyList<LeftoverItem> items, string appName, CancellationToken ct = default)
    {
        var picked = items.Where(i => i.Selected).ToList();
        AppLog.Info("cleanup", $"开始清理「{appName}」的残留：勾选 {picked.Count} 条 / 共 {items.Count} 条");

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var dir = Path.Combine(QuarantineRoot, stamp);

        var quarantineOk = true;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "files"));
        }
        catch (Exception ex)
        {
            quarantineOk = false;
            AppLog.Exception("cleanup", ex, "建立隔离区目录");
        }

        
        var backup = "";
        try
        {
            backup = WriteRegistryBackup(dir, picked, appName);
        }
        catch (Exception ex)
        {
            AppLog.Exception("cleanup", ex, "写注册表备份");
        }

        var results = new List<CleanupItemResult>();

        
        var index = 0;
        foreach (var item in picked)
        {
            ct.ThrowIfCancellationRequested();
            index++;

            try
            {
                results.Add(item.Kind == FootprintKind.Path
                    ? CleanPath(item, dir, index, quarantineOk)
                    : CleanRegistry(item));
            }
            catch (OperationCanceledException) { throw; }
            catch (UnauthorizedAccessException ex)
            {
                AppLog.Info("cleanup", $"权限不足，留待提权补刀：{item.DisplayPath}（{ex.Message}）");
                results.Add(new CleanupItemResult(item, CleanStatus.NeedsAdmin, "需要管理员权限"));
            }
            catch (Exception ex)
            {
                AppLog.Exception("cleanup", ex, $"清理残留（{item.DisplayPath}）");
                results.Add(new CleanupItemResult(item, CleanStatus.Failed, Trim(ex.Message)));
            }
        }

        
        var pendingIndexes = Enumerable.Range(0, results.Count)
            .Where(i => results[i].Status == CleanStatus.NeedsAdmin)
            .ToList();

        if (pendingIndexes.Count > 0 && quarantineOk)
        {
            var pending = pendingIndexes.Select(i => results[i].Item).ToList();
            var retried = await ApplyManifestElevatedAsync(dir, pending, ct);

            
            for (var n = 0; n < pendingIndexes.Count; n++)
                if (n < retried.Count) results[pendingIndexes[n]] = retried[n];
        }
        else if (pendingIndexes.Count > 0)
        {
            
            foreach (var i in pendingIndexes)
                results[i] = results[i] with { Status = CleanStatus.Failed, Note = "需要管理员权限，但本轮无法备份" };
        }

        
        Verify(picked);

        var logFile = WriteHumanLog(dir, appName, results, backup, quarantineOk);

        
        
        WriteManifest(dir, appName, results, quarantineOk);

        var report = new CleanupReport
        {
            QuarantineDir = quarantineOk ? dir : "",
            RegistryBackup = backup,
            LogFile = logFile,
            QuarantineUsable = quarantineOk,
            Results = results,
        };

        AppLog.Info("cleanup",
            $"「{appName}」残留清理结束：成功 {report.RemovedTotal} 条" +
            $"（其中未备份 {report.Count(CleanStatus.RemovedNoBackup)} 条）、" +
            $"失败 {report.Count(CleanStatus.Failed)} 条、被拦 {report.Count(CleanStatus.Blocked)} 条" +
            (quarantineOk ? $"，隔离区 {dir}" : "，⚠️ 隔离区不可用，本轮无法还原"));

        return report;
    }

    
    
    

    private static CleanupItemResult CleanPath(LeftoverItem item, string dir, int index, bool quarantineOk)
    {
        
        if (LeftoverResolver.IsForbiddenPathPublic(item.Path))
        {
            AppLog.Warning("cleanup", $"拒绝清理受保护的位置：{item.Path}");
            return new CleanupItemResult(item, CleanStatus.Blocked, "受保护的位置，已跳过");
        }

        var isDir = Directory.Exists(item.Path);
        var isFile = File.Exists(item.Path);
        if (!isDir && !isFile)
            return new CleanupItemResult(item, CleanStatus.Removed, "已经不在了");

        
        if (quarantineOk)
        {
            var target = Path.Combine(dir, "files", $"{index:000}_{Sanitize(Path.GetFileName(item.Path.TrimEnd('\\')))}");
            var moved = TryMove(item.Path, target, isDir);
            if (moved == MoveOutcome.Moved)
                return new CleanupItemResult(item, CleanStatus.Removed, "已移入隔离区");
            if (moved == MoveOutcome.CrossVolumeTooBig)
            {
                ForceDelete(item.Path, isDir);
                return new CleanupItemResult(item, CleanStatus.RemovedNoBackup, "体积过大未备份，已直接删除");
            }
        }

        ForceDelete(item.Path, isDir);
        return new CleanupItemResult(item,
            quarantineOk ? CleanStatus.Removed : CleanStatus.RemovedNoBackup,
            quarantineOk ? "已移除（未备份）" : "已移除（隔离区不可用）");
    }

    private enum MoveOutcome { Moved, Failed, CrossVolumeTooBig }

    private static MoveOutcome TryMove(string source, string target, bool isDir)
    {
        try
        {
            if (isDir) Directory.Move(source, target);
            else File.Move(source, target);
            return MoveOutcome.Moved;
        }
        catch (IOException)
        {
            
            try
            {
                var size = isDir ? MeasureDir(source) : new FileInfo(source).Length;
                if (size > CrossVolumeBackupLimit)
                {
                    AppLog.Info("cleanup", $"跨盘符且超过备份上限（{size / 1048576} MB），只删不备份：{source}");
                    return MoveOutcome.CrossVolumeTooBig;
                }

                if (isDir) CopyDir(source, target);
                else File.Copy(source, target, overwrite: false);

                ForceDelete(source, isDir);
                return MoveOutcome.Moved;
            }
            catch (Exception ex)
            {
                AppLog.Info("cleanup", $"跨盘备份失败，改为直接删除（{source}）：{ex.GetType().Name} {ex.Message}");
                return MoveOutcome.Failed;
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw;   
        }
        catch (Exception ex)
        {
            AppLog.Info("cleanup", $"移入隔离区失败（{source}）：{ex.GetType().Name} {ex.Message}");
            return MoveOutcome.Failed;
        }
    }

    
    
    
    
    
    private static void ForceDelete(string path, bool isDir)
    {
        if (!isDir) { ClearReadOnly(path); File.Delete(path); return; }

        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            ClearReadOnly(f);

        foreach (var d in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(d, FileAttributes.Directory); }
            catch {  }
        }

        Directory.Delete(path, recursive: true);
    }

    private static void ClearReadOnly(string file)
    {
        try
        {
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
        }
        catch {  }
    }

    private static long MeasureDir(string dir)
    {
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(f).Length; }
            catch {  }
        }
        return total;
    }

    private static void CopyDir(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var d in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, d)));
        foreach (var f in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(target, Path.GetRelativePath(source, f)), overwrite: false);
    }

    private static string Sanitize(string name)
    {
        if (name.Length == 0) return "item";
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        var s = sb.ToString();
        return s.Length > 60 ? s[..60] : s;
    }

    
    
    

    private static CleanupItemResult CleanRegistry(LeftoverItem item)
    {
        
        
        var forbidden = item.Kind == FootprintKind.RegistryKey
            ? LeftoverResolver.IsForbiddenKeyPublic(item.Probe, item.Path)
            : LeftoverResolver.IsForbiddenRegistryPublic(item.Probe, item.Path, item.ValueName);

        if (forbidden)
        {
            AppLog.Warning("cleanup", $"拒绝清理受保护的注册表位置：{item.Path} ▸ {item.ValueName}");
            return new CleanupItemResult(item, CleanStatus.Blocked, "受保护的注册表位置，已跳过");
        }

        if (!RegistryPath.TryParse(item.Path, out var hive, out var subPath))
            return new CleanupItemResult(item, CleanStatus.Failed, "注册表路径解析不了");

        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);

        try
        {
            if (item.Kind == FootprintKind.RegistryKey)
            {
                using var parent = OpenParent(root, subPath, out var leaf);
                if (parent is null || leaf.Length == 0)
                    return new CleanupItemResult(item, CleanStatus.Removed, "已经不在了");

                parent.DeleteSubKeyTree(leaf, throwOnMissingSubKey: false);
                return new CleanupItemResult(item, CleanStatus.Removed, "已移除（已备份至 .reg）");
            }

            using var key = root.OpenSubKey(subPath, writable: true);
            if (key is null)
                return new CleanupItemResult(item, CleanStatus.Removed, "已经不在了");

            if (Array.IndexOf(key.GetValueNames(), item.ValueName) < 0)
                return new CleanupItemResult(item, CleanStatus.Removed, "已经不在了");

            key.DeleteValue(item.ValueName, throwOnMissingValue: false);
            return new CleanupItemResult(item, CleanStatus.Removed, "已删除（备份在 .reg 里）");
        }
        catch (UnauthorizedAccessException)
        {
            throw;   
        }
        catch (System.Security.SecurityException)
        {
            throw;
        }
    }

    
    private static RegistryKey? OpenParent(RegistryKey root, string subPath, out string leaf)
    {
        leaf = "";
        var i = subPath.LastIndexOf('\\');
        if (i <= 0) return null;

        leaf = subPath[(i + 1)..];
        return root.OpenSubKey(subPath[..i], writable: true);
    }

    
    
    
    
    
    private static string WriteRegistryBackup(string dir, IReadOnlyList<LeftoverItem> items, string appName)
    {
        var regItems = items.Where(i => i.Kind != FootprintKind.Path).ToList();
        if (regItems.Count == 0 || dir.Length == 0) return "";

        var sb = new StringBuilder();
        sb.Append("Windows Registry Editor Version 5.00\r\n\r\n");
        sb.Append($"; ClassSoftwareHub · 卸载残留清理的注册表备份\r\n");
        sb.Append($"; 软件：{appName}\r\n");
        sb.Append($"; 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n");
            sb.Append("; 说明：下列各项于本程序清理前原样导出。双击本文件即可导入注册表；\r\n");
            sb.Append(";       导入为「合并」而非「还原」：若同名项已存在，将以本文件内容为准。\r\n\r\n");

        foreach (var item in regItems)
        {
            try
            {
                if (!RegistryPath.TryParse(item.Path, out var hive, out var subPath)) continue;
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);

                if (item.Kind == FootprintKind.RegistryValue)
                {
                    
                    using var key = root.OpenSubKey(subPath, writable: false);
                    if (key is null) continue;

                    var names = key.GetValueNames();
                    if (Array.IndexOf(names, item.ValueName) < 0) continue;

                    sb.Append('[').Append(FullHiveName(hive)).Append('\\').Append(subPath).Append("]\r\n");
                    sb.Append(FormatValue(key, item.ValueName));
                    sb.Append("\r\n");
                }
                else
                {
                    using var key = root.OpenSubKey(subPath, writable: false);
                    if (key is null) continue;
                    ExportKey(sb, key, FullHiveName(hive) + "\\" + subPath);
                }
            }
            catch (Exception ex)
            {
                AppLog.Exception("cleanup", ex, $"备份注册表项（{item.Path}）");
            }
        }

        var file = Path.Combine(dir, "registry-backup.reg");
        File.WriteAllText(file, sb.ToString(), new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

        AppLog.Info("cleanup", $"注册表备份已写出：{file}");
        return file;
    }

    private static void ExportKey(StringBuilder sb, RegistryKey key, string fullPath)
    {
        sb.Append('[').Append(fullPath).Append("]\r\n");

        foreach (var name in key.GetValueNames())
            sb.Append(FormatValue(key, name));

        sb.Append("\r\n");

        foreach (var sub in key.GetSubKeyNames())
        {
            try
            {
                using var child = key.OpenSubKey(sub, writable: false);
                if (child is not null) ExportKey(sb, child, fullPath + "\\" + sub);
            }
            catch (Exception ex)
            {
                AppLog.Info("cleanup", $"备份子键失败（{fullPath}\\{sub}）：{ex.GetType().Name}");
            }
        }
    }

    
    
    
    
    private static string FormatValue(RegistryKey key, string name)
    {
        
        var label = name.Length == 0 ? "@" : "\"" + Escape(name) + "\"";

        object? raw;
        RegistryValueKind kind;
        try
        {
            raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            kind = key.GetValueKind(name);
        }
        catch (Exception ex)
        {
            AppLog.Info("cleanup", $"读取注册表值失败（{key.Name} ▸ {name}）：{ex.GetType().Name}");
            return "";
        }

        switch (kind)
        {
            case RegistryValueKind.String:
            case RegistryValueKind.ExpandString:
                var s = raw as string ?? "";
                var text = kind == RegistryValueKind.ExpandString
                    ? "hex(2):" + HexOfUtf16(s, terminate: true)
                    : "\"" + Escape(s) + "\"";
                return label + "=" + text + "\r\n";

            case RegistryValueKind.MultiString:
                return label + "=hex(7):" + HexOfMultiString(raw as string[] ?? Array.Empty<string>()) + "\r\n";

            case RegistryValueKind.DWord:
                return label + "=dword:" + ((raw as int?) ?? 0).ToString("x8") + "\r\n";

            case RegistryValueKind.QWord:
                return label + "=hex(b):" + HexOfBytes(BitConverter.GetBytes((raw as long?) ?? 0)) + "\r\n";

            case RegistryValueKind.Binary:
                return label + "=hex:" + HexOfBytes(raw as byte[] ?? Array.Empty<byte>()) + "\r\n";

            case RegistryValueKind.None:
                return label + "=hex(0):\r\n";

            default:
                return label + "=\"" + Escape(raw?.ToString() ?? "") + "\"\r\n";
        }
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    
    private static string HexOfUtf16(string s, bool terminate)
        => HexOfBytes(Encoding.Unicode.GetBytes(s + (terminate ? "\0" : "")));

    
    private static string HexOfMultiString(string[] parts)
    {
        var bytes = new List<byte>();
        foreach (var p in parts) bytes.AddRange(Encoding.Unicode.GetBytes(p + "\0"));
        bytes.AddRange(new byte[] { 0, 0 });
        return HexOfBytes(bytes.ToArray());
    }

    
    
    
    private static string HexOfBytes(byte[] bytes)
    {
        if (bytes.Length == 0) return "";

        var sb = new StringBuilder(bytes.Length * 3 + 8);
        for (var i = 0; i < bytes.Length; i++)
        {
            sb.Append(bytes[i].ToString("x2"));
            var last = i == bytes.Length - 1;
            if (!last) sb.Append(',');
            if ((i + 1) % 25 == 0 && !last) sb.Append("\\\r\n  ");
        }

        return sb.ToString();
    }

    private static string FullHiveName(RegistryHive hive) => hive switch
    {
        RegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE",
        RegistryHive.CurrentUser => "HKEY_CURRENT_USER",
        RegistryHive.ClassesRoot => "HKEY_CLASSES_ROOT",
        RegistryHive.Users => "HKEY_USERS",
        _ => "HKEY_LOCAL_MACHINE",
    };

    
    
    

    
    public const string ApplySwitch = "--apply-leftovers";

    
    
    
    
    private static async Task<List<CleanupItemResult>> ApplyManifestElevatedAsync(
        string dir, List<LeftoverItem> items, CancellationToken ct)
    {
        var results = new List<CleanupItemResult>();
        if (items.Count == 0) return results;

        var manifest = Path.Combine(dir, "admin-tasks.json");
        var outcomeFile = manifest + ".result";

        try
        {
            var payload = items.Select(i => new ManifestEntry
            {
                Kind = (int)i.Kind,
                Path = i.Path,
                ValueName = i.ValueName,
                Probe = i.Probe,
            }).ToList();

            await File.WriteAllTextAsync(manifest,
                JsonSerializer.Serialize(payload, ManifestJson), ct);

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                AppLog.Warning("cleanup", "拿不到本程序路径，无法提权补刀");
                return items.Select(i => new CleanupItemResult(i, CleanStatus.Failed, "无法提权")).ToList();
            }

            AppLog.Info("cleanup", $"提权补刀：{items.Count} 条需要管理员权限，启动 {exe} {ApplySwitch}");

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"\"{ApplySwitch}\" \"{manifest}\"",
                UseShellExecute = true,
                Verb = "runas",            
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var p = await Task.Run(() => Process.Start(psi), ct);
            if (p is null)
                return items.Select(i => new CleanupItemResult(i, CleanStatus.Failed, "提权进程没起来")).ToList();

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
                try { await p.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    AppLog.Warning("cleanup", "提权补刀超时（3 分钟）");
                }
            }

            
            if (!File.Exists(outcomeFile))
            {
                AppLog.Warning("cleanup", "提权补刀没留下结果文件（可能被用户取消了 UAC）");
                return items.Select(i => new CleanupItemResult(i, CleanStatus.Failed, "提权未完成")).ToList();
            }

            var text = await File.ReadAllTextAsync(outcomeFile, ct);
            var done = JsonSerializer.Deserialize<List<ManifestResult>>(text, ManifestJson)
                       ?? new List<ManifestResult>();

            foreach (var item in items)
            {
                var hit = done.FirstOrDefault(d =>
                    d.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase) &&
                    d.ValueName.Equals(item.ValueName, StringComparison.OrdinalIgnoreCase));

                results.Add(hit is null
                    ? new CleanupItemResult(item, CleanStatus.Failed, "提权后仍未处理")
                    : new CleanupItemResult(item,
                        hit.Ok ? CleanStatus.Removed : CleanStatus.Failed,
                        hit.Ok ? Trim(hit.Note) : Trim(hit.Note)));
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            
            AppLog.Info("cleanup", "用户取消了提权补刀");
            results.AddRange(items.Select(i => new CleanupItemResult(i, CleanStatus.Failed, "已取消管理员授权")));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Exception("cleanup", ex, "提权补刀");
            results.AddRange(items.Select(i => new CleanupItemResult(i, CleanStatus.Failed, Trim(ex.Message))));
        }

        return results;
    }

    
    
    
    
    public static void ApplyManifestElevated(string manifestPath)
    {
        var outcome = new List<ManifestResult>();

        try
        {
            AppLog.Info("cleanup", $"提权助手启动，清单：{manifestPath}");

            var text = File.ReadAllText(manifestPath);
            var entries = JsonSerializer.Deserialize<List<ManifestEntry>>(text, ManifestJson)
                          ?? new List<ManifestEntry>();

            foreach (var e in entries)
            {
                var item = new LeftoverItem(new FootprintNode(
                    (FootprintKind)e.Kind, e.Probe, e.Path,
                    Path.GetFileName(e.Path.TrimEnd('\\')), e.ValueName));

                try
                {
                    var r = item.Kind == FootprintKind.Path
                        ? CleanPathElevated(item)
                        : CleanRegistry(item);

                    outcome.Add(new ManifestResult
                    {
                        Path = e.Path,
                        ValueName = e.ValueName,
                        Ok = r.Status is CleanStatus.Removed or CleanStatus.RemovedNoBackup,
                        Note = r.Note,
                    });
                }
                catch (Exception ex)
                {
                    AppLog.Exception("cleanup", ex, $"提权清理（{e.Path}）");
                    outcome.Add(new ManifestResult
                    {
                        Path = e.Path, ValueName = e.ValueName, Ok = false, Note = Trim(ex.Message),
                    });
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("cleanup", ex, "提权助手执行");
        }
        finally
        {
            try
            {
                File.WriteAllText(manifestPath + ".result",
                    JsonSerializer.Serialize(outcome, ManifestJson));
            }
            catch (Exception ex)
            {
                AppLog.Exception("cleanup", ex, "提权助手写结果");
            }
        }
    }

    
    
    
    
    
    private static CleanupItemResult CleanPathElevated(LeftoverItem item)
    {
        if (LeftoverResolver.IsForbiddenPathPublic(item.Path))
            return new CleanupItemResult(item, CleanStatus.Blocked, "受保护的位置");

        var isDir = Directory.Exists(item.Path);
        if (!isDir && !File.Exists(item.Path))
            return new CleanupItemResult(item, CleanStatus.Removed, "已经没有权限障碍了（对手已删）");

        ForceDelete(item.Path, isDir);
        return new CleanupItemResult(item, CleanStatus.Removed, "管理员权限下已删除");
    }

    private sealed class ManifestEntry
    {
        public int Kind { get; set; }
        public string Path { get; set; } = "";
        public string ValueName { get; set; } = "";
        public string Probe { get; set; } = "";
    }

    private sealed class ManifestResult
    {
        public string Path { get; set; } = "";
        public string ValueName { get; set; } = "";
        public bool Ok { get; set; }
        public string Note { get; set; } = "";
    }

    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    
    
    

    
    
    
    
    
    private static void Verify(IReadOnlyList<LeftoverItem> items)
    {
        foreach (var item in items)
        {
            try
            {
                item.StillThere = item.Kind == FootprintKind.Path
                    ? File.Exists(item.Path) || Directory.Exists(item.Path)
                    : RegistryStillThere(item);
            }
            catch (Exception ex)
            {
                AppLog.Info("cleanup", $"复扫失败（{item.DisplayPath}）：{ex.GetType().Name}");
                item.StillThere = true;   
            }
        }
    }

    private static bool RegistryStillThere(LeftoverItem item)
    {
        if (!RegistryPath.TryParse(item.Path, out var hive, out var subPath)) return true;

        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        if (item.Kind == FootprintKind.RegistryKey)
        {
            using var k = root.OpenSubKey(subPath, writable: false);
            return k is not null;
        }

        using var key = root.OpenSubKey(subPath, writable: false);
        return key is not null && Array.IndexOf(key.GetValueNames(), item.ValueName) >= 0;
    }

    
    private static string WriteHumanLog(string dir, string appName, List<CleanupItemResult> results,
                                        string backupFile, bool quarantineOk)
    {
        if (dir.Length == 0) return "";

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"ClassSoftwareHub · 卸载残留清理记录");
            sb.AppendLine($"软件：{appName}");
            sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"结果：已移除 {results.Count(r => r.Status is CleanStatus.Removed or CleanStatus.RemovedNoBackup)} 项 · " +
                          $"失败 {results.Count(r => r.Status == CleanStatus.Failed)} 项 · " +
                          $"跳过 {results.Count(r => r.Status == CleanStatus.Blocked)} 项");
            sb.AppendLine();

            if (quarantineOk)
            {
                sb.AppendLine("── 还原方式 ──");
                if (backupFile.Length > 0)
                    sb.AppendLine("· 注册表：双击同目录下的 registry-backup.reg 导入即可。");
                sb.AppendLine("· 文件：已原样移至本目录的 files\\ 下，剪切回原位置即可。");
                sb.AppendLine("        （各项原位置见下方清单的「位置」列）");
                sb.AppendLine();
            }

            sb.AppendLine("── 明细 ──");
            foreach (var r in results)
                sb.AppendLine($"[{Tag(r.Status)}] {r.Item.DisplayPath}    — {r.Note}");

            sb.AppendLine();
            sb.AppendLine("⚠️ 本目录不参与自动清理。确认一切正常后，可手动删除整个目录。");

            var file = Path.Combine(dir, "清理记录.txt");
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
            return file;
        }
        catch (Exception ex)
        {
            AppLog.Exception("cleanup", ex, "写清理记录");
            return "";
        }
    }

    
    
    
    
    
    
    
    private static void WriteManifest(string dir, string appName, List<CleanupItemResult> results, bool quarantineOk)
    {
        if (dir.Length == 0 || !quarantineOk) return;

        try
        {
            var payload = new
            {
                app = appName,
                time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                removed = results.Count(r => r.Status is CleanStatus.Removed or CleanStatus.RemovedNoBackup),
                failed = results.Count(r => r.Status == CleanStatus.Failed),
                blocked = results.Count(r => r.Status == CleanStatus.Blocked),
                items = results.Select(r => new
                {
                    status = r.Status.ToString(),
                    kind = r.Item.Kind.ToString(),
                    path = r.Item.DisplayPath,
                    note = r.Note,
                }).ToList(),
            };

            var json = System.Text.Json.JsonSerializer.Serialize(payload,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(Path.Combine(dir, "manifest.json"), json, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            
            AppLog.Exception("cleanup", ex, "写隔离区清单");
        }
    }

    private static string Tag(CleanStatus s) => s switch
    {
        CleanStatus.Removed => "已移除",
        CleanStatus.RemovedNoBackup => "已移除(未备份)",
        CleanStatus.NeedsAdmin => "权限不足",
        CleanStatus.Failed => "失败",
        _ => "已跳过",
    };

    private static string Trim(string s) => s.Length > 120 ? s[..120] + "…" : s;
}
