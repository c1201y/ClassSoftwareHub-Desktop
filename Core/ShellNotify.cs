using System;
using System.Runtime.InteropServices;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 改完注册表后通知外壳刷新（2026-10-31，机制照 Dism++ 的 <c>ExplorerNotify</c> 抄过来）。
///
/// <para>
/// 为什么需要它：任务栏、桌面的很多设置**写进注册表不会自己重读** —— 以前的办法是
/// <c>taskkill /f /im explorer.exe</c> 再拉起来，代价是用户所有资源管理器窗口被关掉、
/// 界面还要僵几秒。Dism++ 用的是两条外壳通知，改完立刻生效、一个窗口都不动：
/// <list type="bullet">
///   <item><c>WM_SETTINGCHANGE</c> + <c>lParam = "TraySettings"</c> → 任务栏与托盘重读设置；</item>
///   <item><c>WM_COMMAND 0xA220</c>（41504）→ 桌面窗口执行"刷新"（图标、扩展名这类）。</item>
/// </list>
/// </para>
///
/// <para>
/// ⛔⛔ <b>桌面的那条只发给 Progman 一个窗口</b>，绝不 <c>HWND_BROADCAST</c>。
/// <c>WM_COMMAND</c> 是"命令"语义，广播出去可能被别的程序当成自己的命令号执行 ——
/// 为了一次刷新去动别人的窗口不值得。拿不到 Progman 就直接不发：宁可没反应，
/// 用户还能点界面上的「重启资源管理器」兜底。
/// </para>
///
/// <para>
/// ⚠️ 两条都带 1 秒超时、都吞异常：这是"锦上添花"的一步，失败了不该让整次应用报错。
/// </para>
/// </summary>
public static class ShellNotify
{
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    private const uint WmSettingChange = 0x001A;
    private const uint WmCommand = 0x0111;

    /// <summary>只发给没在卡住的窗口，避免等一个挂死的程序。</summary>
    private const uint SmtoAbortIfHung = 0x0002;

    private const uint TimeoutMs = 1000;

    /// <summary>外壳"刷新"的命令号（0xA220）。</summary>
    private const int DesktopRefreshCommand = 41504;

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutText(
        IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutValue(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    /// <summary>让任务栏与通知区域重读设置（隐藏搜索框、靠左对齐这类改完用它）。</summary>
    public static void TraySettings()
    {
        try
        {
            // 广播给所有顶层窗口是 Windows 文档认可的做法：lParam 是带名字的，不认识它的程序会直接忽略
            SendMessageTimeoutText(HwndBroadcast, WmSettingChange, IntPtr.Zero, "TraySettings",
                SmtoAbortIfHung, TimeoutMs, out _);

            AppLog.Step("shell", "已发送任务栏刷新通知（TraySettings）");
        }
        catch (Exception ex)
        {
            AppLog.Warn("shell", "发送任务栏刷新通知失败：" + ex.Message);
        }
    }

    /// <summary>让桌面刷新（显示扩展名、显示隐藏文件这类改完用它）。</summary>
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
