using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>列表里的一行（把 CheckItem 翻译成界面要的样子）。</summary>
public sealed class CheckRow
{
    public CheckRow(CheckItem item)
    {
        Item = item;
        Title = item.App.Name;
        StatusText = item.Status switch
        {
            CheckStatus.Installed => "已装",
            CheckStatus.Newer => "已装（更新）",
            CheckStatus.Outdated => "建议升级",
            CheckStatus.Unknown => "已装（版本未知）",
            _ => "未安装",
        };
        StatusBrush = BrushFor(item.Status);

        // 卡片窄（302 宽），版本文案按状态精简：能一眼看出"要不要动手"就够了，
        // 完整信息在详情页。升级时给出 本机 → 清单 的方向，比罗列两个版本号直观。
        VersionText = item.Status switch
        {
            CheckStatus.Missing => item.App.Version.Length > 0 ? $"清单 {item.App.Version}" : "",
            CheckStatus.Outdated => $"本机 {item.InstalledVersion} → 清单 {item.App.Version}",
            _ => item.InstalledVersion.Length > 0
                ? $"本机 {item.InstalledVersion}"
                : (item.App.Version.Length > 0 ? $"清单 {item.App.Version}" : ""),
        };

        Note = item.Note;

        // 图标：有图就显示图，没图（或占位没解析出来）露一个字形，别留一块空白
        var img = item.App.IconImage;
        IconImage = img;
        PlaceholderVisibility = img is null ? Visibility.Visible : Visibility.Collapsed;
    }

    public CheckItem Item { get; }
    public string Title { get; }
    public string StatusText { get; }
    public Brush StatusBrush { get; }
    public string VersionText { get; }
    public string Note { get; }
    public ImageSource? IconImage { get; }
    public Visibility PlaceholderVisibility { get; }

    /// <summary>状态色。用主题资源，深色/浅色自动跟着走；取不到就退回次要文字色（不会渲染成黑块）。</summary>
    private static Brush BrushFor(CheckStatus status)
    {
        var key = status switch
        {
            CheckStatus.Outdated => "SystemFillColorCautionBrush",
            CheckStatus.Installed or CheckStatus.Newer => "SystemFillColorSuccessBrush",
            CheckStatus.Unknown => "TextFillColorSecondaryBrush",
            _ => "TextFillColorTertiaryBrush",
        };
        return Lookup(key) ?? Lookup("TextFillColorSecondaryBrush")
            ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);   // 都没取到也要可见，不能是透明
    }

    private static Brush? Lookup(string key)
    {
        try
        {
            return Application.Current.Resources.TryGetValue(key, out var v) ? v as Brush : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// 本机核实页：拿内容包的软件清单核对这台电脑装了什么。
///
/// 为什么要后台线程：枚举要遍历三个注册表视图的几百个键，某些机器（尤其开了安全软件的）
/// 读注册表会明显发涩。这活儿不该压在 UI 线程上，所以整体 Task.Run，先把"检测中"摆出来。
/// </summary>
public sealed partial class MachineCheckPage : Page
{
    private const string All = "全部";
    private const string Outdated = "建议升级";
    private const string InstalledOnly = "已装的";
    private const string MissingOnly = "未安装的";

    private List<CheckRow> _all = new();
    private bool _ready;

    public MachineCheckPage()
    {
        InitializeComponent();
        BuildFilters();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Load();
    }

    private void BuildFilters()
    {
        FilterBox.ItemsSource = new[] { All, Outdated, InstalledOnly, MissingOnly };
        FilterBox.SelectedIndex = 0;
    }

    private async void Load()
    {
        LoadingPanel.Visibility = Visibility.Visible;
        ResultGrid.ItemsSource = null;
        EmptyPanel.Visibility = Visibility.Collapsed;
        RescanButton.IsEnabled = false;

        var apps = App.Content.Apps.ToList();

        if (apps.Count == 0)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            RescanButton.IsEnabled = true;
            EmptyPanel.Visibility = Visibility.Visible;
            EmptyText.Text = "软件清单尚未加载，请先前往「软件下载」获取内容包。";
            SummaryHint.Text = "";
            return;
        }

        // 注册表枚举 + 比对一起丢后台（都是纯计算/只读，不碰 UI）
        var results = await Task.Run(() =>
        {
            var installed = InstalledApps.Enumerate();
            return (Items: MachineCheck.Run(apps, installed), InstalledCount: installed.Count);
        });

        _all = results.Items.Select(i => new CheckRow(i)).ToList();

        var s = MachineCheck.Summarize(results.Items);
        StatTotal.Text = s.Total.ToString();
        StatInstalled.Text = s.Installed.ToString();
        StatOutdated.Text = s.Outdated.ToString();
        StatMissing.Text = s.Missing.ToString();

        SummaryHint.Text = s.Outdated > 0
            ? $"{s.Outdated} 个软件建议升级 · 本机共检测到 {results.InstalledCount} 个已安装软件"
            : $"未发现需要升级的软件 · 本机共检测到 {results.InstalledCount} 个已安装软件";

        FootNote.Text =
            "检测口径：读取 Windows「程序和功能」的安装记录（64 位、32 位、当前用户三处），按软件名称与清单匹配。" +
            "Microsoft Store 应用、免安装版以及改过名的软件不会出现在该记录中，可能被判定为「未安装」；" +
            "版本差异仅在本机记录包含有效版本号时给出。点击卡片可查看软件详情。";

        LoadingPanel.Visibility = Visibility.Collapsed;
        RescanButton.IsEnabled = true;
        _ready = true;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (!_ready) return;   // 构造期 SelectedIndex 赋值也会触发一次，那时还没数据
        var key = FilterBox.SelectedItem as string ?? All;

        var shown = key switch
        {
            Outdated => _all.Where(r => r.Item.Status == CheckStatus.Outdated).ToList(),
            InstalledOnly => _all.Where(r => r.Item.Status is CheckStatus.Installed or CheckStatus.Newer
                                             or CheckStatus.Unknown).ToList(),
            MissingOnly => _all.Where(r => r.Item.Status == CheckStatus.Missing).ToList(),
            _ => _all,
        };

        ResultGrid.ItemsSource = shown;
        CountText.Text = $"显示 {shown.Count} / {_all.Count} 条";
        EmptyPanel.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = $"「{key}」中没有符合条件的软件。";
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void Rescan_Click(object sender, RoutedEventArgs e) => Load();

    /// <summary>整张卡片可点，进详情页（和「软件下载」的卡片行为一致，省掉卡上一个按钮的位置）。</summary>
    private void Result_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not CheckRow row) return;
        if (row.Item.App.Id.Length == 0) return;
        Frame.Navigate(typeof(DetailPage), row.Item.App.Id);
    }

    private void GoApps_Click(object sender, RoutedEventArgs e)
        => App.MainWindow?.Shell.NavigateTo("apps");

    /// <summary>整卡可点，给个悬停反馈（和「软件下载」的卡片同一手法：压一点不透明度）。</summary>
    private void Card_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Opacity = 0.88;
    }

    private void Card_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Opacity = 1.0;
    }
}
