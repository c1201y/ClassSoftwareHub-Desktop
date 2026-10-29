using System;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 给工具窗 / 浮窗 / 文档窗上**系统背景**（云母 / 亚克力 / 纯色），默认跟着「设置」里主窗口那个选择走。
///
/// 规矩（照搬主窗口那套，别自己发明）：
///   · 用 WinUI 自带的 `Window.SystemBackdrop`（框架自己管 composition target，**不会静默失效**）；
///   · ⚠️ **不要**改回手写 `new MicaController/DesktopAcrylicController` + `AddSystemBackdropTarget` ——
///     那套在窗口构造早期挂载会静默失败（不报错，窗口死黑）；
///   · 根元素背景：上了背景就置空（让背景透出来）；完全不支持才退纯色（**别留透明根**，
///     WinUI3 内容岛不铺满露出来的就是黑底）。
/// </summary>
public static class BackdropHost
{
    /// <summary>
    /// 给窗口上背景。返回实际生效的 kind（mica / acrylic / solid）。
    /// <paramref name="prefer"/> 传了就用它（"mica"/"acrylic"/"solid"），不传读设置。
    /// </summary>
    public static string Apply(Window win, Panel? root = null, string? prefer = null)
    {
        var kind = string.IsNullOrWhiteSpace(prefer) ? SettingBackdrop() : prefer!;
        kind = string.IsNullOrWhiteSpace(kind) ? "acrylic" : kind.Trim().ToLowerInvariant();

        try
        {
            win.SystemBackdrop = null;
            if (root is not null) root.Background = null;

            if (kind == "solid")
            {
                Solid(win, root);
                return "solid";
            }

            if (kind == "mica")
            {
                if (MicaController.IsSupported())
                {
                    // MicaKind.Base = 普通云母（BaseAlt 在深色下偏色发脏，别用）
                    win.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
                    return "mica";
                }

                kind = "acrylic";                            // 云母不支持 → 退亚克力
            }

            if (DesktopAcrylicController.IsSupported())
            {
                win.SystemBackdrop = new DesktopAcrylicBackdrop();
                return "acrylic";
            }

            if (MicaController.IsSupported())                // 亚克力不支持 → 退云母
            {
                win.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
                return "mica";
            }

            Solid(win, root);
            return "solid";
        }
        catch
        {
            Solid(win, root);
            return "solid";
        }
    }

    private static void Solid(Window win, Panel? root)
    {
        try
        {
            win.SystemBackdrop = null;
            if (root is not null) root.Background = new SolidColorBrush(SolidColor(root));
        }
        catch { }
    }

    /// <summary>纯色兜底：跟主窗口一个色（深浅色各一），别用死黑。</summary>
    private static Windows.UI.Color SolidColor(Panel root)
        => root.ActualTheme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(255, 32, 32, 32)
            : Windows.UI.Color.FromArgb(255, 243, 243, 243);

    /// <summary>读设置里主窗口选的那个背景（读不到就按亚克力）。</summary>
    private static string SettingBackdrop()
    {
        try
        {
            var b = App.Settings.Current.Backdrop;
            return string.IsNullOrWhiteSpace(b) ? "acrylic" : b;
        }
        catch { return "acrylic"; }
    }
}
