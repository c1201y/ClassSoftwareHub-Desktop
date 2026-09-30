using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Services.VirtualKeyboard;

/// <summary>
/// 「接管系统触摸键盘」—— 让系统那个别自动弹，改成弹我们的。
///
/// <b>⛔⛔ 这里只动一个注册表值，绝不碰任何进程。</b>
/// 2026-09-30 的事故就是把 <c>TabTip.exe</c> 当成要清理的对象去盯着杀，结果
/// <c>Win+H</c> 直接失效（那 82 次结束进程换来的）——<b>系统进程永远不是我们要管的东西</b>。
///
/// <b>为什么是注册表</b>：<c>EnableDesktopModeAutoInvoke</c> 是"桌面模式下戳输入框要不要自动弹
/// 触摸键盘"的唯一精准开关，**当前用户级、免管理员、Win10/11 通用**，而且跟"手动调出"互不影响 ——
/// 用户照样能用 Win+Ctrl+O / 任务栏图标把系统键盘叫出来。
///
/// <b>⛔ 三条纪律</b>：
///   ① 动之前先备份原值（原值很可能是"**根本不存在**"，那还原时就得**删值**而不是写 0）；
///   ② **没有备份就什么都不做** —— 否则用户连点两次「还原」，第二次会把刚还回去的原值又抹掉；
///   ③ 功能关掉时必须显式 <see cref="Restore"/>，不能让"系统键盘不自动弹"留在用户机器上。
/// </summary>
internal static class SystemKeyboardCapture
{
    private const string KeyPath = @"Software\Microsoft\TabletTip\1.7";
    private const string ValueName = "EnableDesktopModeAutoInvoke";

    /// <summary>备份文件。内容 = 原值在不在 + 原值。</summary>
    private static string BackupFile => Path.Combine(SettingsStore.Dir, "vkbd-systemkbd-backup.json");

    private sealed class Backup
    {
        public bool Had { get; set; }
        public int Value { get; set; }
    }

    /// <summary>当前是不是"我们改过"的状态（内存里记着，跨进程不可靠，所以只当辅助）。</summary>
    public static bool IsApplied { get; private set; }

    /// <summary>
    /// 让系统触摸键盘别自动弹。重复调用安全；已经改过就不再覆盖备份。
    /// </summary>
    public static bool Apply()
    {
        try
        {
            SaveBackupOnce();

            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            if (key is null)
            {
                VkbdLog.Write("⚠️ 接管系统键盘：打不开注册表键，本次不接管");
                return false;
            }

            key.SetValue(ValueName, 0, RegistryValueKind.DWord);
            IsApplied = true;
            VkbdLog.Write("接管系统键盘：已关闭系统触摸键盘的自动弹出（Win+Ctrl+O / 任务栏图标仍可用）");
            return true;
        }
        catch (Exception ex)
        {
            VkbdLog.Write("⚠️ 接管系统键盘失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 还原成原样。<b>没有备份就什么都不做</b>（这是刻意的，见类注释纪律②）。
    /// </summary>
    public static void Restore()
    {
        try
        {
            if (!File.Exists(BackupFile))
            {
                IsApplied = false;
                return;      // ⛔ 不是"那就删掉让系统回默认"，是**什么都别动**
            }

            var backup = JsonSerializer.Deserialize<Backup>(File.ReadAllText(BackupFile));
            if (backup is null)
            {
                IsApplied = false;
                return;
            }

            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key is not null)
            {
                if (backup.Had)
                    key.SetValue(ValueName, backup.Value, RegistryValueKind.DWord);
                else
                    key.DeleteValue(ValueName, throwOnMissingValue: false);   // 原来就没有 → 删掉
            }

            File.Delete(BackupFile);
            IsApplied = false;
            VkbdLog.Write("接管系统键盘：已还原成原样");
        }
        catch (Exception ex)
        {
            VkbdLog.Write("⚠️ 还原系统键盘设置失败：" + ex.Message);
        }
    }

    private static void SaveBackupOnce()
    {
        if (File.Exists(BackupFile)) return;      // 已备份过就别覆盖，否则会丢掉"原始值"

        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        var raw = key?.GetValue(ValueName);

        var backup = new Backup
        {
            Had = raw is not null,
            Value = raw is int i ? i : 0,
        };

        File.WriteAllText(BackupFile, JsonSerializer.Serialize(backup));
        VkbdLog.Write($"接管系统键盘：备份完成（原来 {(backup.Had ? $"存在，值={backup.Value}" : "不存在")}）");
    }
}
