using System;
using System.Collections.Generic;
using System.Linq;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace ClassSoftwareHub.Desktop.Pages;


public sealed partial class SoftwarePage : Page
{
    private string _category = "";

    
    
    
    
    private string _platform = "";

    
    
    
    
    
    private string _keyword = "";
    private string _view = "tile";          
    private bool _loading;

    
    
    
    
    
    
    
    
    private string _appliedKey = "";

    
    
    
    
    
    private List<SoftwareApp> _shown = new();

    public SoftwarePage()
    {
        InitializeComponent();

        
        TileGrid.Loaded += (_, _) => FitCards();
        TileGrid.SizeChanged += (_, _) => FitCards();
        CompactGrid.Loaded += (_, _) => FitCards();
        CompactGrid.SizeChanged += (_, _) => FitCards();

        
        Core.WheelRelay.Attach(this, TileGrid, CompactGrid);

        
        AiFlyout.ContextProvider = BuildAiContext;
    }

    
    private const double CardGap = 8;

    private const double TileMinWidth = 300, TileHeight = 126;
    private const double CompactMinWidth = 184, CompactHeight = 114;

    
    
    
    
    
    
    
    
    private void FitCards()
    {
        FitOne(TileGrid, TileMinWidth, TileHeight);
        FitOne(CompactGrid, CompactMinWidth, CompactHeight);
    }

    private static void FitOne(GridView grid, double minWidth, double height)
    {
        if (grid.ItemsPanelRoot is not ItemsWrapGrid panel) return;

        
        
        if (panel.Tag is null)
        {
            panel.Tag = "fit-watch";
            panel.SizeChanged += (_, _) => FitOne(grid, minWidth, height);
        }

        
        
        
        
        
        var available = panel.ActualWidth;
        if (available < 1)
        {
            
            
            available = grid.ActualWidth - grid.Padding.Left - grid.Padding.Right;
            if (grid.Tag is null)
            {
                grid.Tag = "fit-retry";
                grid.DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    grid.Tag = null;
                    FitOne(grid, minWidth, height);
                });
            }
        }
        if (available <= minWidth) return;   

        
        var container = grid.ActualWidth - grid.Padding.Left - grid.Padding.Right;
        if (container > 1 && container < available) available = container;

        var columns = Math.Max(1, (int)Math.Floor((available + CardGap) / (minWidth + CardGap)));

        
        
        
        
        
        var slot = Math.Floor(available / columns) - 0.5;

        
        
        if (slot - CardGap < minWidth) slot = minWidth + CardGap;

        
        
        if (Math.Abs(panel.ItemWidth - slot) < 1) return;

        panel.ItemWidth = slot;
        panel.ItemHeight = height + CardGap;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        
        
        
        
        if (e.Parameter is string requestedCategory && requestedCategory.Length > 0)
            _category = requestedCategory;

        
        
        
        HubAiEntry.Visibility = App.Settings.Current.HubAiEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;

        _view = App.Settings.Current.AppCardView == "grid" ? "grid" : "tile";
        _loading = true;
        ViewPicker.SelectedIndex = _view == "grid" ? 1 : 0;
        _loading = false;
        UpdateView();

        
        
        
        App.Content.Changed -= OnContentChanged;
        App.Content.Changed += OnContentChanged;

        if (Fingerprint() == _appliedKey) return;   

        BuildChips();
        Apply();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        App.Content.Changed -= OnContentChanged;
        base.OnNavigatedFrom(e);
    }

    
    private void OnContentChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnContentChanged);
            return;
        }

        if (Fingerprint() != _appliedKey)
        {
            BuildChips();
            Apply();
            return;
        }

        
        
        if (App.Content.Apps.Count == 0) UpdateEmptyState(0);
    }

    
    
    
    
    
    
    
    private string Fingerprint()
        => string.Join("\u0001", _category, _platform, _keyword, _view,
                       App.Content.Revision.ToString(), App.Content.Source);

    
    private void ViewPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ViewPicker.SelectedItem is not ComboBoxItem { Tag: string tag }) return;

        _view = tag == "grid" ? "grid" : "tile";
        App.Settings.Current.AppCardView = _view;
        App.Settings.Save();
        UpdateView();
    }

    private void UpdateView()
    {
        var tile = _view != "grid";
        TileGrid.Visibility = tile ? Visibility.Visible : Visibility.Collapsed;
        CompactGrid.Visibility = tile ? Visibility.Collapsed : Visibility.Visible;
    }

    
    
    
    
    
    private void BuildChips()
    {
        CategoryBar.Items.Clear();

        CategoryBar.Items.Add(new SelectorBarItem
        {
            Text = $"全部 {App.Content.Apps.Count}",
            Tag = "",
        });

        foreach (var category in App.Content.Categories)
        {
            CategoryBar.Items.Add(new SelectorBarItem
            {
                Text = $"{category.Name} {App.Content.CountInCategory(category.Key)}",
                Tag = category.Key,
            });
        }

        
        _loading = true;
        try
        {
            CategoryBar.SelectedItem = CategoryBar.Items
                .FirstOrDefault(i => (string?)i.Tag == _category);

            if (CategoryBar.SelectedItem is null && CategoryBar.Items.Count > 0)
                CategoryBar.SelectedItem = CategoryBar.Items[0];
        }
        finally
        {
            _loading = false;
        }

        
        BuildPlatformPicker();
    }

    
    
    
    
    
    
    private void BuildPlatformPicker()
    {
        var platforms = App.Content.Platforms;

        PlatformPicker.Visibility = platforms.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        _loading = true;
        try
        {
            PlatformPicker.Items.Clear();
            PlatformPicker.Items.Add("全部系统");

            var index = 0;
            for (var i = 0; i < platforms.Count; i++)
            {
                PlatformPicker.Items.Add(platforms[i].Name);
                if (platforms[i].Key == _platform) index = i + 1;
            }

            
            if (_platform.Length > 0 && index == 0) _platform = "";

            PlatformPicker.SelectedIndex = index;
        }
        finally
        {
            _loading = false;
        }
    }

    private void PlatformPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        var index = PlatformPicker.SelectedIndex;
        _platform = index <= 0 || index > App.Content.Platforms.Count
            ? ""
            : App.Content.Platforms[index - 1].Key;

        Apply();
    }

    private void CategoryBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_loading) return;
        if (sender.SelectedItem is not SelectorBarItem item) return;

        _category = item.Tag as string ?? "";
        Apply();
    }

    
    
    
    
    
    
    
    private PageContextDoc BuildAiContext()
    {
        var doc = new PageContextDoc(
            "软件下载",
            "这一页是站内软件库的下载墙，只能用库里有的软件回答推荐类问题。" +
            "用户问「有没有 XX」「哪个能装 Win7」时，先看下面的清单与分类，库里没有就直说没有。");

        var all = App.Content.Apps;

        doc.Add("筛选状态", new
        {
            分类 = _category.Length == 0 ? "全部" : App.Content.CategoryName(_category),
            分类key = _category,
            平台 = _platform.Length == 0 ? "全部系统" : App.Content.PlatformName(_platform),
            平台key = _platform,
            关键词 = _keyword,
            排序 = "按软件库原始顺序",
            视图 = _view == "tile" ? "卡片" : "紧凑",
            当前显示 = _shown.Count,
            库内总数 = all.Count,
        });

        doc.Add("分类与数量", App.Content.Categories.Select(c => new
        {
            key = c.Key,
            名称 = c.Name,
            数量 = all.Count(a => a.Category == c.Key),
        }).ToList());

        doc.Add("可筛选的平台", App.Content.Platforms.Select(p => new { key = p.Key, 名称 = p.Name }).ToList());

        
        doc.Add("当前页上的软件（前 25 款）", _shown.Take(25).Select(a => new
        {
            a.Name,
            分类 = a.CategoryDisplay.Length > 0 ? a.CategoryDisplay : App.Content.CategoryName(a.Category),
            版本 = a.Version,
            体积 = a.Size,
            支持系统 = string.Join("/", a.Platforms),
            系统要求 = a.System,
            一句话 = a.Tagline,
            下载方式 = string.Join("/", a.Downloads.Select(d => d.ActionText).Distinct()),
        }).ToList());

        if (_shown.Count > 25)
            doc.Add("其余软件", new
            {
                条数 = _shown.Count - 25,
                名称 = _shown.Skip(25).Select(a => a.Name).ToList(),
            });

        doc.Action("按分类筛选", "点分类栏切换，AI 无法代点");
        doc.Action("按系统筛选", "平台下拉框，可筛 Windows/macOS/Linux 等");
        doc.Action("打开软件详情", "卡片点进去能看到下载条目、校验值与官网链接");

        if (_shown.Count == 0)
            doc.Say("当前筛选下没有任何结果；用户若在找某款软件，可以建议他清掉筛选条件再看");

        return doc;
    }

    private void Apply()
    {        var list = App.Content.Apps
            .Where(a => _category.Length == 0 || a.Category == _category)
            .Where(a => a.SupportsPlatform(_platform))
            .Where(a => a.Matches(_keyword))
            .ToList();

        TileGrid.ItemsSource = list;
        CompactGrid.ItemsSource = list;

        WarmPalette(list);

        
        _shown = list;

        var baseTitle = _category.Length == 0
            ? (_keyword.Length == 0 ? "软件下载" : $"搜索：{_keyword}")
            : App.Content.CategoryName(_category);

        
        TitleText.Text = _platform.Length == 0
            ? baseTitle
            : $"{baseTitle} · {App.Content.PlatformName(_platform)}";

        UpdateEmptyState(list.Count);

        _appliedKey = Fingerprint();   
    }

    
    
    
    
    
    
    
    private static void WarmPalette(List<SoftwareApp> list)
    {
        const int limit = 32;

        for (var i = 0; i < list.Count && i < limit; i++)
            Services.IconPalette.Preload(list[i].Icon);
    }

    
    
    
    
    private void UpdateEmptyState(int shown)
    {
        if (shown > 0)
        {
            EmptyPanel.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyPanel.Visibility = Visibility.Visible;

        EmptyDetail.Text = $"当前内容来源：{App.Content.SourceLabel}" +
                           (App.Content.Issues.Count > 0 ? $"\n读取问题：{App.Content.Issues[0].Message}" : "");

        var hasFilter = _keyword.Length > 0 || _category.Length > 0;
        if (hasFilter)
        {
            EmptyTitle.Text = "未找到匹配的软件";
            EmptyText.Text = "请更换关键词，或单击「全部」查看所有软件。";
            EmptyText.Visibility = Visibility.Visible;
            EmptyDetail.Text = $"当前内容来源：{App.Content.SourceLabel}（共 {App.Content.Apps.Count} 个软件）";
            EmptyRetry.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyRetry.Visibility = Visibility.Visible;
        EmptyRetry.IsEnabled = true;
        EmptyRetry.Content = "重新获取清单";

        
        if (App.Content.IsSyncing)
        {
            EmptyTitle.Text = "正在获取软件清单";
            EmptyText.Text = "正在从网络获取最新的软件清单，请稍候。";
            EmptyRetry.IsEnabled = false;
            EmptyRetry.Content = "正在获取";
            return;
        }

        EmptyTitle.Text = "软件清单为空";
        EmptyText.Text = "软件清单不随安装包提供，需要联网获取。" +
                         "若始终为空，多半是当前网络连不上（教学机、校园网常见），换个网络再点下面的按钮。";
    }

    
    private async void EmptyRetry_Click(object sender, RoutedEventArgs e)
    {
        EmptyRetry.IsEnabled = false;
        EmptyRetry.Content = "正在获取";
        EmptyTitle.Text = "正在获取软件清单";
        EmptyText.Text = "正在从网络获取最新清单。";
        EmptyDetail.Text = "";

        try
        {
            var result = await Services.ContentUpdater.SyncAsync(
                new Progress<string>(text => EmptyDetail.Text = text));

            if (result.Updated) App.Content.Load();

            BuildChips();
            Apply();

            if (App.Content.Apps.Count == 0)
            {
                EmptyTitle.Text = "仍未获取到清单";
                EmptyText.Text = "网络不可用，或当前网络访问获取源不通。可以换个网络再试；" +
                                 "如问题持续，请将本页截图提供给维护人员。";
                EmptyDetail.Text = result.Message;
            }
        }
        catch (Exception ex)
        {
            EmptyTitle.Text = "获取失败";
            EmptyText.Text = "网络不可用或暂时无法访问，请稍后重试。";
            EmptyDetail.Text = ex.Message;
        }
        finally
        {
            EmptyRetry.IsEnabled = true;
            EmptyRetry.Content = "重新获取清单";
        }
    }

    private void AppGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SoftwareApp app)
            Frame.Navigate(typeof(DetailPage), app.Id);
    }

    
    private void Card_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Opacity = 0.88;
    }

    private void Card_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Opacity = 1.0;
    }

    
    
    

    
    
    
    
    

    private void HubAiEntry_Click(object sender, RoutedEventArgs e) => AiFlyout.Toggle();

    
    
    
    
    
    private void SubmitEntry_Click(object sender, RoutedEventArgs e)
        => ClassSoftwareHub.Desktop.Views.Submit.SubmitWizardWindow.Show();

}
