using System;
using System.Collections.Generic;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

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
///
/// <para>
/// ⛔⛔ 顺序：**先定深浅色，再建背景材质**（2026-10-29 修，之前是反的）。
/// 实测反差：主窗口是「<c>ApplyTheme</c> → <c>ApplyBackdrop</c>」，而且切深浅色时会**重建**背景，
/// 所以它一直是浅色；而所有走本方法的窗口都是「先建背景、后设主题」，材质按创建那一刻的
/// （= 系统）深浅色烤死 —— 应用设成浅色、系统还是深色时，这些窗口的透明区域整片发黑
/// （Nick 截图：条款与声明、日志·详细视图全黑，主界面正常）。
/// 另外 <c>SystemBackdrop</c> 在主题变化后不会自己重新上色，所以这里还挂了 <c>ActualThemeChanged</c> 重建。
/// </para>
/// </summary>
public static class BackdropHost
{
    /// <summary>
    /// 给窗口上背景。返回实际生效的 kind（mica / acrylic / solid）。
    /// <paramref name="prefer"/> 传了就用它（"mica"/"acrylic"/"solid"），不传读设置。
    /// </summary>
    public static string Apply(Window win, Panel? root = null, string? prefer = null)
    {
        // ⚠️ 必须排在前面：材质取的是「窗口此刻的深浅色」，主题还没设就等于拿系统的颜色烤死。
        if (root is not null) ThemeHost.Apply(root);

        var kind = string.IsNullOrWhiteSpace(prefer) ? SettingBackdrop() : prefer!;
        kind = string.IsNullOrWhiteSpace(kind) ? "acrylic" : kind.Trim().ToLowerInvariant();

        var applied = SetMaterial(win, root, kind, ThemeHost.CurrentTheme);

        // 运行中切深浅色（ThemeHost.Notify 只改 RequestedTheme，材质不会跟着变）→ 重建一次。
        if (root is not null) HookThemeChange(win, root, applied);

        return applied;
    }

    /// <summary>装上背景材质，返回实际生效的那种（不支持就逐级降级，最后纯色）。</summary>
    private static string SetMaterial(Window win, Panel? root, string kind, ElementTheme theme)
    {
        try
        {
            win.SystemBackdrop = null;
            if (root is not null) root.Background = null;

            // 顺带告诉 DWM 这个窗口是深还是浅：云母 / 亚克力的着色读的是这个属性，
            // 不是 XAML 的 RequestedTheme —— 应用主题与系统相反时，这一句才是真正决定观感的。
            SyncWindowDarkMode(win, IsDark(theme));

            if (kind == "solid")
            {
                Solid(win, root, IsDark(theme));
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

            Solid(win, root, IsDark(theme));
            return "solid";
        }
        catch
        {
            Solid(win, root, IsDark(theme));
            return "solid";
        }
    }

    /// <summary><c>Default</c> = 跟随系统，这时看应用级的 RequestedTheme。</summary>
    private static bool IsDark(ElementTheme theme) => theme switch
    {
        ElementTheme.Dark => true,
        ElementTheme.Light => false,
        _ => Application.Current.RequestedTheme == ApplicationTheme.Dark,
    };

    /// <summary>把「这个窗口是深色」告诉 DWM。拿不到句柄或系统不认就算了（Win10 直接忽略）。</summary>
    private static void SyncWindowDarkMode(Window win, bool dark)
    {
        try
        {
            Core.WindowChrome.SetDarkMode(WindowNative.GetWindowHandle(win), dark);
        }
        catch { /* 句柄还没建好 / 老系统不认识：不影响主流程 */ }
    }

    // 已经挂过 ActualThemeChanged 的根（弱引用）。同一个根被 Apply 两次不必重复挂。
    private static readonly List<WeakReference<FrameworkElement>> Hooked = new();

    private static void HookThemeChange(Window win, Panel root, string kind)
    {
        lock (Hooked)
        {
            foreach (var r in Hooked)
                if (r.TryGetTarget(out var t) && ReferenceEquals(t, root)) return;

            Hooked.RemoveAll(r => !r.TryGetTarget(out _));
            Hooked.Add(new WeakReference<FrameworkElement>(root));
        }

        var weakRoot = new WeakReference<Panel>(root);
        root.ActualThemeChanged += (_, _) =>
        {
            try
            {
                if (!weakRoot.TryGetTarget(out var live) || live is null) return;
                // 这时候 ActualTheme 已经是新的了，直接用它重算一次。
                SetMaterial(win, live, kind, live.ActualTheme);
            }
            catch { }
        };
    }

    private static void Solid(Window win, Panel? root, bool dark)
    {
        try
        {
            win.SystemBackdrop = null;
            if (root is not null) root.Background = new SolidColorBrush(SolidColor(dark));
        }
        catch { }
    }

    /// <summary>纯色兜底：跟主窗口一个色（深浅色各一），别用死黑。</summary>
    private static Windows.UI.Color SolidColor(bool dark)
        => dark
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
