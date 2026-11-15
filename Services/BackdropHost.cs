using System;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassSoftwareHub.Desktop.Services;











public static class BackdropHost
{
    
    
    
    
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
                    
                    win.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
                    return "mica";
                }

                kind = "acrylic";                            
            }

            if (DesktopAcrylicController.IsSupported())
            {
                win.SystemBackdrop = new DesktopAcrylicBackdrop();
                return "acrylic";
            }

            if (MicaController.IsSupported())                
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

    
    private static Windows.UI.Color SolidColor(Panel root)
        => root.ActualTheme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(255, 32, 32, 32)
            : Windows.UI.Color.FromArgb(255, 243, 243, 243);

    
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
