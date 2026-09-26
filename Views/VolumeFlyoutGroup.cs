using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 两个音量浮窗（主音量 + 合成器）的联动：**一起活着、一起收**，并且**按住边条不让它缩回去**。
///
/// ① 一起收：主音量浮窗和合成器浮窗是两个独立窗口，谁失焦谁收的话，
///    用户点主音量浮窗上的「展开」→ 主音量失焦 → 主音量当场收起，合成器就成孤儿窗口了。
///    所以任一浮窗失焦后先等一拍，看焦点是不是落到另一个浮窗上；两个都没焦点才一起收。
///
/// ② 按住边条：音量浮窗是**挨着边条长出来的一栏**，边条必须在（不然看着就是俩不相关的东西）。
///    但浮窗一显形就会抢焦点，边条一"失焦"就自己缩回去 —— 所以浮窗开着期间要一直压住自动收起。
///    <see cref="ToolSidebarWindow.SuppressAutoCollapse"/> 是有时效的（15 秒到点自己恢复，防"永远不收"），
///    这里用个慢速定时器**持续续上**，浮窗一关就放手。
/// </summary>
public static class VolumeFlyoutGroup
{
    private static DispatcherQueueTimer? _focusTimer;
    private static DispatcherQueueTimer? _holdTimer;

    /// <summary>主音量浮窗 / 合成器浮窗的 HWND（由各自窗口在配置时注册）。</summary>
    public static IntPtr MainHwnd { get; set; }
    public static IntPtr MixerHwnd { get; set; }

    /// <summary>任一浮窗失焦时调它：延迟一拍，若焦点没落到另一个浮窗上就全收。</summary>
    public static void OnAnyDeactivated()
    {
        try
        {
            if (_focusTimer is null)
            {
                _focusTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
                _focusTimer.Interval = TimeSpan.FromMilliseconds(220);
                _focusTimer.IsRepeating = false;
                _focusTimer.Tick += (_, _) => CloseIfLostFocus();
            }
            _focusTimer.Stop();
            _focusTimer.Start();
        }
        catch
        {
        }
    }

    /// <summary>有浮窗开着了：把边条按住（持续续期，别让它自动缩回去）。</summary>
    public static void HoldSidebar()
    {
        try
        {
            ToolSidebarWindow.SuppressAutoCollapse = true;

            if (_holdTimer is null)
            {
                _holdTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
                _holdTimer.Interval = TimeSpan.FromSeconds(5);   // 比 SuppressAutoCollapse 的 15 秒寿命短得多
                _holdTimer.IsRepeating = true;
                _holdTimer.Tick += (_, _) => ToolSidebarWindow.SuppressAutoCollapse = true;
            }
            _holdTimer.Start();
        }
        catch
        {
        }
    }

    /// <summary>浮窗都关了：放开边条（之后它会照常走自己的自动收起）。</summary>
    public static void ReleaseSidebar()
    {
        try { _holdTimer?.Stop(); } catch { }
        try { ToolSidebarWindow.SuppressAutoCollapse = false; } catch { }
    }

    /// <summary>某个浮窗收完了叫一声：两个都收干净了才放开边条（滑出动画还得靠边条的位置），
    /// 并且顺手把边条也收回去（点音量时压住的那份自动收起，早就错过了它自己的失焦时机）。</summary>
    public static void OnAnyHidden()
    {
        if (VolumeWindow.IsVisible || VolumeMixerWindow.IsVisible) return;
        ReleaseSidebar();
        ToolSidebarWindow.CollapseAfterVolumeFlyoutsClosed();
    }

    /// <summary>把两个浮窗一起收掉（Esc、边条隐藏等显式收起走这里，不等延迟判断）。</summary>
    public static void CloseAll()
    {
        VolumeWindow.CloseIfOpen();
        VolumeMixerWindow.CloseIfOpen();
    }

    private static void CloseIfLostFocus()
    {
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && (fg == MainHwnd || fg == MixerHwnd)) return;   // 焦点还在我们这边

        CloseAll();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
