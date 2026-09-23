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

        BuildChips();
        Apply();
    }

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
        EmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
