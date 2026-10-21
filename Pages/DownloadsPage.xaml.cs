using System;
using System.IO;
using System.Threading.Tasks;
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
    private bool _loadingSettings;      // 初始化开关时别把"设初值"当成用户改的

    public DownloadsPage()
    {
        InitializeComponent();
        TaskList.ItemsSource = DownloadManager.Current.Tasks;

        _loadingSettings = true;
        AutoDeleteBox.IsChecked = App.Settings.Current.AutoDeletePackage;
        AutoOpenBox.IsChecked = App.Settings.Current.AutoOpenExtractFolder;
        _loadingSettings = false;

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

        // 任务列表是进程内的，重开应用就空了 —— 但「下好待装」的那个包还在硬盘上。
        // 进这一页先把它补成一条已完成的任务，否则用户又回到"不知道下没下好"。
        Services.Updating.UpdateFlow.EnsurePendingTaskVisible();

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

    /// <summary>
    /// 行尾「⋯」：这条任务能干的两件事。
    ///
    /// ⛔ 顺序固定 —— **不删东西的放前面**（从列表移除），不可逆的「删除文件」放最后、且要确认。
    /// 之所以补上「删除文件」：以前这里只有"从列表移除"，文件留在硬盘上，用户想删得自己摸到下载
    /// 文件夹里找（Nick 2026-10-16：「删除已下载安装程序（即使未安装）」）。
    ///
    /// ⚠️ 菜单**手建**、闭包直接捕获这一条任务 —— 跟 AppData 管理页同一套做法
    ///    （`MenuFlyout` 写进 `DataTemplate` 时 `{x:Bind}` 求值上下文很脆，本项目有 WMC9999 前科）。
    /// </summary>
    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DownloadTask task } fe) return;

        var flyout = new MenuFlyout();

        var remove = new MenuFlyoutItem
        {
            Text = "从列表移除",
            Icon = new FontIcon { Glyph = "\uE894" },   // Clear（只把这一行从列表里清掉，不动文件）
        };
        remove.Click += (_, _) => DownloadManager.Current.Remove(task.Id);
        flyout.Items.Add(remove);

        // 「删除文件」只在文件**真还在**的时候给 —— 用户可能早把它挪走或删掉了
        if (task.CanOpen)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());

            var del = new MenuFlyoutItem
            {
                Text = "删除文件",
                Icon = new FontIcon { Glyph = "\uE74D" },   // Delete
            };
            del.Click += (_, _) => _ = DeleteFileAsync(task);
            flyout.Items.Add(del);
        }

        flyout.ShowAt(fe);
    }

    /// <summary>
    /// 弹出删除确认框。返回 true = 用户确认要删。
    ///
    /// ⚠️ 为什么单独包一层 try/catch：同一时刻**只允许开一个** `ContentDialog`，
    ///    若此刻还有别的弹窗（典型是「下载完成」那张）没关，`ShowAsync()` 会直接抛。
    ///    正常操作遇不到 —— 模态弹窗会把卡片上的点击挡住；但这一步是不可逆动作，
    ///    ⛔ 不能让它静默失败，所以至少留一条日志。
    /// </summary>
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
            // ⚠️ 不可逆操作，默认落在「取消」上，别让一个回车就删掉
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

    /// <summary>
    /// 把这条任务下载的文件从硬盘上删掉，连带移出列表。
    ///
    /// ⚠️ 是**永久删除、不进回收站** —— 与「设置 → 本地安装包」删更新包的行为保持一致
    ///    （那边 `InstallerCleanup.TryDelete` 也是直接 `File.Delete`），所以确认框里必须写清这一点。
    /// ⚠️ 万一删的是**待装的更新包**，得顺手把待装标记清掉 —— 否则首页会一直挂着
    ///    "XX 已下载就绪"，点下去却找不到文件。（首页那边有兜底自检，但主动清更干净。）
    /// </summary>
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

    /// <summary>
    /// 更新包下完之后的「安装」。
    ///
    /// ⚠️ 必须走 <c>UpdateFlow.InstallLocalAsync</c>（先退应用、后起安装程序）——
    ///    直接双击/Open 那个安装包会被 Inno 的 AppMutex 拦下并静默取消，
    ///    用户端只表现为"点了没反应、版本没变"（2026-10-01 踩过）。
    /// </summary>
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is not { Path: { Length: > 0 } path }) return;
        await Services.Updating.UpdateFlow.InstallLocalAsync(path);
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        // 解压过就去产物目录 —— 用户想看的显然是解出来的那一堆，不是那个压缩包
        if (Tagged(sender) is { PreferredRevealPath: { Length: > 0 } path })
            App.MainWindow?.RevealFile(path);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => DownloadService.OpenDownloadsFolder();

    private void ClearFinished_Click(object sender, RoutedEventArgs e) => DownloadManager.Current.ClearFinished();

    /// <summary>
    /// 「安装」/「解压」—— 同一个按钮，行为由任务自己的文件类型决定（见 <see cref="PackageKind"/>）。
    ///
    /// ⚠️ 这里的「安装」跟更新包那颗「安装」不是一回事：更新包必须先退应用（走 UpdateFlow），
    ///    这儿是启动用户自己下载的第三方安装程序，应用不用退。
    /// </summary>
    private void PackageAction_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is not { } task) return;
        _ = PackageRunner.RunAsync(task);   // 两个开关由它自己去存档里读
    }

    /// <summary>解压完成后的「打开解压后的文件夹」。</summary>
    private void OpenExtracted_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is { ExtractedDir: { Length: > 0 } dir })
            App.MainWindow?.RevealFile(dir);
    }

    /// <summary>「完成后自动删除」（默认关闭）—— 改完立刻落盘，下次进来还是这个值。</summary>
    private void AutoDelete_Changed(object sender, RoutedEventArgs e)
        => SaveSwitch(AutoDeleteBox, v => App.Settings.Current.AutoDeletePackage = v,
            App.Settings.Current.AutoDeletePackage, "安装或解压完成后自动删除安装包或压缩包");

    /// <summary>「解压后自动打开文件夹」（默认开启）。</summary>
    private void AutoOpen_Changed(object sender, RoutedEventArgs e)
        => SaveSwitch(AutoOpenBox, v => App.Settings.Current.AutoOpenExtractFolder = v,
            App.Settings.Current.AutoOpenExtractFolder, "解压完成后自动打开所在文件夹");

    /// <summary>两个开关共用的落盘逻辑（都在初始化时会被触发一次，用 _loadingSettings 挡住）。</summary>
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
