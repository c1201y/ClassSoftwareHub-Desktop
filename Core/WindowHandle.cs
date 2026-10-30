using System;
using Microsoft.UI.Xaml;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 取「某个界面元素所在的那个窗口」的句柄（2026-10-30）。
///
/// <para>
/// 来由：编码页现在有两个去处 —— 主界面里，以及侧边栏点开的独立窗口（<c>ToolPageWindow</c>）。
/// 页面里挑文件用的是 <c>FileOpenPicker</c>，它必须绑定一个窗口句柄；原来写死
/// <c>App.MainWindow</c>，从独立窗口打开时文件对话框会挂到一个**可能收在托盘里**的主窗口上，
/// 表现是对话框位置怪、或者干脆看不见。
/// </para>
///
/// <para>
/// ⚠️ 只给"会在多个窗口里出现"的页面用。只在主界面里跑的页面继续用
///    <c>App.MainWindow</c> 就行 —— 不必为了统一而全量替换，那样每一处都要多一次 XamlRoot 查找，
///    而收益为零。
/// </para>
/// </summary>
public static class WindowHandle
{
    /// <summary>
    /// 元素所在窗口的句柄。取不到（还没进可视化树、或系统版本太老没有 ContentIslandEnvironment）
    /// 就退回主窗口；主窗口也没有则返回 <see cref="IntPtr.Zero"/>。
    /// </summary>
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

    /// <summary>给 FileOpenPicker / FileSavePicker / FolderPicker 绑定窗口。</summary>
    public static void BindPicker(object picker, FrameworkElement element)
    {
        var hwnd = Of(element);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("拿不到窗口句柄，无法打开文件对话框");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }
}
