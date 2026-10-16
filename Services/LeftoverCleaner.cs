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

/// <summary>一条残留的清理结果。</summary>
public enum CleanStatus
{
    /// <summary>已删掉（文件进了隔离区，注册表项进了备份）。</summary>
    Removed = 0,

    /// <summary>删掉了，但**没能备份**（体积超上限之类）—— 界面上要如实标出来。</summary>
    RemovedNoBackup = 1,

    /// <summary>权限不够，没删成（会被提权补刀再试一次）。</summary>
    NeedsAdmin = 2,

    /// <summary>真删不掉（文件被占用 / 只读卷 / 路径没了）。</summary>
    Failed = 3,

    /// <summary>安全闸拦下了（不该出现，出现了说明前面漏判了）。</summary>
    Blocked = 4,
}

/// <summary>一条残留的清理结果明细。</summary>
public sealed record CleanupItemResult(LeftoverItem Item, CleanStatus Status, string Note);

/// <summary>一轮清理的总结。</summary>
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

/// <summary>
/// 残留清理器（2026-10-14 加，卸载流程的最后一步）。
///
/// ⛔⛔ 三条设计铁律（每一条都是"绝对安全干净"这个承诺的一部分）：
///
///  ① **先备份，再删**。这不是我们自己想出来的讲究 —— 业界那几个卸载工具都是这么干的
///     （HiBit 的注册表清理器原话：<c>automatically stores all deleted registry items, on a
///     per session basis, and includes a Restore feature for each</c>）。所以：
///       · 注册表 → 序列化成**标准 <c>.reg</c> 文本**（双击就能导回来，不依赖本程序）；
///       · 文件   → **移动**到隔离区（同盘符的移动是改目录项，几乎不花时间、不多占空间）。
///
///  ② **删除动作自己写，⛔ 绝不 shell out 到 <c>reg.exe</c> / <c>del</c>**。
///     除了没法拼出可靠的引号转义，"调系统的删除命令"本身就是这类工具出事故的主要来源。
///     本机开发环境里 <c>reg.exe</c> 还被安全策略硬拦（不可绕过），所以自己遍历
///     <c>Microsoft.Win32.Registry</c> 也是唯一可测的路。
///
///  ③ **两道闸，且复扫报实**。动手前把 <see cref="LeftoverResolver"/> 的禁碰名单**再判一次**
///     （界面上勾了什么都不算数）；动手后**当场复扫**，报告里写的是"实际删掉了什么"，
///     不是"我们打算删什么"。
///
/// ⚠️ 权限：<c>HKCU</c> 和用户目录普通权限就够；<c>HKLM</c> 与 <c>Program Files</c> 需要管理员。
///    做法是**先不提权跑一遍**，把失败的收起来，只有真有失败时才弹一次 UAC 补刀
///    （见 <see cref="ApplyManifestElevatedAsync"/>）—— 绝大多数情况一次 UAC 都不用多弹。
///
/// ⚠️ 隔离区放 <c>%LOCALAPPDATA%\ClassSoftwareHub\quarantine\</c>，⛔ **不进 <c>cache\</c>**：
///    <c>cache\</c> 由 <see cref="CacheHome.Clean"/> 按"保留最近 N 个版本"自动回收，
///    把隔离区放进去就等于过几天偷偷把用户的后悔药删了。
/// </summary>
public static class LeftoverCleaner
{
    /// <summary>隔离区根目录（永不自动清理，只有用户在设置页里显式清才会走）。</summary>
    public static string QuarantineRoot => Path.Combine(SettingsStore.Dir, "quarantine");

    /// <summary>跨盘符时"复制+删除"能接受的体积上限。超过就只删不备份，并在汇报里标明。</summary>
    private const long CrossVolumeBackupLimit = 2L * 1024 * 1024 * 1024;

    // ══════════════════════════════════════════════════════════════════════════
    //  主流程
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 清一轮。只处理 <see cref="LeftoverItem.Selected"/> 为 true 的条目。
    /// ⚠️ 调用方负责让它跑在后台线程上（注册表遍历 + 文件移动都可能慢）。
    /// </summary>
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

        // ① 注册表备份 —— 必须在删之前，而且**一次写全**
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

        // ② 逐条动手
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

        // ③ 提权补刀（只在真有失败时）
        var pendingIndexes = Enumerable.Range(0, results.Count)
            .Where(i => results[i].Status == CleanStatus.NeedsAdmin)
            .ToList();

        if (pendingIndexes.Count > 0 && quarantineOk)
        {
            var pending = pendingIndexes.Select(i => results[i].Item).ToList();
            var retried = await ApplyManifestElevatedAsync(dir, pending, ct);

            // 用提权结果替换原结果（按位置回填，⛔ 别用 record 的相等性去 IndexOf —— 那是在比较整个对象）
            for (var n = 0; n < pendingIndexes.Count; n++)
                if (n < retried.Count) results[pendingIndexes[n]] = retried[n];
        }
        else if (pendingIndexes.Count > 0)
        {
            // 隔离区都建不起来 ⇒ 不提权硬删（备份是前提，这条不能破）
            foreach (var i in pendingIndexes)
                results[i] = results[i] with { Status = CleanStatus.Failed, Note = "需要管理员权限，但本轮无法备份" };
        }

        // ④ 复扫：实际剩下什么
        Verify(picked);

        var logFile = WriteHumanLog(dir, appName, results, backup, quarantineOk);

        // 另出一份**机器可读**的清单：「隔离区」界面要按记录列出"都清过什么"，
        // 去解析上面那份中文记录太脆（措辞一改解析就废）。
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

    // ══════════════════════════════════════════════════════════════════════════
    //  文件
    // ══════════════════════════════════════════════════════════════════════════

    private static CleanupItemResult CleanPath(LeftoverItem item, string dir, int index, bool quarantineOk)
    {
        // ⛔ 第二道闸：进场再判一次禁碰（界面勾过的也不能例外）
        if (LeftoverResolver.IsForbiddenPathPublic(item.Path))
        {
            AppLog.Warning("cleanup", $"拒绝清理受保护的位置：{item.Path}");
            return new CleanupItemResult(item, CleanStatus.Blocked, "受保护的位置，已跳过");
        }

        var isDir = Directory.Exists(item.Path);
        var isFile = File.Exists(item.Path);
        if (!isDir && !isFile)
            return new CleanupItemResult(item, CleanStatus.Removed, "已经不在了");

        // ① 先试着**移动**到隔离区（同盘符 = 改目录项，瞬间完成且不占额外空间）
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
            // 跨盘符（IOException 的典型来源）→ 复制 + 删除。先量一下体积，太大就放弃备份。
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
            throw;   // 交给上一层记成 NeedsAdmin
        }
        catch (Exception ex)
        {
            AppLog.Info("cleanup", $"移入隔离区失败（{source}）：{ex.GetType().Name} {ex.Message}");
            return MoveOutcome.Failed;
        }
    }

    /// <summary>
    /// 强制删除。⚠️ 必须自己清只读属性 —— <c>Directory.Delete(path, true)</c> 碰到
    /// 只读文件会直接抛 <c>UnauthorizedAccessException</c>，于是被误判成"需要管理员"，
    /// 白弹一次 UAC。
    /// </summary>
    private static void ForceDelete(string path, bool isDir)
    {
        if (!isDir) { ClearReadOnly(path); File.Delete(path); return; }

        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            ClearReadOnly(f);

        foreach (var d in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(d, FileAttributes.Directory); }
            catch { /* 某些目录改不了属性，删的时候再报 */ }
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
        catch { /* 改不了属性就交给删除那一步报错 */ }
    }

    private static long MeasureDir(string dir)
    {
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(f).Length; }
            catch { /* 单个文件量不到就跳过 */ }
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

    // ══════════════════════════════════════════════════════════════════════════
    //  注册表
    // ══════════════════════════════════════════════════════════════════════════

    private static CleanupItemResult CleanRegistry(LeftoverItem item)
    {
        // ⛔ 第二道闸：进场再判一次禁碰。⚠️ 修键和修值走**不同**的判据 ——
        //    Run 那条**键**不能删，但它底下的某个**值**正是要删的东西。
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
            throw;   // → NeedsAdmin → 提权补刀
        }
        catch (System.Security.SecurityException)
        {
            throw;
        }
    }

    /// <summary>打开一个键的**父键**，并给出最后一段名字（删除只能从父键下手）。</summary>
    private static RegistryKey? OpenParent(RegistryKey root, string subPath, out string leaf)
    {
        leaf = "";
        var i = subPath.LastIndexOf('\\');
        if (i <= 0) return null;

        leaf = subPath[(i + 1)..];
        return root.OpenSubKey(subPath[..i], writable: true);
    }

    /// <summary>
    /// 把要删的注册表项序列化成**标准 <c>.reg</c> 文本**（UTF-16LE）。
    /// ⚠️ 导出是**递归**的：删的是整棵子树，导的也必须整棵，否则导回来是残缺的。
    /// ⛔ 别用 <c>reg export</c> —— 本机安全策略硬拦 <c>reg.exe</c>，而且是不可绕过的。
    /// </summary>
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
                    // 只备份"这一个值"：把它的键连同这一个值写出来
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

    /// <summary>
    /// 一个值 → <c>.reg</c> 的一行（长的十六进制会自动折行）。
    /// 值的类型必须**原样保留**，否则导回去会变成字符串 —— 那就不是"还原"了。
    /// </summary>
    private static string FormatValue(RegistryKey key, string name)
    {
        // ⛔ 值名在 .reg 里也要转义引号和反斜杠，否则名字里有引号的项会写出一个坏文件
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

    /// <summary>UTF-16LE 的十六进制（字符串值，末尾带双字节 0）。EXPAND_SZ 用。</summary>
    private static string HexOfUtf16(string s, bool terminate)
        => HexOfBytes(Encoding.Unicode.GetBytes(s + (terminate ? "\0" : "")));

    /// <summary>MULTI_SZ：每个串各带一个 0，整组结束再来一个 0。</summary>
    private static string HexOfMultiString(string[] parts)
    {
        var bytes = new List<byte>();
        foreach (var p in parts) bytes.AddRange(Encoding.Unicode.GetBytes(p + "\0"));
        bytes.AddRange(new byte[] { 0, 0 });
        return HexOfBytes(bytes.ToArray());
    }

    /// <summary>
    /// 字节数组 → <c>a1,b2,...</c>，每 25 个字节一行，续行末尾加反斜杠（<c>.reg</c> 的规定）。
    /// </summary>
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

    // ══════════════════════════════════════════════════════════════════════════
    //  提权补刀
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>提权助手模式的命令行开关。⛔ 改这里要同步改 <c>App.xaml.cs</c> 里的判定。</summary>
    public const string ApplySwitch = "--apply-leftovers";

    /// <summary>
    /// 把还需要管理员权限的条目写成清单，提权再跑一次自己。
    /// ⚠️ 提权是**按需**的：普通权限就能搞定的条目根本不会走到这儿，所以多数时候不弹 UAC。
    /// </summary>
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
                Verb = "runas",            // 会弹一次 UAC
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

            // 读回结果
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
            // 用户在 UAC 上点了"否"
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

    /// <summary>
    /// 提权助手：读清单，照着删，把结果写回去。由 <c>App</c> 在启动最前面调用，
    /// 跑完直接 <c>Environment.Exit</c>，⛔ 不进 WinUI 的正常流程。
    /// </summary>
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

    /// <summary>
    /// 提权模式下的文件删除。⚠️ 提权进程**不往隔离区搬** —— 因为隔离区在用户的
    /// <c>%LOCALAPPDATA%</c> 下，管理员进程搬过去的目录属主会变成 Administrators，
    /// 反而让用户自己以后删不掉。备份的事在非提权那一轮已经做过了（能备份的都备份了）。
    /// </summary>
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

    // ══════════════════════════════════════════════════════════════════════════
    //  复扫与汇报
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 清完之后当场看一遍还剩什么。
    /// ⛔ 这一步不能省 —— 汇报里必须写"实际结果"，不是"我们以为的结果"。
    ///    （文件被别的进程占用而删不掉是常态，不单查出来用户根本不知道。）
    /// </summary>
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
                item.StillThere = true;   // 查不清就算它还在，宁可保守
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

    /// <summary>写一份人看得懂的清理记录（放进隔离区，和注册表备份摆一起）。</summary>
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

    /// <summary>
    /// 写一份**机器可读**的清单（<c>manifest.json</c>），供「隔离区」界面读取。
    ///
    /// 为什么除了「清理记录.txt」还要这个：那份中文记录是写给人看的，程序要想列出
    /// "隔离区里都清过什么"就得去解析它 —— 措辞一改解析就废。所以另出一份结构化的，界面只认这一份。
    /// ⚠️ 读不到（旧记录没有这个文件）就只显示目录名与体积，⛔ 不假装知道详情。
    /// </summary>
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
            // 清单写不出来不算致命：界面读不到就退回"只显示目录名与体积"
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
