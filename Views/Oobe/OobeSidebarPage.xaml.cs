using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Views.Oobe;




public sealed partial class OobeSidebarPage : Page
{
    private bool _loading;

    public OobeSidebarPage()
    {
        InitializeComponent();

        _loading = true;
        var draft = OobeSession.Current!.Draft;
        SidebarSwitch.IsOn = draft.SidebarEnabled;
        PaletteTopSwitch.IsOn = draft.PaletteOnTop;
        LongLabelSwitch.IsOn = draft.SidebarLongLabels;
        PinnedSwitch.IsOn = draft.SidebarPinned;
        _loading = false;

        UpdateEnabledState();
    }

    private void Sidebar_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var session = OobeSession.Current;
        if (session is null) return;

        session.Draft.SidebarEnabled = SidebarSwitch.IsOn;
        UpdateEnabledState();
    }

    private void PaletteTop_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var session = OobeSession.Current;
        if (session is not null) session.Draft.PaletteOnTop = PaletteTopSwitch.IsOn;
    }

    private void LongLabel_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var session = OobeSession.Current;
        if (session is not null) session.Draft.SidebarLongLabels = LongLabelSwitch.IsOn;
    }

    private void Pinned_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var session = OobeSession.Current;
        if (session is not null) session.Draft.SidebarPinned = PinnedSwitch.IsOn;
    }

    
    
    
    
    private void UpdateEnabledState()
    {
        LongLabelCard.IsEnabled = SidebarSwitch.IsOn;
        PinnedCard.IsEnabled = SidebarSwitch.IsOn;
    }
}
