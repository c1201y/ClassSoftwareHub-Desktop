using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 工具页按键的统一手感：按下去底色换成**主题色渐变**，不是系统默认那层灰。
///
/// <para>
/// ⛔ 不能直接改 <c>Button.Background</c> —— 模板的 Pressed 状态会用自己的画刷把它盖掉。
/// 把 <c>ButtonBackgroundPressed</c> 这个主题资源**覆盖到按钮自己身上**，
/// 模板里的 <c>{ThemeResource}</c> 就会取到我们的渐变。
/// </para>
/// <para>化学式编辑器与方程编辑器共用这一套，手感不能一处一样一处不一样（Nick 2026-10-17）。</para>
/// </summary>
internal static class ButtonFeel
{
    /// <summary>给按钮挂上「按下＝主题色渐变」。</summary>
    /// <param name="button">Button 或 ToggleButton（两者都是 Control）。</param>
    /// <param name="dark">渐变末端的压暗系数，越小越深。</param>
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
