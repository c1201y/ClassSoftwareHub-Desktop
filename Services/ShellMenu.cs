using System;
using System.Diagnostics;
using System.IO;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 资源管理器右键菜单。全部写在 HKCU 下，不需要管理员权限，开关关掉即整项删除。
/// Win11 会把传统注册表菜单收进「显示更多选项」，这是系统行为，不是没注册上。
/// </summary>
public static class ShellMenu
{
    private sealed record Entry(string Root, string Verb, string Label, string Argument);

    private static readonly Entry[] Entries =
    {
        new(@"Software\Classes\SystemFileAssociations\.md\shell", "ClassSoftwareHub.Markdown",
            "用 ClassSoftwareHub 打开", "--md"),

        new(@"Software\Classes\SystemFileAssociations\image\shell", "ClassSoftwareHub.PickColor",
            "用 ClassSoftwareHub 取色", "--color"),

        new(@"Software\Classes\*\shell", "ClassSoftwareHub.QrShare",
            "用 ClassSoftwareHub 生成二维码", "--qr"),

        new(@"Software\Classes\*\shell", "ClassSoftwareHub.Hash",
            "用 ClassSoftwareHub 校验编码", "--hash"),
    };

    /// <summary>写入或删除右键菜单项。返回是否成功。</summary>
    public static bool Apply(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                Remove();
                return true;
            }

            var exe = ExecutablePath();
            if (string.IsNullOrEmpty(exe)) return false;

            foreach (var entry in Entries)
            {
                using var verb = Registry.CurrentUser.CreateSubKey(
                    $@"{entry.Root}\{entry.Verb}", writable: true);

                if (verb is null) continue;

                verb.SetValue(null, entry.Label);
                verb.SetValue("Icon", $"\"{exe}\",0");
                verb.SetValue("MultiSelectModel", "Single");

                using var command = verb.CreateSubKey("command", writable: true);
                command?.SetValue(null, $"\"{exe}\" {entry.Argument} \"%1\"");
            }

            AppLog.Step("shell", "右键菜单已注册");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("shell", "注册右键菜单失败：" + ex.Message);
            return false;
        }
    }

    public static void Remove()
    {
        foreach (var entry in Entries)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(
                    $@"{entry.Root}\{entry.Verb}", throwOnMissingSubKey: false);
            }
            catch (Exception ex)
            {
                AppLog.Warn("shell", "撤销右键菜单失败：" + ex.Message);
            }
        }

        AppLog.Step("shell", "右键菜单已撤销");
    }

    /// <summary>检查当前注册的项是否指向这个 exe，用于判断要不要重写。</summary>
    public static bool IsAppliedToCurrentExe()
    {
        try
        {
            var exe = ExecutablePath();
            if (string.IsNullOrEmpty(exe)) return false;

            using var command = Registry.CurrentUser.OpenSubKey(
                $@"{Entries[0].Root}\{Entries[0].Verb}\command", writable: false);

            return command?.GetValue(null) is string value
                   && value.Contains(exe, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? ExecutablePath()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path)) return path;

            using var proc = Process.GetCurrentProcess();
            return proc.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }
}
