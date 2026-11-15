using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassSoftwareHub.Desktop.Services;











internal static class ButtonFeel
{
    
    
    
    public static void ApplyPressGradient(Control button, double dark = 0.72)
    {
        var accent = (ThemeBrush.Get(null, "AccentFillColorDefaultBrush") as SolidColorBrush)?.Color;
        if (accent is not { } color) return;

        var gradient = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
        };

        gradient.GradientStops.Add(new GradientStop { Color = color, Offset = 0 });
        gradient.GradientStops.Add(new GradientStop
        {
            Color = Microsoft.UI.ColorHelper.FromArgb(255,
                (byte)(color.R * dark), (byte)(color.G * dark), (byte)(color.B * dark)),
            Offset = 1,
        });

        button.Resources["ButtonBackgroundPressed"] = gradient;
    }
}
