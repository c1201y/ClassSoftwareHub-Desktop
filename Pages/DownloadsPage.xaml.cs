using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 「任务进行」：正在下载 / 已下载完的任务一览。
///
/// 这一页解决的是「下载时只能盯着进度条」—— 下载本身跑在
/// <see cref="DownloadManager"/> 里，就算把首页的下载弹窗收掉，任务也一直在这儿，
/// 随时能看进度、取消、或打开下好的文件（左侧导航上的数字徽标也是它）。
/// </summary>
public sealed partial class DownloadsPage : Page
{
    private bool _hooked;

    public DownloadsPage()
    {
        InitializeComponent();
        TaskList.ItemsSource = DownloadManager.Current.Tasks;

        // 页面用一次就丢（Frame 的 CacheSize=0），所以离开时必须把订阅摘掉，
        // 否则 DownloadManager 这个进程级单例会把已经没人要的页面一直攥着。
        Loaded += (_, _) => Hook();
        Unloaded += (_, _) => Unhook();
    }

    private void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        DownloadManager.Current.Changed += Refresh;
        Refresh();
    }

    private void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        DownloadManager.Current.Changed -= Refresh;
    }

    /// <summary>刷顶部汇总行 + 空状态 + 底部按钮的可用性（每条任务自己的卡片靠绑定自己更新）。</summary>
    private void Refresh()
    {
        var all = DownloadManager.Current.Tasks;
        var active = DownloadManager.Current.ActiveCount;
        var finished = all.Count - active;

        SummaryText.Text = all.Count == 0
            ? "下载任务不会占用界面，可随时切换到其他页面；下载完成后会弹出系统通知。"
            : active > 0
                ? $"正在下载 {active} 个" + (finished > 0 ? $"　·　已完成 {finished} 个" : "")
                : $"没有正在下载的任务　·　已完成 {finished} 个";

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

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is { } task) DownloadManager.Current.Remove(task.Id);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is { Path: { Length: > 0 } path } task) App.MainWindow?.OpenFile(path);
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is { Path: { Length: > 0 } path } task) App.MainWindow?.RevealFile(path);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => DownloadService.OpenDownloadsFolder();

    private void ClearFinished_Click(object sender, RoutedEventArgs e) => DownloadManager.Current.ClearFinished();

    private static DownloadTask? Tagged(object sender) =>
        (sender as FrameworkElement)?.Tag as DownloadTask;
}
