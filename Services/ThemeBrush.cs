using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 按**元素所在那棵树**的主题取色（浅色一套 / 深色一套）。
///
/// ⚠️ 为什么不用 `Application.Current.Resources["TextFillColorPrimaryBrush"]`：
///    那个查的是**应用级**主题（开机时跟着**系统**定死的那个），跟"这个窗口自己 RequestedTheme 设的"
///    **压根不是一回事**。用户把应用切成浅色、而系统还是深色时，代码里这么查拿到的仍是**深色**那套
///    —— 于是浅色面板上顶着白字（Nick：'悬浮窗口的字是白的，这不适配浅色模式'）就来了。
///    结论：**XAML 里一律用 `{ThemeResource ...}`**（它认识树）；**代码里一律走这里**。
///
/// 颜色值抄自 WinUI generic.xaml（WASDK 2.5.1）的 Light / Default(=深色) 两个主题字典，
/// 提取脚本 `_csh_scratch\extract-colors2.mjs`。
/// </summary>
public static class ThemeBrush
{
    // (浅色, 深色)，0xAARRGGBB
    private static readonly Dictionary<string, (uint Light, uint Dark)> Table = new(StringComparer.Ordinal)
    {
        ["TextFillColorPrimary"] = (0xE4000000, 0xFFFFFFFF),
        ["TextFillColorSecondary"] = (0x9E000000, 0xC5FFFFFF),
        ["TextFillColorTertiary"] = (0x72000000, 0x87FFFFFF),
        ["TextFillColorDisabled"] = (0x5C000000, 0x5DFFFFFF),
        ["TextOnAccentFillColorPrimary"] = (0xFFFFFFFF, 0xFF000000),
        ["ControlFillColorSecondary"] = (0x80F9F9F9, 0x15FFFFFF),
        ["ControlFillColorTertiary"] = (0x4DF9F9F9, 0x08FFFFFF),
        ["SubtleFillColorSecondary"] = (0x09000000, 0x0FFFFFFF),
        ["SubtleFillColorTertiary"] = (0x06000000, 0x0AFFFFFF),
        ["ControlStrokeColorDefault"] = (0x0F000000, 0x12FFFFFF),
        ["CardStrokeColorDefault"] = (0x0F000000, 0x19000000),
        ["CardBackgroundFillColorDefault"] = (0xB3FFFFFF, 0x0DFFFFFF),
        ["LayerFillColorDefault"] = (0x80FFFFFF, 0x4C3A3A3A),
        ["SystemFillColorSuccess"] = (0xFF0F7B0F, 0xFF6CCB5F),
        ["SystemFillColorCaution"] = (0xFF9D5D00, 0xFFFCE100),
        ["SystemFillColorCritical"] = (0xFFC42B1C, 0xFFFF99A4),
        ["SystemFillColorAttention"] = (0xFF005FB8, 0xFF60CDFF),
    };

    /// <summary>元素所在那棵树是深色吗。</summary>
    public static bool IsDark(FrameworkElement? el)
    {
        var t = el?.ActualTheme ?? ElementTheme.Default;
        if (t != ElementTheme.Default) return t == ElementTheme.Dark;

        // ⚠️ 元素**还没挂到树上**时 ActualTheme 就是 Default（页面在构造里就把元素建好、颜色也算好了，
        //    这时候它还没进树）——以前这里直接落回 Application.RequestedTheme（跟系统走），
        //    于是"应用浅色 + 系统深色"时，构造期算出来的颜色全是深色那套 → 页面看着没做浅色适配。
        //    正确的兜底顺序：主窗口那棵（已经挂好、主题是确定的）→ 设置里那个。
        try
        {
            if (App.MainWindow?.Content is FrameworkElement main && main.ActualTheme != ElementTheme.Default)
                return main.ActualTheme == ElementTheme.Dark;
        }
        catch { }

        try
        {
            var s = App.Settings.Current;
            var name = (s.SplitTheme ? s.ExternalTheme : s.Theme)?.Trim().ToLowerInvariant();
            if (name == "light") return false;
            if (name == "dark") return true;
        }
        catch { }

        return Application.Current.RequestedTheme == ApplicationTheme.Dark;
    }

    /// <summary>诊断用：把一次取色的全部依据写进 theme.log（Nick 反馈白字问题时用，定位完可留）。</summary>
    public static void Probe(FrameworkElement? el, string tag)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(" [").Append(tag).Append("] ");
            sb.Append("el=").Append(el?.GetType().Name ?? "null");
            sb.Append(" elTheme=").Append(el?.ActualTheme.ToString() ?? "-");
            try { sb.Append(" xamlRootTheme=").Append((el?.XamlRoot?.Content as FrameworkElement)?.ActualTheme.ToString() ?? "-"); } catch { sb.Append(" xamlRootTheme=!"); }
            try { sb.Append(" mainWinTheme=").Append((App.MainWindow?.Content as FrameworkElement)?.ActualTheme.ToString() ?? "-"); } catch { sb.Append(" mainWinTheme=!"); }
            sb.Append(" sysTheme=").Append(Application.Current.RequestedTheme);
            try
            {
                var s = App.Settings.Current;
                sb.Append(" set.Theme=").Append(s.Theme).Append(" split=").Append(s.SplitTheme).Append(" ext=").Append(s.ExternalTheme);
            }
            catch { sb.Append(" set=!"); }
            sb.Append(" isDark=").Append(IsDark(el));
            var b = (SolidColorBrush)Get(el, "TextFillColorPrimaryBrush");
            sb.Append(" text=#").Append(b.Color.A.ToString("X2")).Append(b.Color.R.ToString("X2")).Append(b.Color.G.ToString("X2")).Append(b.Color.B.ToString("X2"));
            var c = (SolidColorBrush)Get(el, "CardBackgroundFillColorDefaultBrush");
            sb.Append(" card=#").Append(c.Color.A.ToString("X2")).Append(c.Color.R.ToString("X2")).Append(c.Color.G.ToString("X2")).Append(c.Color.B.ToString("X2"));
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassSoftwareHub");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "theme.log"), sb.AppendLine().ToString());
        }
        catch { }
    }

    /// <summary>按树主题取笔刷；传的 key 跟 XAML 里那个名字一样（如 "TextFillColorPrimaryBrush"）。</summary>
    public static Brush Get(FrameworkElement? el, string key)
    {
        // ⚠️ 这里必须砍掉 **5 个字符**的 "Brush"。以前写的是 key[..^1]（只砍一个字符），
        //    "TextFillColorPrimaryBrush" -> "TextFillColorPrimaryBrus" 在表里查不到 ->
        //    静默掉进下面"应用级查找"的兜底，而应用级主题跟**系统**走 → 浅色界面上全是白字。
        //    所有代码画的颜色都栽在这一行上（XAML 里用 {ThemeResource} 的地方不受影响）。
        var k = key.EndsWith("Brush", StringComparison.Ordinal) ? key[..^"Brush".Length] : key;
        switch (k)
        {
            case "AccentFillColorDefault": return Accent(el);
            case "AccentTextFillColorPrimary": return AccentText(el);
        }

        if (Table.TryGetValue(k, out var p)) return new SolidColorBrush(FromArgb(IsDark(el) ? p.Dark : p.Light));

        // 兜底：应用级查找（变体可能不对，但至少有颜色，总比没有强）
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b) return b;
        }
        catch { }
        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    /// <summary>强调色填充（≈ AccentFillColorDefaultBrush）。</summary>
    public static SolidColorBrush Accent(FrameworkElement? el)
        => new(Ramp(IsDark(el) ? "SystemAccentColorLight2" : "SystemAccentColorDark1") ?? AccentColor());

    /// <summary>强调色的**文字**用色（≈ AccentTextFillColorPrimaryBrush）。</summary>
    public static SolidColorBrush AccentText(FrameworkElement? el)
        => new(Ramp(IsDark(el) ? "SystemAccentColorLight3" : "SystemAccentColorDark2") ?? Ramp("SystemAccentColor") ?? AccentColor());

    /// <summary>系统强调色（读不到色阶时的兜底）。</summary>
    public static Color AccentColor()
    {
        try { return new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent); }
        catch { return Color.FromArgb(255, 0, 120, 212); }
    }

    private static Color? Ramp(string key)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var v) && v is Color c) return c;
        }
        catch { }
        return null;
    }

    private static Color FromArgb(uint v)
        => Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
}
