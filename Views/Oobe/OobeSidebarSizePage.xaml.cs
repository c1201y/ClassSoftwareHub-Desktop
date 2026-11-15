using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace ClassSoftwareHub.Desktop.Views.Oobe;





public sealed partial class OobeSidebarSizePage : Page
{
    
    
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
