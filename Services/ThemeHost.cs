using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 主题（深浅色）分发。
///
/// 为啥要它：WinUI3 里 `RequestedTheme` **只作用于设它的那棵树**，主窗口设了不代表别的窗口跟着变 ——
/// 于是侧边栏 / 常用工具浮窗 / 截图的那几个窗会一直跟着**系统**主题走，
/// 用户把应用切成浅色，它们还是深色（"只做了深色没做浅色"就是这么来的）。
///
/// 用法：每个自己开出来的窗口，构造时来一句 `ThemeHost.Apply(Root)`；
/// 用户改设置时主窗口叫一声 `ThemeHost.Notify()`，登记过的根一起换。
/// </summary>
public static class ThemeHost
{
    private static readonly List<WeakReference<FrameworkElement>> Roots = new();

    /// <summary>设置里的主题（system | light | dark）映射成 ElementTheme。</summary>
    public static ElementTheme Map(string? theme) => (theme ?? "").Trim().ToLowerInvariant() switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    /// <summary>把当前设置的主题应用到某个窗口的根元素，并登记（之后改设置会一起变）。</summary>
    public static void Apply(FrameworkElement root)
    {
        try
        {
            root.RequestedTheme = Map(CurrentSetting());
            Roots.RemoveAll(r => !r.TryGetTarget(out _));
            Roots.Add(new WeakReference<FrameworkElement>(root));
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("应用窗口主题失败: " + ex.Message);
        }
    }

    /// <summary>设置变了：所有登记过的窗口根元素跟着换。</summary>
    public static void Notify()
    {
        var theme = Map(CurrentSetting());
        foreach (var r in Roots)
        {
            try
            {
                if (r.TryGetTarget(out var root) && root is not null)
                    root.RequestedTheme = theme;
            }
            catch { }
        }
    }

    private static string CurrentSetting()
    {
        try
        {
            var s = App.Settings.Current;
            if (s.SplitTheme)                                     // 分体：外部组件自己一套外观
                return string.IsNullOrWhiteSpace(s.ExternalTheme) ? "system" : s.ExternalTheme;
            return s.Theme;
        }
        catch { return "system"; }
    }
}
