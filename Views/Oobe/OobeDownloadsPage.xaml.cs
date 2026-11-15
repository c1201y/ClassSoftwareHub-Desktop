using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Views.Oobe;




public sealed partial class OobeDownloadsPage : Page
{
    private bool _loading;

    public OobeDownloadsPage()
    {
        InitializeComponent();

        _loading = true;
        var draft = OobeSession.Current!.Draft;
        RouteCombo.SelectedIndex = GithubRoutes.Normalize(draft.GithubRoute) switch
        {
            GithubRoutes.SelfHosted => 1,
            GithubRoutes.Official => 2,
            _ => 0,
        };
        AutoCheckSwitch.IsOn = draft.AutoCheckUpdate;

        
        CacheCombo.SelectedIndex = draft.CacheKeepCount >= 10 ? 2 : draft.CacheKeepCount >= 5 ? 1 : 0;
        _loading = false;
    }

    private void Route_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var session = OobeSession.Current;
        if (session is not null) session.Draft.GithubRoute = TagOf(RouteCombo, GithubRoutes.Auto);
    }

    private void Cache_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var session = OobeSession.Current;
        if (session is null) return;
        if (int.TryParse(TagOf(CacheCombo, "3"), out var keep))
            session.Draft.CacheKeepCount = System.Math.Clamp(keep, 1, 10);
    }

    private void AutoCheck_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var session = OobeSession.Current;
        if (session is not null) session.Draft.AutoCheckUpdate = AutoCheckSwitch.IsOn;
    }

    private static string TagOf(ComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;
}
