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

/// <summary>
/// 一次「应用更改」的结果。
/// <para>
/// <see cref="Failed"/> 是**改完之后重读、状态仍与目标不一致**的那几项的 Id ——
/// 界面靠它把没成功的项留在待应用队列里，而不是一股脑清空（那样用户就再也找不到是哪几项没成）。
/// </para>
/// </summary>
public sealed record TweakApplyResult(
    int Requested,
    int Matched,
    string? Error,
    IReadOnlyList<string> Failed)
{
    public bool Ok => Error is null;
}

/// <summary>
/// 系统优化项的读写（2026-10-31）。
///
/// <para>
/// ⭐ 核心策略：**当前用户直写、机器级才提权**。界面类项都在 <c>HKCU</c>，切换只是排队，
/// 点「应用更改」时逐项直接写入，**不弹 UAC**；只有 <c>Scope = System</c> 的机器级项才合成一份
/// <c>.reg</c> 用 <c>reg.exe import</c> 提权执行**一次**（本页目前没有这类项，它们在「系统功能」页）。
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
    /// <summary>
    /// 读当前值；读不到 / 没写过 / 类型不认识都返回 <c>null</c>。
    /// <para>⚠️ 判定"开了没有"请用 <see cref="EffectiveValue"/>，别拿 <c>null</c> 直接和数字比。</para>
    /// </summary>
    public static int? ReadValue(TweakDef def)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(
                def.Scope == TweakScope.System ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
                RegistryView.Default);

            using var key = root.OpenSubKey(def.KeyPath, writable: false);
            var raw = key?.GetValue(def.ValueName);

            // 值缺失 = 系统还没写过这一项，按关态（OffValue）理解
            if (raw is null) return null;

            // ⚠️ 类型宽松一点：DWORD 正常是 int，但导入的 .reg 或旧软件可能留成 long / string，
            //    这时按 off 处理会让"系统明明是深色"的开关显示成关。
            //    解析规则抽在 Core/Tweaks/TweakRules.cs，那边有单元测试覆盖各种形态。
            var value = TweakRules.ParseDword(raw);

            if (value is null)
                AppLog.Warn("tweaks", $"{def.FullPath} 的值类型不认识（{raw.GetType().Name}），按关处理");

            return value;
        }
        catch (Exception ex)
        {
            AppLog.Warn("tweaks", $"读取 {def.Id}（{def.FullPath}）失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 界面上应当显示成哪个值：值缺失时按 <see cref="TweakDef.OffValue"/> 解释（与 Dism++ 的
    /// <c>&lt;Applicable&gt;</c> 兜底同一口径）。下拉框与开关都按它摆初始状态。
    /// </summary>
    public static int EffectiveValue(TweakDef def) => ReadValue(def) ?? def.OffValue;

    /// <summary>复选框语义：当前值等于 <see cref="TweakDef.OnValue"/> 视为"开"。</summary>
    public static bool IsOn(TweakDef def) => EffectiveValue(def) == def.OnValue;

    /// <summary>
    /// 整批应用。<paramref name="changes"/> 是 (项, 目标状态)。
    /// <para>返回里 <c>Requested</c> 是要改几项、<c>Matched</c> 是改完之后**实际读到的状态与目标一致**的项数。</para>
    /// </summary>
    public static async Task<TweakApplyResult> ApplyAsync(IReadOnlyList<(TweakDef Def, int Value)> changes)
    {
        if (changes.Count == 0) return new TweakApplyResult(0, 0, null, Array.Empty<string>());

        // 界面类设置全在 HKCU，直接写、不弹 UAC；只有真正机器级的项才需要提权。
        var userChanges = changes.Where(c => c.Def.Scope == TweakScope.User).ToList();
        var systemChanges = changes.Where(c => c.Def.Scope == TweakScope.System).ToList();

        string? error = null;

        // ── ① 当前用户：直接写 ──
        foreach (var (def, value) in userChanges)
        {
            try
            {
                WriteUser(def, value);
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

        // ── ③ 通知外壳重读：任务栏 / 桌面各发一条，同一类只发一次 ──
        // 2026-10-31 起不再靠杀 explorer 让改动"显形"（照 Dism++ 的 ExplorerNotify 做法）。
        // 放到后台线程：SendMessageTimeout 最长能等 1 秒，别卡住界面。
        var refreshes = TweakRules.RefreshesFor(changes);

        if (refreshes.Count > 0)
        {
            await Task.Run(() =>
            {
                foreach (var kind in refreshes)
                {
                    if (kind == TweakRefresh.Tray) ShellNotify.TraySettings();
                    else if (kind == TweakRefresh.Desktop) ShellNotify.RefreshDesktop();
                }
            });
        }

        // 成败一律按改完之后**重新读到的值**判定，不看命令返回值
        var failed = new List<string>();
        foreach (var (def, value) in changes)
            if (EffectiveValue(def) != value) failed.Add(def.Id);

        var matched = changes.Count - failed.Count;

        AppLog.Info("tweaks",
            matched == changes.Count
                ? $"全部生效（{matched}/{changes.Count}）"
                : $"有 {failed.Count} 项未生效（{matched}/{changes.Count}）：{string.Join("、", failed)}");

        return new TweakApplyResult(changes.Count, matched, error, failed);
    }

    /// <summary>写当前用户的一个 DWORD。界面类设置都走这里，不需要管理员。</summary>
    private static void WriteUser(TweakDef def, int value)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);

        // 关掉时要"恢复成没配置过"的项：直接删值，而不是留一个 0。
        // ⚠️ 这里刻意**不建键**：键本来就不存在时，创建一个空键只是多留垃圾，
        //    而"没有这个键"本身已经等于关态了。所以先按可写方式打开，打不开就直接收工。
        if (def.DeleteWhenOff && value == def.OffValue)
        {
            using var existing = root.OpenSubKey(def.KeyPath, writable: true);
            existing?.DeleteValue(def.ValueName, throwOnMissingValue: false);
            return;
        }

        using var key = root.CreateSubKey(def.KeyPath, writable: true)
                        ?? throw new InvalidOperationException($"打不开注册表键：{def.KeyPath}");

        key.SetValue(def.ValueName, value, RegistryValueKind.DWord);
    }

    /// <summary>
    /// 生成 .reg 文本。<c>UnicodeEncoding(false, true)</c> 写出来是带 BOM 的 UTF-16LE ——
    /// <c>reg.exe import</c> 对编码敏感，ANSI/UTF-8 遇到中文键值会乱码甚至导入失败。
    /// <para>生成规则本体在 <see cref="TweakRules.BuildRegText"/>（纯函数，有单元测试）。</para>
    /// </summary>
    public static string BuildRegFile(IReadOnlyList<(TweakDef Def, int Value)> changes) =>
        TweakRules.BuildRegText(changes);

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
