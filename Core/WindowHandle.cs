using System;
using Microsoft.UI.Xaml;

namespace ClassSoftwareHub.Desktop.Core;

















public static class WindowHandle
{
    
    
    
    
    public static IntPtr Of(FrameworkElement element)
    {
        try
        {
            if (element.XamlRoot?.ContentIslandEnvironment is { } island)
                return Microsoft.UI.Win32Interop.GetWindowFromWindowId(island.AppWindowId);
        }
        catch (Exception ex)
        {
            AppLog.Warn("window", "从元素取窗口句柄失败，改试主窗口：" + ex.Message);
        }

        try
        {
            return App.MainWindow is { } window
                ? WinRT.Interop.WindowNative.GetWindowHandle(window)
                : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    
    public static void BindPicker(object picker, FrameworkElement element)
    {
        var hwnd = Of(element);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("拿不到窗口句柄，无法打开文件对话框");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }
}
