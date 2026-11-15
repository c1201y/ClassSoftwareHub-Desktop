using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace ClassSoftwareHub.Desktop.Views.Oobe;

/// <summary>
/// 第 7 步：侧边栏大小。收起条 / 展开面板分别选样式；高度上限与自适应只对单列（紧凑模式）有意义。
/// 只改草稿，收尾时统一重建侧边栏。
/// </summary>
public sealed partial class OobeSidebarSizePage : Page
{
    // 必须是 true：XAML 里 Slider.Value="75" 在 InitializeComponent 期间就会触发 ValueChanged，
    // 那时若守卫是 false，会把草稿里的高度比例改写成默认值。
    private bool _loading = true;

    public OobeSidebarSizePage()
    {
        InitializeComponent();

        var draft = OobeSession.Current!.Draft;
        SelectTag(DensityCombo, draft.SidebarDensity);
        SelectTag(ExpandedDensityCombo, draft.SidebarExpandedDensity);
        SelectTag(ColumnsCombo, draft.SidebarColumns == 3 ? "3" : "1");

        var percent = Math.Clamp((int)Math.Round(draft.SidebarHeightRatio * 100), 45, 95);
        HeightRatioSlider.Value = percent;
        HeightRatioText.Text = percent + "%";
        HeightAutoSwitch.IsOn = draft.SidebarHeightAuto;
        _loading = false;

        UpdateEnabledState();

        // 页面被缓存：从上一页改完开关再翻回来时，置灰状态要重算
        Loaded += (_, _) => UpdateEnabledState();
    }

    private OobeDraft? Draft => OobeSession.Current?.Draft;

    private static void SelectTag(ComboBox combo, string tag)
    {
        var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == tag);
        combo.SelectedItem = item ?? combo.Items.OfType<ComboBoxItem>().First();
    }

    private void Density_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (DensityCombo.SelectedItem is not ComboBoxItem { Tag: string style }) return;

        var draft = Draft;
        if (draft is null) return;

        draft.SidebarDensity = style == "compact" ? "compact" : "standard";
    }

    private void ExpandedDensity_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ExpandedDensityCombo.SelectedItem is not ComboBoxItem { Tag: string style }) return;

        var draft = Draft;
        if (draft is null) return;

        draft.SidebarExpandedDensity = style is "standard" or "compact" ? style : "same";
    }

    private void Columns_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ColumnsCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;

        var draft = Draft;
        if (draft is null) return;

        draft.SidebarColumns = tag == "3" ? 3 : 1;
        UpdateEnabledState();
    }

    private void HeightRatio_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;

        // XAML 已把范围限在 45~95，这里再夹一道并吸到 5 的整数倍
        var percent = Math.Clamp(
            (int)Math.Round(e.NewValue / 5.0, MidpointRounding.AwayFromZero) * 5, 45, 95);

        var draft = Draft;
        if (draft is null) return;

        draft.SidebarHeightRatio = percent / 100.0;
        if (HeightRatioText is not null) HeightRatioText.Text = percent + "%";
    }

    private void HeightAuto_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var draft = Draft;
        if (draft is not null) draft.SidebarHeightAuto = HeightAutoSwitch.IsOn;
    }

    /// <summary>
    /// 侧边栏关掉 → 全部置灰；选了舒展模式（每行三个）→ 高度上限与自适应没有意义，单独置灰。
    /// </summary>
    private void UpdateEnabledState()
    {
        var draft = Draft;
        var on = draft?.SidebarEnabled != false;
        var singleColumn = draft?.SidebarColumns != 3;

        DensityCard.IsEnabled = on;
        ExpandedDensityCard.IsEnabled = on;
        ColumnsCard.IsEnabled = on;
        HeightCard.IsEnabled = on && singleColumn;
        HeightAutoCard.IsEnabled = on && singleColumn;
        OffHint.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }
}
