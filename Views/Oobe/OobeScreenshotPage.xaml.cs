using System;
using System.Diagnostics;
using System.IO;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Views.Oobe;


public sealed partial class OobeScreenshotPage : Page
{
    private bool _loading = true;

    public OobeScreenshotPage()
    {
        InitializeComponent();

        ShotAutoSaveSwitch.IsOn = OobeSession.Current!.Draft.ShotAutoSave;
        _loading = false;

        RefreshDir();
    }

    private OobeDraft? Draft => OobeSession.Current?.Draft;

    private void ShotAutoSave_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var draft = Draft;
        if (draft is null) return;

        draft.ShotAutoSave = ShotAutoSaveSwitch.IsOn;
        UpdateEnabledState();
    }

    private async void ShotDir_Change_Click(object sender, RoutedEventArgs e)
    {
        var session = OobeSession.Current;
        if (session is null) return;

        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop,
            };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, session.WindowHandle);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            session.Draft.ShotSaveDir = folder.Path;
            RefreshDir();
        }
        catch (Exception ex)
        {
            AppLog.Warn("oobe", "选择截图目录失败：" + ex.Message);
        }
    }

    private void ShotDir_Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = EffectiveDir();
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn("oobe", "打开截图目录失败：" + ex.Message);
        }
    }

    private void ShotDir_Reset_Click(object sender, RoutedEventArgs e)
    {
        var draft = Draft;
        if (draft is null) return;

        draft.ShotSaveDir = "";
        RefreshDir();
    }

    
    private string EffectiveDir()
    {
        var dir = (Draft?.ShotSaveDir ?? "").Trim();
        return dir.Length > 0 ? dir : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    private void RefreshDir()
    {
        var dir = (Draft?.ShotSaveDir ?? "").Trim();
        ShotDirText.Text = dir.Length > 0 ? dir : "桌面（默认）";
        UpdateEnabledState();
    }

    private void UpdateEnabledState() => DirCard.IsEnabled = ShotAutoSaveSwitch.IsOn;
}
