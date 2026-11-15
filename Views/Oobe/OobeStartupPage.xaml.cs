using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Views.Oobe;




public sealed partial class OobeStartupPage : Page
{
    private bool _loading;

    public OobeStartupPage()
    {
        InitializeComponent();

        _loading = true;
        var draft = OobeSession.Current!.Draft;
        AutoStartSwitch.IsOn = draft.AutoStart;
        MinimizeSwitch.IsOn = draft.MinimizeOnStart;
        TraySwitch.IsOn = draft.CloseToTray;
        _loading = false;

        UpdateMinimizeState();
    }

    private void AutoStart_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var session = OobeSession.Current;
        if (session is null) return;

        session.Draft.AutoStart = AutoStartSwitch.IsOn;
        UpdateMinimizeState();
    }

    private void Minimize_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var session = OobeSession.Current;
        if (session is not null) session.Draft.MinimizeOnStart = MinimizeSwitch.IsOn;
    }

    private void Tray_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var session = OobeSession.Current;
        if (session is not null) session.Draft.CloseToTray = TraySwitch.IsOn;
    }

    
    private void UpdateMinimizeState()
    {
        var on = AutoStartSwitch.IsOn;
        MinimizeCard.IsEnabled = on;
        MinimizeHint.Opacity = on ? 0.55 : 0.4;
        MinimizeHint.Text = on
            ? "开机启动时直接收进托盘（任务栏上不留按钮），想用的时候点托盘图标。"
            : "只有打开「开机自动启动」时，这个选项才有用。";
    }
}
