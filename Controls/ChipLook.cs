using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace ClassSoftwareHub.Desktop.Controls;

/// <summary>
/// 筛选芯片的观感 —— 把 <see cref="ToggleButton"/> 画成"安静的一枚标签"。
///
/// 2026-10-13 从 <see cref="LevelChipBar"/> 里抽出来，给「应用卸载」页的分类芯片共用
/// （两处芯片长得必须一样，不然同一套界面里出现两种筛选器观感）。
///
///   · off = **无底无边** + 内容压淡（读起来就是一枚普通标签）
///   · on  = 一层浅填充 + 一圈细边 + 主文字（"开着"，但不用 Accent 喊）
/// 选中态不走 Accent：一排 Accent 药丸既刺眼，又违反"一页 Accent 一到两处"（§3）。
///
/// ⚠️⚠️ "无"那一侧一律用 <c>Colors.Transparent</c> 现造，**不要**去查
///   <c>…TransparentBrush</c> 这类主题键：查不到时 <see cref="LogReader.Look"/> 会兜底成
///   不透明灰 ⇒ 关掉的芯片反而多出一圈灰边、比选中的还显眼（2026-10-11 实拍踩到）。
///   透明本身与主题无关，不算"写死颜色"（§2 禁的是 RGB 主题色）。
/// </summary>
internal static class ChipLook
{
    /// <summary>把芯片的 on/off 画刷就地换成安静版本（实例级资源覆盖，模板会先命中这一层）。</summary>
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
