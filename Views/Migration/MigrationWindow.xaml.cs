using System;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views.Migration;

/// <summary>
/// 设置迁移窗口：导出与导入同页并列，不再分步（2026-11-21 Nick 定）。
/// 窗口不可最大化，避免被当成主界面使用。
/// </summary>
public sealed partial class MigrationWindow : Window
{
    private static MigrationWindow? _instance;

    private AppWindow? _appWindow;

    private MigrationWindow()
    {
        InitializeComponent();
        Configure();
    }

    public static void Show()
    {
        if (_instance is not null)
        {
            _instance.Activate();
            return;
        }

        try
        {
            _instance = new MigrationWindow();
            _instance.Activate();
            AppLog.Info("migration", "设置迁移窗口已打开");
        }
        catch (Exception ex)
        {
            AppLog.Exception("migration", ex, "设置迁移窗口创建失败");
            _instance = null;
        }
    }

    private void Configure()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "设置迁移";

            // 这一页只做导入导出，不需要最大化；最小化也一并去掉，与分享窗口保持一致。
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
            }

            ModernWindow.ApplyTitleBar(this, TitleBarHost, Root);
            ThemeHost.Apply(Root);
            BackdropHost.Apply(this, Root, App.Settings.Current.Backdrop);

            _appWindow.Resize(new SizeInt32(760, 600));
            CenterOnScreen();

            Closed += (_, _) => _instance = null;
        }
        catch (Exception ex)
        {
            AppLog.Exception("migration", ex, "设置迁移窗口初始化");
        }
    }

    private void CenterOnScreen()
    {
        if (_appWindow is null) return;

        try
        {
            var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
            var work = area.WorkArea;
            var size = _appWindow.Size;
            _appWindow.Move(new PointInt32(
                work.X + Math.Max(0, (work.Width - size.Width) / 2),
                work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
        }
        catch { }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = $"classsoftwarehub-settings-{ShellConfig.ShellVersion}.json",
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeChoices.Add("JSON 设置包", new[] { ".json" });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            var target = await picker.PickSaveFileAsync();
            if (target is null) return;

            await Windows.Storage.FileIO.WriteTextAsync(
                target, ConfigMigration.Export(App.Settings.Current));
            Show("已导出到：" + target.Path, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Warn("migration", "导出失败：" + ex.Message);
            Show("导出失败，请换一个位置再试。", InfoBarSeverity.Error);
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
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var json = await Windows.Storage.FileIO.ReadTextAsync(file);
            var imported = ConfigMigration.Import(json);
            var differences = ConfigMigration.Preview(imported);
            var warnings = ConfigMigration.PathWarnings(imported);

            DiffCard.Visibility = Visibility.Visible;

            if (differences.Count == 0 && warnings.Count == 0)
            {
                DiffText.Text = "没有需要应用的变化。";
                Show("导入包与当前设置一致。", InfoBarSeverity.Informational);
                return;
            }

            DiffText.Text = string.Join("\n", differences);
            if (warnings.Count > 0) DiffText.Text += "\n\n" + string.Join("\n", warnings);

            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "确认导入设置",
                Content = "将用导入包覆盖上面列出的设置。窗口位置、截图目录、下载目录等本机字段不会导入。",
                PrimaryButtonText = "应用导入设置",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var backup = ConfigMigration.BackupCurrent();
            ConfigMigration.MergeIntoCurrent(imported);
            App.MainWindow?.RefreshHotkeys();
            ToolSidebarWindow.ApplyModules();
            Show("导入成功。原设置已备份到：" + backup, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Warn("migration", "导入失败：" + ex.Message);
            Show("导入失败，请确认文件来自本程序。", InfoBarSeverity.Error);
        }
    }

    private void Show(string message, InfoBarSeverity severity)
    {
        ResultBar.IsOpen = true;
        ResultBar.Severity = severity;
        ResultBar.Message = message;
    }
}
