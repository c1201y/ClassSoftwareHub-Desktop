using System;
using Windows.Storage.Pickers;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Pages;


public sealed partial class MigrationWizardPage : Page
{
    public MigrationWizardPage() => InitializeComponent();

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = $"classsoftwarehub-settings-{Core.ShellConfig.ShellVersion}.json",
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeChoices.Add("JSON 设置包", new[] { ".json" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));
            var target = await picker.PickSaveFileAsync();
            if (target is null) return;
            await Windows.Storage.FileIO.WriteTextAsync(target, ConfigMigration.Export(App.Settings.Current));
            Show("导出成功：" + target.Path, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Warn("migration", "导出失败：" + ex.Message);
            Show("导出失败：" + ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                CommitButtonText = "导入",
            };
            picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            var json = await Windows.Storage.FileIO.ReadTextAsync(file);
            var imported = ConfigMigration.Import(json);
            ConfigMigration.MergeIntoCurrent(imported);
            Show("导入成功。部分外观与性能设置会在重启后完全生效。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Warn("migration", "导入失败：" + ex.Message);
            Show("导入失败：" + ex.Message, InfoBarSeverity.Error);
        }
    }

    private void Show(string message, InfoBarSeverity severity)
    {
        ResultBar.IsOpen = true;
        ResultBar.Severity = severity;
        ResultBar.Message = message;
    }
}
