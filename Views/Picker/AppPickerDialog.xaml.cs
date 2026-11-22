using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassSoftwareHub.Desktop.Views.Picker;

/// <summary>应用选择框里的一格。图标后补，所以要实现属性通知。</summary>
public sealed class ScannedAppItem : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private bool _iconLoaded;

    public ScannedAppItem(string name, string path)
    {
        Name = name;
        Path = path;
    }

    public string Name { get; }

    public string Path { get; }

    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    /// <summary>取一次就记住，重复出现在可视区域时不重复读图标。</summary>
    public void EnsureIcon()
    {
        if (_iconLoaded) return;
        _iconLoaded = true;

        Icon = AppIconService.Get(Path);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 从扫描结果里挑选应用。控件、间距、字号全部用系统默认，只调整对话框尺寸（2026-11-22）。
/// </summary>
public sealed partial class AppPickerDialog : ContentDialog
{
    private readonly List<ScannedAppItem> _all;

    public AppPickerDialog(IReadOnlyList<ScannedApp> apps)
    {
        InitializeComponent();

        _all = apps.Select(a => new ScannedAppItem(a.Name, a.Path)).ToList();
        AppGrid.ItemsSource = _all;

        HintText.Text = _all.Count == 0
            ? "没有扫描到应用。可以用「浏览其他文件」直接挑选程序。"
            : $"已扫描到 {_all.Count} 个应用，点选即可多选。";

        // 图标靠 shell 取，先把像素在后台备好，格子滚到眼前时只差一次贴图。
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            foreach (var item in _all) AppIconService.Prewarm(item.Path);
        });
    }

    /// <summary>用户选中的应用。</summary>
    public IReadOnlyList<ScannedApp> Selected =>
        AppGrid.SelectedItems
            .OfType<ScannedAppItem>()
            .Select(i => new ScannedApp(i.Name, i.Path))
            .ToList();

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

        var query = (sender.Text ?? "").Trim();
        var matched = query.Length == 0
            ? _all
            : _all.Where(i => i.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();

        AppGrid.SelectedItems.Clear();
        AppGrid.ItemsSource = matched;

        HintText.Text = _all.Count == 0
            ? "没有扫描到应用。可以用「浏览其他文件」直接挑选程序。"
            : query.Length == 0
                ? $"已扫描到 {_all.Count} 个应用，点选即可多选。"
                : $"匹配 {matched.Count} 个。";

        UpdatePrimary();
    }

    private void AppGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePrimary();

    private void UpdatePrimary()
    {
        var count = AppGrid.SelectedItems.Count;
        PrimaryButtonText = count > 0 ? $"添加 {count} 个" : "添加";
        IsPrimaryButtonEnabled = count > 0;
    }

    private void AppGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        if (args.Item is not ScannedAppItem item) return;

        item.EnsureIcon();
    }
}
