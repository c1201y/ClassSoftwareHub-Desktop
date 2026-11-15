using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace ClassSoftwareHub.Desktop.Controls;
















internal static class ChipLook
{
    
    public static void Quiet(ToggleButton button)
    {
        void Set(string key, Brush brush)
        {
            try { button.Resources[key] = brush; } catch { }
        }

        var none = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var hover = LogReader.Look("SubtleFillColorSecondaryBrush");
        var press = LogReader.Look("SubtleFillColorTertiaryBrush");
        var onFill = LogReader.Look("ControlFillColorSecondaryBrush");
        var onEdge = LogReader.Look("CardStrokeColorDefaultBrush");
        var onText = LogReader.Look("TextFillColorPrimaryBrush");
        var onTextDim = LogReader.Look("TextFillColorSecondaryBrush");

        Set("ToggleButtonBackground", none);
        Set("ToggleButtonBackgroundPointerOver", hover);
        Set("ToggleButtonBackgroundPressed", press);
        Set("ToggleButtonBorderBrush", none);
        Set("ToggleButtonBorderBrushPointerOver", none);
        Set("ToggleButtonBorderBrushPressed", none);

        Set("ToggleButtonBackgroundChecked", onFill);
        Set("ToggleButtonBackgroundCheckedPointerOver", press);
        Set("ToggleButtonBackgroundCheckedPressed", press);
        Set("ToggleButtonForegroundChecked", onText);
        Set("ToggleButtonForegroundCheckedPointerOver", onText);
        Set("ToggleButtonForegroundCheckedPressed", onTextDim);
        Set("ToggleButtonBorderBrushChecked", onEdge);
        Set("ToggleButtonBorderBrushCheckedPointerOver", onEdge);
        Set("ToggleButtonBorderBrushCheckedPressed", onEdge);
    }
}
