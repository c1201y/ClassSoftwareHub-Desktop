using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ClassSoftwareHub.Desktop.Pages;





public sealed partial class StartupRow : INotifyPropertyChanged
{
    private bool _enabled;
    private bool _busy;

    private StartupRow(StartupItem item)
    {
        Item = item;
        _enabled = item.Enabled;
    }

    public static StartupRow From(StartupItem item) => new(item);

    public StartupItem Item { get; }

    public string Name => Item.Name;

    
    public const string RowGlyph = "\uE945";

    public string Glyph => RowGlyph;

    public string SourceLabel => Item.SourceLabel;

    public string Sub => Item.Missing ? Item.Command + "　（找不到对应文件）" : Item.Command;

    public string SubTooltip => Item.Missing
        ? Item.Command + "\n\n命令行指向的文件已经不存在，多半是软件卸载后留下的死项。"
        : Item.Command;

    
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            Raise(nameof(Enabled));
            Raise(nameof(StateVisibility));
        }
    }

    
    public bool Busy
    {
        get => _busy;
        set
        {
            if (_busy == value) return;
            _busy = value;
            Raise(nameof(CanToggle));
        }
    }

    public bool CanToggle => !_busy;

    
    public Visibility StateVisibility => _enabled ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}





public sealed partial class StartupPage : Page
{
    private List<StartupRow> _rows = new();
    private CancellationTokenSource? _cts;
    private bool _ready;
    private bool _busy;          
    private bool _dialogOpen;    

    public StartupPage()
    {
        InitializeComponent();

        
        WheelRelay.Attach(this, EntryList);

        
        Loaded += (_, _) =>
        {
            if (!_ready && _cts is null) _ = ScanAsync();
        };
    }

    
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        CancelScan();
    }

    private void CancelScan()
    {
        var cts = _cts;
        _cts = null;
        if (cts is null) return;

        try
        {
            cts.Cancel();
        }
        catch (Exception ex)
        {
            AppLog.Exception("startup", ex, "取消扫描开机启动项");
        }
    }

    

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (_busy) return;

        CancelScan();
        var cts = new CancellationTokenSource();
        _cts = cts;

        _busy = true;
        _ready = false;

        LoadingPanel.Visibility = Visibility.Visible;
        EmptyPanel.Visibility = Visibility.Collapsed;
        EntryList.ItemsSource = null;
        CountLine.Text = "正在读取…";

        try
        {
            var items = await StartupItems.ScanAsync(cts.Token);
            if (cts.IsCancellationRequested) return;

            _rows = items.Select(StartupRow.From).ToList();
            EntryList.ItemsSource = _rows;

            _ready = true;
            RenderCount();

            EmptyPanel.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            
        }
        catch (Exception ex)
        {
            AppLog.Exception("startup", ex, "扫描开机启动项");

            _ready = true;
            CountLine.Text = "";
            EmptyText.Text = "读取开机启动项时出错。可以点右上角「重新扫描」再试一次。";
            EmptyPanel.Visibility = Visibility.Visible;
        }
        finally
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            _busy = false;
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
        }
    }

    private void RenderCount()
    {
        var total = _rows.Count;
        if (total == 0)
        {
            CountLine.Text = "";
            return;
        }

        var off = _rows.Count(r => !r.Enabled);
        var text = off == 0
            ? $"共 {total} 项，全部处于启用状态"
            : $"共 {total} 项，其中 {off} 项已禁用";

        var admin = _rows.Count(r => r.Item.NeedsAdmin);
        if (admin > 0) text += $"　·　{admin} 项为所有用户级，改动时需要管理员授权";

        CountLine.Text = text;
    }

    

    
    
    
    
    
    
    
    private async void Row_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.DataContext is not StartupRow row) return;
        if (row.Busy) return;
        if (toggle.IsOn == row.Enabled) return;

        var want = toggle.IsOn;
        row.Busy = true;

        try
        {
            var error = await StartupItems.SetEnabledAsync(row.Item, want, CancellationToken.None);

            if (error.Length == 0)
            {
                row.Enabled = want;
                RenderCount();
            }
            else
            {
                
                
                toggle.IsOn = !want;
                await ShowMessageAsync(want ? "启用未成功" : "禁用未成功", error);
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("startup", ex, "切换开机启动项");
            toggle.IsOn = !want;
            await ShowMessageAsync("操作未成功", ex.Message);
        }
        finally
        {
            row.Busy = false;
        }
    }

    

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
            XamlRoot = XamlRoot,
            Title = "开机启动项：这里扫的是什么",
            Content = BuildAboutBody(),
            CloseButtonText = "关闭",
        };

        await ShowDialogAsync(dialog);
    }

    private static UIElement BuildAboutBody()
    {
        var panel = new StackPanel { Spacing = 14 };

        void Item(string title, string body)
        {
            var block = new StackPanel { Spacing = 4 };
            block.Children.Add(new TextBlock { Text = title, FontSize = 13 });
            block.Children.Add(new TextBlock
            {
                Text = body, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(block);
        }

        Item("扫的是这五处",
             "注册表的 Run 键（当前用户 / 所有用户 / 所有用户的 32 位）与启动文件夹（当前用户 / 所有用户）。"
             + "一次性启动项（RunOnce）不在此列 —— 它执行过一次就自动消失，没有「开机自启」这层含义。");

        Item("关掉一项会发生什么",
             "只是关掉这一项的「开机自启」：程序还在、文件一个不动、注册表里的命令行也原样保留。"
             + "启用状态记录在 Windows 自己的启动状态位里 —— 与任务管理器的「启动」页是同一份记录，"
             + "所以在任何一边改动，另外两边都会跟着变。");

        Item("「所有用户」级的项",
             "这类项的状态位需要管理员权限才能改写，按下去时会弹一次授权确认。"
             + "取消授权不会改动任何东西。");

        Item("「找不到对应文件」",
             "命令行指向的文件已经不存在时会标出来。这类多半是软件卸载之后留下的死项，"
             + "Windows 启动时本来就会跳过它们，可以放心关掉。");

        return panel;
    }

    

    private async Task ShowDialogAsync(ContentDialog dialog)
    {
        if (_dialogOpen) return;

        _dialogOpen = true;
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            AppLog.Info("startup", $"弹窗没弹出来：{ex.GetType().Name}");
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = message, FontSize = 13, TextWrapping = TextWrapping.Wrap,
            },
            CloseButtonText = "关闭",
        };

        return ShowDialogAsync(dialog);
    }
}
