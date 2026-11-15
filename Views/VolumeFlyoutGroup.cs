using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace ClassSoftwareHub.Desktop.Views;













public static class VolumeFlyoutGroup
{
    private static DispatcherQueueTimer? _focusTimer;
    private static DispatcherQueueTimer? _holdTimer;

    
    public static IntPtr MainHwnd { get; set; }
    public static IntPtr MixerHwnd { get; set; }

    
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

    
    public static void HoldSidebar()
    {
        try
        {
            ToolSidebarWindow.SuppressAutoCollapse = true;

            if (_holdTimer is null)
            {
                _holdTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
                _holdTimer.Interval = TimeSpan.FromSeconds(5);   
                _holdTimer.IsRepeating = true;
                _holdTimer.Tick += (_, _) => ToolSidebarWindow.SuppressAutoCollapse = true;
            }
            _holdTimer.Start();
        }
        catch
        {
        }
    }

    
    public static void ReleaseSidebar()
    {
        try { _holdTimer?.Stop(); } catch { }
        try { ToolSidebarWindow.SuppressAutoCollapse = false; } catch { }
    }

    
    
    public static void OnAnyHidden()
    {
        if (VolumeWindow.IsVisible || VolumeMixerWindow.IsVisible) return;
        ReleaseSidebar();
        ToolSidebarWindow.CollapseAfterPopupsClosed();
    }

    
    public static void CloseAll()
    {
        VolumeWindow.CloseIfOpen();
        VolumeMixerWindow.CloseIfOpen();
    }

    private static void CloseIfLostFocus()
    {
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && (fg == MainHwnd || fg == MixerHwnd)) return;   

        CloseAll();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
