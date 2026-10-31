using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Views.Oobe;

/// <summary>
/// 第 11 步：Hub AI。
/// </summary>
public sealed partial class OobeAiPage : Page
{
    private bool _loading;

    public OobeAiPage()
    {
        InitializeComponent();

        _loading = true;
        var draft = OobeSession.Current!.Draft;
        AiSwitch.IsOn = draft.HubAiEnabled;
        NameBox.Text = draft.UserDisplayName;
        _loading = false;
    }

    private void Ai_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var session = OobeSession.Current;
        if (session is not null) session.Draft.HubAiEnabled = AiSwitch.IsOn;
    }

    private void Name_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        var session = OobeSession.Current;
        if (session is not null) session.Draft.UserDisplayName = NameBox.Text;
    }
}
