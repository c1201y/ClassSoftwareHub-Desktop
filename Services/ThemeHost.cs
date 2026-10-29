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
/// 用法：每个自己开出来的窗口，构造时来一句 `ThemeHost.Apply(Root, external: …)`；
/// 用户改设置时主窗口叫一声 `ThemeHost.Notify()`，登记过的根一起换。
///
/// <para>
/// ⛔⛔ **两套主题，别搞混**（2026-10-29 Nick 定的口径）：
/// <list type="bullet">
///   <item><b>应用主题</b>（设置里的「主题」）：**其余所有窗口都跟这个**
///         —— 主界面、学科工具窗（数学工具）、音量浮窗、截图窗、条款与声明、日志详细视图……</item>
///   <item><b>外部组件主题</b>（「桌面悬浮窗单独设置外观」+「外部组件外观」）：
///         **只作用于「桌面悬浮件」** —— ①「常用工具」浮窗（随机抽号 / 课堂计时 / 秒表计时 / 全屏时钟）；
///         ② 屏幕边缘的工具侧边栏。这两样常年压在桌面和 PPT 上，需要单独一套观感。</item>
/// </list>
/// 以前这里只有一个 `Apply(root)`，分体开关一开，条款窗和日志窗也去读外部主题 ——
/// 表现出来就是主界面浅色、这两个窗整片发黑（它们的根是透明的，观感全压在背景材质上）。
/// ⇒ 只有 <c>ToolPaletteWindow</c>（常用工具浮窗）与 <c>ToolSidebarWindow</c>（侧边栏）传 <c>external: true</c>，其余一律不要。
/// </para>
/// </summary>
public static class ThemeHost
{
    // 登记过的根 + 它跟的是哪一套主题（弱引用，窗口关了自然失效）。
    private static readonly List<(WeakReference<FrameworkElement> Root, bool External)> Roots = new();

    /// <summary>设置里的主题（system | light | dark）映射成 ElementTheme。</summary>
    public static ElementTheme Map(string? theme) => (theme ?? "").Trim().ToLowerInvariant() switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    /// <summary>
    /// 当前该用哪套深浅色。<paramref name="external"/> = 这个窗口属于「桌面悬浮窗」那一类。
    /// <para>
    /// ⚠️ 也给 <c>BackdropHost</c> 这类**不能等布局周期**的调用方用（2026-10-29）：
    /// 刚设完 <c>RequestedTheme</c> 时根的 <c>ActualTheme</c> 还没刷新，拿它算深浅色会算成旧值，
    /// 背景材质就按旧值烤死了。这里直接读设置，结果与布局时机无关。
    /// </para>
    /// </summary>
    public static ElementTheme ThemeFor(bool external) => external ? ExternalTheme : AppTheme;

    /// <summary>主界面 / 文档窗口用的主题。</summary>
    public static ElementTheme CurrentTheme => AppTheme;

    private static ElementTheme AppTheme
    {
        get
        {
            try { return Map(App.Settings.Current.Theme); }
            catch { return ElementTheme.Default; }
        }
    }

    /// <summary>桌面悬浮组件用的主题：分体关着时跟应用主题一致。</summary>
    private static ElementTheme ExternalTheme
    {
        get
        {
            try
            {
                var s = App.Settings.Current;
                if (!s.SplitTheme) return Map(s.Theme);
                return Map(string.IsNullOrWhiteSpace(s.ExternalTheme) ? "system" : s.ExternalTheme);
            }
            catch { return ElementTheme.Default; }
        }
    }

    /// <summary>
    /// 把主题应用到某个窗口的根元素，并登记（之后改设置会一起变）。
    /// <paramref name="external"/> 只对「桌面悬浮窗」那一类传 true。
    /// </summary>
    public static void Apply(FrameworkElement root, bool external = false)
    {
        try
        {
            root.RequestedTheme = ThemeFor(external);

            Roots.RemoveAll(r => !r.Root.TryGetTarget(out _));
            Roots.RemoveAll(r => r.Root.TryGetTarget(out var t) && ReferenceEquals(t, root));
            Roots.Add((new WeakReference<FrameworkElement>(root), external));
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("应用窗口主题失败: " + ex.Message);
        }
    }

    /// <summary>设置变了：所有登记过的窗口根元素跟着换（各按各的那一套）。</summary>
    public static void Notify()
    {
        foreach (var (weak, external) in Roots)
        {
            try
            {
                if (weak.TryGetTarget(out var root) && root is not null)
                    root.RequestedTheme = ThemeFor(external);
            }
            catch { }
        }
    }
}
