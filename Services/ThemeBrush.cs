using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ClassSoftwareHub.Desktop.Services;













public static class ThemeBrush
{
    
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
        
        
        ["SystemFillColorSuccessBackground"] = (0xFFDFF6DD, 0xFF393D1B),
        ["SystemFillColorCautionBackground"] = (0xFFFFF4CE, 0xFF433519),
        ["SystemFillColorCriticalBackground"] = (0xFFFDE7E9, 0xFF442726),
        ["SystemFillColorAttentionBackground"] = (0x80F6F6F6, 0x08FFFFFF),
    };

    
    public static bool IsDark(FrameworkElement? el)
    {
        var t = el?.ActualTheme ?? ElementTheme.Default;
        if (t != ElementTheme.Default) return t == ElementTheme.Dark;

        
        
        
        
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
            Core.AppLog.Info("theme", sb.ToString());
        }
        catch { }
    }

    
    public static Brush Get(FrameworkElement? el, string key)
    {
        
        
        
        
        var k = key.EndsWith("Brush", StringComparison.Ordinal) ? key[..^"Brush".Length] : key;
        switch (k)
        {
            case "AccentFillColorDefault": return Accent(el);
            case "AccentTextFillColorPrimary": return AccentText(el);
        }

        if (Table.TryGetValue(k, out var p)) return new SolidColorBrush(FromArgb(IsDark(el) ? p.Dark : p.Light));

        
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b) return b;
        }
        catch { }
        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    
    public static SolidColorBrush Accent(FrameworkElement? el)
        => new(Ramp(IsDark(el) ? "SystemAccentColorLight2" : "SystemAccentColorDark1") ?? AccentColor());

    
    public static SolidColorBrush AccentText(FrameworkElement? el)
        => new(Ramp(IsDark(el) ? "SystemAccentColorLight3" : "SystemAccentColorDark2") ?? Ramp("SystemAccentColor") ?? AccentColor());

    
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
