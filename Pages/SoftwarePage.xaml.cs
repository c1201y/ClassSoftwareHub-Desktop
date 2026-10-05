using System;
using System.Collections.Generic;
using System.Linq;
using ClassSoftwareHub.Desktop.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>软件下载页：搜索 + 分类筛选 + 软件卡片墙（全原生，不加载任何网页）。</summary>
public sealed partial class SoftwarePage : Page
{
    private string _category = "";
    private string _keyword = "";
    private string _view = "tile";          // tile | grid
    private bool _loading;
    private readonly List<ToggleButton> _chips = new();

    /// <summary>
    /// 上一次真正重建列表时的「状态指纹」（分类|关键词|视图|内容包版本|内容来源）。
    ///
    /// 为什么要有它：本页 XAML 是 NavigationCacheMode="Enabled"，实例会被 Frame 留下来，
    /// 但 <see cref="Apply"/> 每次都把 ItemsSource 换成新的 List —— GridView 见到新集合
    /// 会把**所有卡片容器整个重建一遍**（含图标），那 200ms 左右的停顿又回来了，缓存等于白做。
    /// 指纹没变就什么都不做，"退出去再进来"就变成零成本。
    /// </summary>
    private string _appliedKey = "";

    public SoftwarePage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _category = e.Parameter as string ?? "";

        _view = App.Settings.Current.AppCardView == "grid" ? "grid" : "tile";
        _loading = true;
        ViewChoice.SelectedIndex = _view == "grid" ? 1 : 0;
        _loading = false;
        UpdateView();

        // 内容同步好之后自己要刷新 —— 安装包不自带清单，首启就停在"正在获取"这一屏上，
        // 用户多半不会为了看到列表专门切一次页（2026-10-05）。
        // ⚠️ 本页 NavigationCacheMode=Enabled（实例长驻），先 -= 再 += 保证只挂一次。
        App.Content.Changed -= OnContentChanged;
        App.Content.Changed += OnContentChanged;

        if (Fingerprint() == _appliedKey) return;   // 状态没变 → 保留现有卡片，别重建

        BuildChips();
        Apply();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        App.Content.Changed -= OnContentChanged;
        base.OnNavigatedFrom(e);
    }

    /// <summary>内容变了（同步完成 / 同步状态变化）：重建列表，或只刷一下空清单那几句提示。</summary>
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

        // 指纹没变 = 内容本身没换，只是"正在同步"的开关翻了页 → 不必重建卡片，
        // 否则每次同步开始/结束都要付一次列表重建的代价。
        if (App.Content.Apps.Count == 0) UpdateEmptyState(0);
    }

    /// <summary>
    /// 当前"页面状态"的指纹；任一要素变了才值得重建列表与分类条。
    ///
    /// ⚠️ 内容那一位必须用 <see cref="Core.ContentStore.Revision"/>（每次 Load 都涨），
    /// **不能**用 ContentVersion / Source（一个恒为空、一个恒为同一路径，索引不到"清单被补全了"，
    /// 首启同步完页面就不会自己刷新 —— 2026-10-05 实测踩到）。
    /// </summary>
    private string Fingerprint()
        => string.Join("\u0001", _category, _keyword, _view,
                       App.Content.Revision.ToString(), App.Content.Source);

    /// <summary>磁贴（3 列，带简介）/ 网格（5 列，紧凑）切换，选择会记进设置。</summary>
    private void ViewChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ViewChoice.SelectedItem is not RadioButton rb || rb.Tag is not string tag) return;

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
        Chips.Children.Clear();
        _chips.Clear();

        AddChip("全部", "");
        foreach (var c in App.Content.Categories)
            AddChip(c.Name, c.Key);
    }

    private void AddChip(string text, string key)
    {
        var chip = new ToggleButton
        {
            Content = key.Length == 0 ? $"{text} {App.Content.Apps.Count}" : $"{text} {App.Content.CountInCategory(key)}",
            Tag = key,
            IsChecked = key == _category,
        };
        chip.Click += (s, _) =>
        {
            _category = (string)((ToggleButton)s).Tag;
            foreach (var other in _chips)
                if (!ReferenceEquals(other, s)) other.IsChecked = false;
            ((ToggleButton)s).IsChecked = true;
            Apply();
        };
        _chips.Add(chip);
        Chips.Children.Add(chip);
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _keyword = sender.Text ?? "";
        Apply();
    }

    private void Apply()
    {
        var list = App.Content.Apps
            .Where(a => _category.Length == 0 || a.Category == _category)
            .Where(a => a.Matches(_keyword))
            .ToList();

        TileGrid.ItemsSource = list;
        CompactGrid.ItemsSource = list;

        TitleText.Text = _category.Length == 0
            ? (_keyword.Length == 0 ? "软件下载" : $"搜索：{_keyword}")
            : App.Content.CategoryName(_category);

        UpdateEmptyState(list.Count);

        _appliedKey = Fingerprint();   // 记下这次是按什么状态建的，下次同状态直接跳过
    }

    /// <summary>
    /// 空清单时别只写一句"没有匹配的软件"——要分清三种情况：
    /// ① 搜索/分类筛掉了 ② 正常：安装包不自带清单，正在联网取 ③ 异常：真的没取到
    /// </summary>
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

        // 正在联网取 —— 这是最正常的首启状态（安装包不再内置清单），别吓唬用户
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

    /// <summary>空状态里的「重新获取清单」：拉一次网络内容再重读（失败就照实说）。</summary>
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

    // 卡片自己的悬停反馈（容器 chrome 已关掉，见 XAML 里的 CshCardItemStyle）
    private void Card_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Opacity = 0.88;
    }

    private void Card_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Opacity = 1.0;
    }

    // 占位字形（购物袋）的显隐由 SoftwareApp.IconPlaceholder 驱动（见 Data/SoftwareApp.cs）：
    // 没图 / 加载中 / 加载失败都显示，加载成功才收掉。用属性 + INotifyPropertyChanged 而不是
    // Image 元素事件，是因为 GridView 回收容器时属性会重新求值，不会被上一次的状态卡住。
}
