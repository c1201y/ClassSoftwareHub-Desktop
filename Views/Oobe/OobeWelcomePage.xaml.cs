using System;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ClassSoftwareHub.Desktop.Views.Oobe;






public sealed partial class OobeWelcomePage : Page
{
    private bool _revealed;

    public OobeWelcomePage()
    {
        InitializeComponent();
        LoadCaveat();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (OobeSession.Current is { } session) session.Started += OnSessionStarted;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (OobeSession.Current is { } session) session.Started -= OnSessionStarted;
    }

    
    private void OnSessionStarted(object? sender, EventArgs e) => Intro.Play();

    
    private void LoadCaveat()
    {
        if (SystemInfo.IsWin11)
        {
            Caveat.Visibility = Visibility.Collapsed;
            return;
        }

        Caveat.Text = "部分功能仅支持 Windows 11，在 Windows 10 上会显示为不可用";
    }

    private void Intro_Completed(object sender, EventArgs e)
    {
        if (_revealed) return;
        _revealed = true;

        if (Caveat.Visibility == Visibility.Visible) OobeMotion.Fade(Caveat, 0);
    }
}
