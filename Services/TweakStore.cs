using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data.Tweaks;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>一次「应用更改」的结果。</summary>
public sealed record TweakApplyResult(int Requested, int Matched, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// 系统优化项的读写（2026-10-31）。
///
/// <para>
/// ⭐ 核心策略：**整批一次提权**。写 <c>HKLM</c> 必须管理员，但我们不做"切一个开关弹一次 UAC" ——
/// 界面上切换只改内存状态，点「应用更改」时把整批改动写成一份 <c>.reg</c>，
/// 用 <c>reg.exe import</c> 提权执行**一次**（与 Dism++ 的交互一致）。
/// </para>
/// <para>
/// ⚠️ 成败判定看**改完之后重新读到的值**，不看 <c>reg.exe</c> 的返回值 ——
/// 经 ShellExecute 提权启动的进程拿不到可靠退出码，用户点「否」和执行失败在代码里长得一样
/// （`FirewallGateway` 吃过"日志写成功、规则其实没建"的亏）。
/// </para>
/// <para>
/// ⛔ 临时 .reg 文件写在数据目录下、用完即删：里面只有键值，没有用户数据，但别留在桌面或临时目录
/// 让用户看见一个来路不明的文件。
/// </para>
/// </summary>
public static class TweakStore
{
    /// <summary>读当前值：值与 <see cref="TweakDef.OnValue"/> 相同即视为"开"。</summary>
    public static bool IsOn(TweakDef def)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(
                def.Scope == TweakScope.System ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
                RegistryView.Default);

            using var key = root.OpenSubKey(def.KeyPath, writable: false);
            var raw = key?.GetValue(def.ValueName);

            // 值缺失 = 系统尚未写过这一项，按"关"处理（OffValue 就是关态）
            if (raw is not int value) return false;

            return value == def.OnValue;
        }
        catch (Exception ex)
        {
            AppLog.Warn("tweaks", $"读取 {def.Id}（{def.FullPath}）失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 整批应用。<paramref name="changes"/> 是 (项, 目标状态)。
    /// <para>返回里 <c>Requested</c> 是要改几项、<c>Matched</c> 是改完之后**实际读到的状态与目标一致**的项数。</para>
    /// </summary>
    public static async Task<TweakApplyResult> ApplyAsync(IReadOnlyList<(TweakDef Def, bool On)> changes)
    {
        if (changes.Count == 0) return new TweakApplyResult(0, 0, null);

        // 界面类设置全在 HKCU，直接写、不弹 UAC；只有真正机器级的项才需要提权。
        var userChanges = changes.Where(c => c.Def.Scope == TweakScope.User).ToList();
        var systemChanges = changes.Where(c => c.Def.Scope == TweakScope.System).ToList();

        string? error = null;

        // ── ① 当前用户：直接写 ──
        foreach (var (def, on) in userChanges)
        {
            try
            {
                WriteUser(def, on);
            }
            catch (Exception ex)
            {
                AppLog.Warn("tweaks", $"写入 {def.Id}（{def.FullPath}）失败：{ex.Message}");
                error ??= ex.Message;
            }
        }

        AppLog.Info("tweaks",
            $"应用 {changes.Count} 项：当前用户 {userChanges.Count} 项、机器级 {systemChanges.Count}（{string.Join("、", changes.Select(c => c.Def.Id))}）");

        // ── ② 机器级：整批一次提权 ──
        if (systemChanges.Count > 0)
        {
            var path = Path.Combine(AppPaths.DataDir, $"tweaks-{DateTime.Now:HHmmss}.reg");

            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllText(path, BuildRegFile(systemChanges), new UnicodeEncoding(false, true));

                var psi = new ProcessStartInfo("reg.exe", $"import \"{path}\"")
                {
                    Verb = "runas",              // 只有这一路会弹 UAC
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };

                using var proc = Process.Start(psi);
                if (proc is not null) await proc.WaitForExitAsync();
            }
            catch (Exception ex)
            {
                // 用户点了「否」也走这里（Win32Exception：操作已被用户取消）
                AppLog.Warn("tweaks", "机器级改动未应用：" + ex.Message);
                error ??= ex.Message;
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }

        // 成败一律按改完之后**重新读到的值**判定，不看命令返回值
        var matched = changes.Count(c => IsOn(c.Def) == c.On);

        AppLog.Info("tweaks",
            matched == changes.Count
                ? $"全部生效（{matched}/{changes.Count}）"
                : $"有 {changes.Count - matched} 项未生效（{matched}/{changes.Count}）");

        return new TweakApplyResult(changes.Count, matched, error);
    }

    /// <summary>写当前用户的一个 DWORD。界面类设置都走这里，不需要管理员。</summary>
    private static void WriteUser(TweakDef def, bool on)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var key = root.CreateSubKey(def.KeyPath, writable: true)
                        ?? throw new InvalidOperationException($"打不开注册表键：{def.KeyPath}");

        key.SetValue(def.ValueName, on ? def.OnValue : def.OffValue, RegistryValueKind.DWord);
    }

    /// <summary>
    /// 生成 .reg 文本。<c>UnicodeEncoding(false, true)</c> 写出来是带 BOM 的 UTF-16LE ——
    /// <c>reg.exe import</c> 对编码敏感，ANSI/UTF-8 遇到中文键值会乱码甚至导入失败。
    /// </summary>
    public static string BuildRegFile(IReadOnlyList<(TweakDef Def, bool On)> changes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Windows Registry Editor Version 5.00");
        sb.AppendLine();

        // 同一个键下的多个值要合并成一个段，重复的段头虽然合法但读起来乱
        foreach (var section in changes.GroupBy(c => (c.Def.RootName, c.Def.KeyPath)))
        {
            sb.AppendLine($"[{section.Key.RootName}\\{section.Key.KeyPath}]");

            foreach (var (def, on) in section)
            {
                var value = on ? def.OnValue : def.OffValue;
                sb.AppendLine($"\"{def.ValueName}\"=dword:{value:x8}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// 重启资源管理器（任务栏）。⚠️ 会关掉当前打开的所有资源管理器窗口。
    /// 先结束再拉起：只结束不拉起的话，桌面上只剩壁纸，用户会以为系统坏了。
    /// </summary>
    public static void RestartExplorer()
    {
        try
        {
            AppLog.Info("tweaks", "重启资源管理器");

            using (var kill = Process.Start(new ProcessStartInfo("taskkill.exe", "/f /im explorer.exe")
                   {
                       UseShellExecute = false,
                       CreateNoWindow = true,
                   }))
            {
                kill?.WaitForExit(5000);
            }

            // ⚠️ 等它真的退干净再拉，否则新实例会立刻被判为"已在运行"而直接退出
            for (var i = 0; i < 20 && Process.GetProcessesByName("explorer").Length > 0; i++)
                System.Threading.Thread.Sleep(100);

            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Exception("tweaks", ex, "重启资源管理器");
        }
    }
}
