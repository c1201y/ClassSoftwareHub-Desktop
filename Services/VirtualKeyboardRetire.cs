using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 虚拟键盘下线后的**一次性收尾**（2026-10-09 删除该功能时补）。
///
/// 「接管系统触摸键盘」是那套功能里**唯一会写注册表**的地方 ——
/// 把 <c>HKCU\Software\Microsoft\TabletTip\1.7\EnableDesktopModeAutoInvoke</c> 写成 0，
/// 让系统那个别在桌面模式下自动弹（原来由 <c>SystemKeyboardCapture</c> 负责写、退出时还原）。
///
/// 功能删掉后这条改动**再也没人负责还原了**：用户机器上会永久留着"戳输入框不弹系统触摸键盘"，
/// 而他连是哪个开关造成的都看不见（设置页那页已经不在了）。所以这里保留最后一段清理。
///
/// <b>⛔ 三条纪律</b>（与当年 <c>SystemKeyboardCapture</c> 一致）：
///   ① <b>没有备份文件就什么都不做</b> —— 不是"回默认"，是别动别人的注册表；
///   ② 备份里记着原值**原本不存在**时，还原要**删值**而不是写 0；
///   ③ 还原成功后**删掉备份文件**，这样下次启动不会再走一遍（幂等）。
///   ④ 只还原注册表值，<b>绝不碰任何进程</b>。
/// </summary>
internal static class VirtualKeyboardRetire
{
    private const string KeyPath = @"Software\Microsoft\TabletTip\1.7";
    private const string ValueName = "EnableDesktopModeAutoInvoke";

    private sealed class Backup
    {
        public bool Had { get; set; }
        public int Value { get; set; }
    }

    /// <summary>幂等：备份文件不在就直接返回（老用户 / 从没开过这个开关的人走的都是这条）。</summary>
    public static void Run()
    {
        try
        {
            var file = Path.Combine(SettingsStore.Dir, "vkbd-systemkbd-backup.json");
            if (!File.Exists(file)) return;

            var backup = JsonSerializer.Deserialize<Backup>(File.ReadAllText(file));
            if (backup is null)
            {
                File.Delete(file);
                return;
            }

            using (var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true))
            {
                if (key is not null)
                {
                    if (backup.Had)
                        key.SetValue(ValueName, backup.Value, RegistryValueKind.DWord);
                    else
                        key.DeleteValue(ValueName, throwOnMissingValue: false);   // 原来就没有 → 删掉
                }
            }

            File.Delete(file);
            Log("虚拟键盘已下线：系统触摸键盘接管已还原成原样");
        }
        catch (Exception ex)
        {
            // 还原失败不影响使用，但要留痕（用户可能在日志页里看到）
            Log("还原系统触摸键盘设置失败: " + ex.Message);
        }
    }

    private static void Log(string message)
    {
        try
        {
            Core.AppLog.Info("settings", message);
        }
        catch
        {
            // 日志写不进去就算了
        }
    }
}
