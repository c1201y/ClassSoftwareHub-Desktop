using System;
using System.Runtime.InteropServices;

namespace ClassSoftwareHub.Desktop.Core;

























public static class ShellNotify
{
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    private const uint WmSettingChange = 0x001A;
    private const uint WmCommand = 0x0111;

    
    private const uint SmtoAbortIfHung = 0x0002;

    private const uint TimeoutMs = 1000;

    
    private const int DesktopRefreshCommand = 41504;

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutText(
        IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutValue(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    
    public static void TraySettings()
    {
        try
        {
            
            SendMessageTimeoutText(HwndBroadcast, WmSettingChange, IntPtr.Zero, "TraySettings",
                SmtoAbortIfHung, TimeoutMs, out _);

            AppLog.Step("shell", "已发送任务栏刷新通知（TraySettings）");
        }
        catch (Exception ex)
        {
            AppLog.Warn("shell", "发送任务栏刷新通知失败：" + ex.Message);
        }
    }

    
    public static void RefreshDesktop()
    {
        try
        {
            var progman = FindWindow("Progman", null);
            if (progman == IntPtr.Zero)
            {
                AppLog.Warn("shell", "找不到桌面窗口（Progman），跳过刷新通知");
                return;
            }

            SendMessageTimeoutValue(progman, WmCommand, new IntPtr(DesktopRefreshCommand), IntPtr.Zero,
                SmtoAbortIfHung, TimeoutMs, out _);

            AppLog.Step("shell", "已发送桌面刷新通知");
        }
        catch (Exception ex)
        {
            AppLog.Warn("shell", "发送桌面刷新通知失败：" + ex.Message);
        }
    }
}
