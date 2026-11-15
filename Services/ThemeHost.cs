using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;

namespace ClassSoftwareHub.Desktop.Services;

































public static class ThemeHost
{
    
    private static readonly List<(WeakReference<FrameworkElement> Root, bool External)> Roots = new();

    
    public static ElementTheme Map(string? theme) => (theme ?? "").Trim().ToLowerInvariant() switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    
    
    
    
    
    
    
    
    public static ElementTheme ThemeFor(bool external) => external ? ExternalTheme : AppTheme;

    private static ElementTheme AppTheme
    {
        get
        {
            try { return Map(App.Settings.Current.Theme); }
            catch { return ElementTheme.Default; }
        }
    }

    
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
