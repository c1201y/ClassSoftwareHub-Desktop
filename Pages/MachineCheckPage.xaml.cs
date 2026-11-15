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


public sealed class CheckRow
{
    public CheckRow(CheckItem item)
    {
        Item = item;
        Title = item.App.Name;
        StatusText = item.Status switch
        {
            CheckStatus.Installed => "已安装",
            CheckStatus.Newer => "已安装（更新）",
            CheckStatus.Outdated => "建议升级",
            CheckStatus.Unknown => "已安装（版本未知）",
            _ => "未安装",
        };
        StatusBrush = BrushFor(item.Status);

        
        
        VersionText = item.Status switch
        {
            CheckStatus.Missing => item.App.Version.Length > 0 ? $"清单 {item.App.Version}" : "",
            CheckStatus.Outdated => $"本机 {item.InstalledVersion} → 清单 {item.App.Version}",
            _ => item.InstalledVersion.Length > 0
                ? $"本机 {item.InstalledVersion}"
                : (item.App.Version.Length > 0 ? $"清单 {item.App.Version}" : ""),
        };

        Note = item.Note;

        
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
            ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);   
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







public sealed partial class MachineCheckPage : Page
{
    private const string All = "全部";
    private const string Outdated = "建议升级";
    private const string InstalledOnly = "已安装";
    private const string MissingOnly = "未安装";

    private List<CheckRow> _all = new();
    private bool _ready;

    public MachineCheckPage()
    {
        InitializeComponent();
        BuildFilters();

        
        Core.WheelRelay.Attach(this, ResultGrid);
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
            EmptyText.Text = "软件清单尚未加载，请先到「软件下载」页联网获取清单。";
            SummaryHint.Text = "";
            return;
        }

        
        var results = await Task.Run(() =>
        {
            var installed = InstalledApps.Enumerate();
            
            var store = InstalledApps.EnumerateStorePackages();
            return (
                Items: MachineCheck.Run(apps, installed, store),
                InstalledCount: installed.Count,
                StoreCount: store.Count);
        });

        _all = results.Items.Select(i => new CheckRow(i)).ToList();

        var s = MachineCheck.Summarize(results.Items);
        StatTotal.Text = s.Total.ToString();
        StatInstalled.Text = s.Installed.ToString();
        StatOutdated.Text = s.Outdated.ToString();
        StatMissing.Text = s.Missing.ToString();

        
        var storeTail = results.StoreCount > 0 ? $"，另有 {results.StoreCount} 个 Microsoft Store 应用" : "";
        SummaryHint.Text = s.Outdated > 0
            ? $"{s.Outdated} 个软件建议升级 · 本机共检测到 {results.InstalledCount} 个已安装软件{storeTail}"
            : $"未发现需要升级的软件 · 本机共检测到 {results.InstalledCount} 个已安装软件{storeTail}";

        FootNote.Text =
            "检测口径：读取 Windows「程序和功能」的安装记录（64 位、32 位、当前用户三处），以及本账户注册的 Microsoft Store 应用，" +
            "按软件名称与清单匹配。免安装版、已更名的软件，以及其他账户下安装的商店应用不在上述两处记录中，" +
            "可能被判定为「未安装」；版本差异仅在本机记录包含有效版本号时给出。单击卡片可查看软件详情。";

        LoadingPanel.Visibility = Visibility.Collapsed;
        RescanButton.IsEnabled = true;
        _ready = true;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (!_ready) return;   
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

    
    private void Result_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not CheckRow row) return;
        if (row.Item.App.Id.Length == 0) return;
        Frame.Navigate(typeof(DetailPage), row.Item.App.Id);
    }

    private void GoApps_Click(object sender, RoutedEventArgs e)
        => App.MainWindow?.Shell.NavigateTo("apps");

    
    private void Card_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Opacity = 0.88;
    }

    private void Card_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Opacity = 1.0;
    }
}
