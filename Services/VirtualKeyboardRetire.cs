using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Services;

















internal static class VirtualKeyboardRetire
{
    private const string KeyPath = @"Software\Microsoft\TabletTip\1.7";
    private const string ValueName = "EnableDesktopModeAutoInvoke";

    private sealed class Backup
    {
        public bool Had { get; set; }
        public int Value { get; set; }
    }

    
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
                        key.DeleteValue(ValueName, throwOnMissingValue: false);   
                }
            }

            File.Delete(file);
            Log("虚拟键盘已下线：系统触摸键盘接管已还原成原样");
        }
        catch (Exception ex)
        {
            
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
            
        }
    }
}
