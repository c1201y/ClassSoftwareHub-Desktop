using System;
using System.IO;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Pages;








public sealed partial class DownloadsPage : Page
{
    private bool _hooked;
    private bool _loadingSettings;      

    public DownloadsPage()
    {
        InitializeComponent();
        TaskList.ItemsSource = DownloadManager.Current.Tasks;

        _loadingSettings = true;
        AutoDeleteBox.IsChecked = App.Settings.Current.AutoDeletePackage;
        AutoOpenBox.IsChecked = App.Settings.Current.AutoOpenExtractFolder;
        _loadingSettings = false;

        
        
        Loaded += (_, _) => Hook();
        Unloaded += (_, _) => Unhook();
    }

    private void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        DownloadManager.Current.Changed += Refresh;

        
        
        Services.Updating.UpdateFlow.EnsurePendingTaskVisible();

        Refresh();
    }

    private void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        DownloadManager.Current.Changed -= Refresh;
    }

    
    private void Refresh()
    {
        var all = DownloadManager.Current.Tasks;
        var active = DownloadManager.Current.ActiveCount;
        var finished = all.Count - active;

        SummaryText.Text = all.Count == 0
            ? "下载任务不占用界面，可随时切换至其他页面；下载完成后显示系统通知。"
            : active > 0
                ? $"正在下载 {active} 个" + (finished > 0 ? $"　·　已完成 {finished} 个" : "")
                : $"当前无进行中的下载任务　·　已完成 {finished} 个";

        EmptyPanel.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = finished > 0;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is { } task) DownloadManager.Current.Cancel(task.Id);
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is { } task) DownloadManager.Current.Retry(task.Id);
    }

    
    
    
    
    
    
    
    
    
    
    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DownloadTask task } fe) return;

        var flyout = new MenuFlyout();

        var remove = new MenuFlyoutItem
        {
            Text = "从列表移除",
            Icon = new FontIcon { Glyph = "\uE894" },   
        };
        remove.Click += (_, _) => DownloadManager.Current.Remove(task.Id);
        flyout.Items.Add(remove);

        
        if (task.CanOpen)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());

            var del = new MenuFlyoutItem
            {
                Text = "删除文件",
                Icon = new FontIcon { Glyph = "\uE74D" },   
            };
            del.Click += (_, _) => _ = DeleteFileAsync(task);
            flyout.Items.Add(del);
        }

        flyout.ShowAt(fe);
    }

    
    
    
    
    
    
    
    
    private async Task<bool> ConfirmDeleteAsync(DownloadTask task, string path)
    {
        var size = task.Bytes > 0 ? Core.AppLog.Size(task.Bytes) + "　·　" : "";
        var dialog = new ContentDialog
        {
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
            XamlRoot = XamlRoot,
            Title = "删除已下载的文件？",
            Content = new TextBlock
            {
                Text = $"{task.FileName}\n{size}{path}\n\n"
                     + "文件将被永久删除，不进入回收站；删除后如需使用需要重新下载。",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            
            DefaultButton = ContentDialogButton.Close,
        };

        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("download", ex, "弹出「删除已下载的文件」确认框");
            return false;
        }
    }

    
    
    
    
    
    
    
    
    private async Task DeleteFileAsync(DownloadTask task)
    {
        if (task.Path is not { Length: > 0 } path) return;

        if (!await ConfirmDeleteAsync(task, path)) return;

        try
        {
            File.Delete(path);
            Core.AppLog.Done("download", $"已删除已下载的文件「{task.Title}」→ {path}");

            var s = App.Settings.Current;
            if (string.Equals(s.UpdatePendingPath, path, StringComparison.OrdinalIgnoreCase))
            {
                s.UpdatePendingPath = "";
                s.UpdatePendingTag = "";
                App.Settings.Save();
                Core.AppLog.Info("update", "随文件删除，待装标记已一并清除");
            }

            DownloadManager.Current.Remove(task.Id);
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("download", ex, "删除已下载的文件：" + path);

            var fail = new ContentDialog
            {
                        RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
                XamlRoot = XamlRoot,
                Title = "没能删除",
                Content = new TextBlock
                {
                    Text = "文件可能正被其他程序占用（比如安装程序还在运行）。关掉它之后再试一次。",
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                },
                CloseButtonText = "关闭",
            };
            await fail.ShowAsync();
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is { Path: { Length: > 0 } path } task) App.MainWindow?.OpenFile(path);
    }

    
    
    
    
    
    
    
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is not { Path: { Length: > 0 } path }) return;
        await Services.Updating.UpdateFlow.InstallLocalAsync(path);
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        
        if (Tagged(sender) is { PreferredRevealPath: { Length: > 0 } path })
            App.MainWindow?.RevealFile(path);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => DownloadService.OpenDownloadsFolder();

    private void ClearFinished_Click(object sender, RoutedEventArgs e) => DownloadManager.Current.ClearFinished();

    
    
    
    
    
    
    private void PackageAction_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is not { } task) return;
        _ = PackageRunner.RunAsync(task);   
    }

    
    private void OpenExtracted_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is { ExtractedDir: { Length: > 0 } dir })
            App.MainWindow?.RevealFile(dir);
    }

    
    private void AutoDelete_Changed(object sender, RoutedEventArgs e)
        => SaveSwitch(AutoDeleteBox, v => App.Settings.Current.AutoDeletePackage = v,
            App.Settings.Current.AutoDeletePackage, "安装或解压完成后自动删除安装包或压缩包");

    
    private void AutoOpen_Changed(object sender, RoutedEventArgs e)
        => SaveSwitch(AutoOpenBox, v => App.Settings.Current.AutoOpenExtractFolder = v,
            App.Settings.Current.AutoOpenExtractFolder, "解压完成后自动打开所在文件夹");

    
    private void SaveSwitch(CheckBox box, Action<bool> write, bool current, string label)
    {
        if (_loadingSettings) return;
        var on = box.IsChecked == true;
        if (current == on) return;

        write(on);
        App.Settings.Save();
        Core.AppLog.Info("package", $"「{label}」已{(on ? "开启" : "关闭")}");
    }

    private static DownloadTask? Tagged(object sender) =>
        (sender as FrameworkElement)?.Tag as DownloadTask;
}
