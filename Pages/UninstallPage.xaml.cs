using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>列表里的一行（把 <see cref="InstalledApp"/> 翻译成界面要的样子）。</summary>
public sealed class UninstallRow
{
    public UninstallRow(InstalledApp app)
    {
        App = app;
        Name = app.Name;
        Version = app.Version;

        // 副标题：发布者 + 来源。都取不到就退回安装位置，再取不到就留空
        //（⛔ 别写"未知"之类的占位符，看着像没加载出来）
        var bits = new List<string>();
        if (app.Publisher.Length > 0) bits.Add(app.Publisher);
        if (app.IsStore) bits.Add("商店应用");
        if (bits.Count == 0 && app.Location.Length > 0) bits.Add(app.Location);
        Sub = string.Join(" · ", bits);

        CanUninstall = AppUninstaller.CanUninstall(app);
        ActionTip = AppUninstaller.IsSelf(app) ? "这是本程序自己，不能在这里卸载"
            : CanUninstall ? $"卸载「{app.Name}」"
            : "这条记录没有提供卸载命令，需要手动处理";
    }

    public InstalledApp App { get; }
    public string Name { get; }
    public string Version { get; }
    public string Sub { get; }
    public bool CanUninstall { get; }
    public string ActionTip { get; }
}

/// <summary>
/// 应用卸载页（2026-10-12 加，挂在「实验性功能」下）。
///
/// 主题是「以软件下载起步的程序，也管卸载」—— 把本机装了什么列出来，能直接卸。
/// 扫描走 <see cref="InstalledApps"/>（跟「本机核实」同一套数据源），
/// 卸载走 <see cref="AppUninstaller"/>（调厂商自己的卸载程序，我们不删文件）。
///
/// ⚠️ 扫描一定要丢后台：要遍历三个注册表视图的几百个键，某些机器（开着安全软件的）
///    读注册表明显发涩，压 UI 线程会卡成一片白。
/// </summary>
public sealed partial class UninstallPage : Page
{
    private List<UninstallRow> _all = new();
    private bool _ready;    // 数据到位了没（构造期的控件事件要忽略）
    private bool _loading;  // 扫描中（防重入）
    private bool _busy;     // 正在跑一次卸载（防重入）

    public UninstallPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;

        LoadingPanel.Visibility = Visibility.Visible;
        AppList.ItemsSource = null;
        EmptyPanel.Visibility = Visibility.Collapsed;
        RescanButton.IsEnabled = false;
        CountText.Text = "正在扫描…";

        try
        {
            var apps = await Task.Run(() =>
            {
                var list = new List<InstalledApp>();
                list.AddRange(InstalledApps.Enumerate());          // 「程序和功能」
                list.AddRange(InstalledApps.EnumerateStorePackages()); // 商店应用
                return list.OrderBy(a => a.Name, StringComparer.CurrentCulture).ToList();
            });

            _all = apps.Select(a => new UninstallRow(a)).ToList();
            _ready = true;
            ApplyFilter();
        }
        catch (Exception ex)
        {
            AppLog.Exception("uninstall", ex, "扫描已安装软件");
            _all.Clear();
            _ready = true;
            AppList.ItemsSource = null;
            EmptyPanel.Visibility = Visibility.Visible;
            EmptyText.Text = "扫描失败，请点「重新扫描」再试一次。";
            CountText.Text = "";
        }
        finally
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            RescanButton.IsEnabled = true;
            _loading = false;
        }
    }

    private void ApplyFilter()
    {
        if (!_ready) return;   // 构造期 TextChanged 也会进来一次，那时还没数据

        var key = SearchBox.Text?.Trim() ?? "";
        var shown = key.Length == 0
            ? _all
            : _all.Where(r =>
                    r.Name.Contains(key, StringComparison.CurrentCultureIgnoreCase) ||
                    r.Sub.Contains(key, StringComparison.CurrentCultureIgnoreCase))
                  .ToList();

        AppList.ItemsSource = shown;

        CountText.Text = _all.Count == 0
            ? "没有扫描到已安装的软件"
            : key.Length == 0
                ? $"共 {_all.Count} 个应用"
                : $"共 {_all.Count} 个应用，筛选出 {shown.Count} 个";

        EmptyPanel.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _all.Count == 0
            ? "没有扫描到已安装的软件。"
            : $"没有名称或发布者包含「{key}」的应用。";
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Rescan_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not UninstallRow row) return;
        if (!row.CanUninstall) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"卸载「{row.Name}」？",
            Content = BuildConfirmBody(row),
            PrimaryButtonText = "卸载",
            CloseButtonText = "取消",
            // ⚠️ 默认落在「取消」上：这是不可逆操作，别让一个回车就卸掉东西
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _busy = true;
        RescanButton.IsEnabled = false;
        SearchBox.IsEnabled = false;
        CountText.Text = $"正在卸载「{row.Name}」…";

        try
        {
            var outcome = await AppUninstaller.RunAsync(row.App);

            // 卸载程序退出了不等于登记表就更新了（有些要等一两拍）→ 稍等再重扫
            await Task.Delay(1200);
            await LoadAsync();

            // ⚠️ 提示必须放在 LoadAsync **之后** —— 它会把 CountText 重写成统计值
            if (!outcome.Started)
            {
                CountText.Text = outcome.Message;
            }
            else
            {
                var stillThere = _all.Any(r =>
                    string.Equals(r.Name, row.Name, StringComparison.CurrentCultureIgnoreCase));
                CountText.Text = stillThere
                    ? $"「{row.Name}」仍在列表里：卸载可能被取消、失败，或需要重启后才完成。"
                    : $"「{row.Name}」已从列表消失 —— 卸载完成。";
            }
        }
        finally
        {
            _busy = false;
            RescanButton.IsEnabled = true;
            SearchBox.IsEnabled = true;
        }
    }

    /// <summary>确认框正文：把"要动的是哪个软件"摆清楚，再说清接下来会发生什么。</summary>
    private static UIElement BuildConfirmBody(UninstallRow row)
    {
        var panel = new StackPanel { Spacing = 8 };

        void Line(string text, double opacity)
            => panel.Children.Add(new TextBlock
            {
                Text = text, FontSize = 12, Opacity = opacity, TextWrapping = TextWrapping.Wrap,
            });

        panel.Children.Add(new TextBlock
        {
            Text = row.Name, FontSize = 15, TextWrapping = TextWrapping.Wrap,
        });

        if (row.Version.Length > 0) Line($"版本 {row.Version}", 0.75);
        if (row.Sub.Length > 0) Line(row.Sub, 0.6);

        Line(row.App.IsStore
            ? "将按 Microsoft Store 应用的方式移除（等同于在系统「设置 → 应用」里点卸载）。"
            : "将调起该软件自带的卸载程序。期间可能弹出它自己的界面，以及系统的管理员授权提示。", 0.75);
        Line("卸载不可撤销，请先退出该软件并保存好正在编辑的内容。", 0.75);

        return panel;
    }
}
