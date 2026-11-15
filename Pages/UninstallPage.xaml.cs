using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Controls;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace ClassSoftwareHub.Desktop.Pages;


public sealed class CategoryOption
{
    public CategoryOption(AppCategory category, string glyph, string label, int count)
    {
        Category = category;
        Glyph = glyph;
        Label = label;
        Count = count;
    }

    public AppCategory Category { get; }
    public string Glyph { get; }
    public string Label { get; }
    public int Count { get; }

    
    public string CountText => Count.ToString("N0");
}


public sealed class UninstallRow
{
    private ImageSource? _icon;
    private bool _iconResolved;

    public UninstallRow(InstalledApp app)
    {
        App = app;
        Name = app.Name;
        Version = app.Version;
        CategoryText = AppCategoryLook.LabelOf(app.Category);

        
        
        
        var bits = new List<string>();
        var publisher = FriendlyPublisher(app.Publisher);
        if (publisher.Length > 0) bits.Add(publisher);
        if (bits.Count == 0 && app.PackageFullName.Length > 0)
            bits.Add(app.PackageFullName.Split('_')[0]);
        if (bits.Count == 0 && app.Location.Length > 0) bits.Add(app.Location);
        Sub = string.Join(" · ", bits);

        CanUninstall = AppUninstaller.CanUninstall(app);
        ActionTip = AppUninstaller.IsSelf(app) ? "该条目为本程序自身，不提供卸载"
            : AppUninstaller.IsProtected(app) ? "该条目为 Windows 系统组件，不提供卸载"
            : CanUninstall ? $"卸载「{app.Name}」"
            : "该记录未提供卸载命令，需手动处理";
    }

    public InstalledApp App { get; }
    public string Name { get; }
    public string Version { get; }
    public string Sub { get; }
    public bool CanUninstall { get; }
    public string ActionTip { get; }
    public string CategoryText { get; }

    
    
    
    
    
    public ImageSource? Icon
    {
        get
        {
            if (!_iconResolved)
            {
                _iconResolved = true;
                _icon = AppIconService.Get(App.IconPath);
            }

            return _icon;
        }
    }

    public Visibility IconVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility PlaceholderVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;

    
    
    
    
    
    public Visibility ActionsVisibility => CanUninstall ? Visibility.Visible : Visibility.Collapsed;

    
    public string PlaceholderGlyph => AppCategoryLook.GlyphOf(App.Category);

    
    
    
    
    private static string FriendlyPublisher(string raw)
    {
        if (raw.Length == 0) return "";
        if (!raw.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return raw;

        var s = raw[3..];
        var comma = s.IndexOf(',');
        return (comma > 0 ? s[..comma] : s).Trim();
    }
}








public sealed class LeftoverRow : System.ComponentModel.INotifyPropertyChanged
{
    private bool _checked;

    public LeftoverRow(LeftoverItem item)
    {
        Item = item;
        _checked = item.Selected;

        var bits = new List<string> { item.KindText };
        if (item.SizeText.Length > 0) bits.Add(item.SizeText);

        Meta = string.Join(" · ", bits);
    }

    public LeftoverItem Item { get; }

    
    public string Title => Item.DisplayLeaf;

    
    public string Location => Item.DisplayParent;

    
    public string FullPath => Item.DisplayPath;

    
    public string Meta { get; }

    
    public string Reason => Item.Reason.Length > 0 ? Item.Reason : "无可用判定依据";

    
    
    
    
    
    
    public bool? IsChecked
    {
        get => _checked;
        set
        {
            var v = value == true;
            if (v == _checked) return;
            _checked = v;
            Item.Selected = v;
            Raise();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    
    public event EventHandler? Changed;

    public Visibility StrongVisibility => Visible(LeftoverEvidence.Strong);
    public Visibility MediumVisibility => Visible(LeftoverEvidence.Medium);
    public Visibility WeakVisibility => Visible(LeftoverEvidence.Weak);

    private Visibility Visible(LeftoverEvidence level)
        => Item.Evidence == level ? Visibility.Visible : Visibility.Collapsed;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise()
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsChecked)));
}



















public sealed partial class UninstallPage : Page
{
    private List<UninstallRow> _all = new();
    private AppCategory _category = AppCategory.Desktop;
    private bool _ready;         
    private bool _loading;       
    private bool _busy;          

    
    private List<LeftoverRow> _leftoverRows = new();

    
    private string _leftoverAppName = "";

    
    private List<UninstallRow> _shown = new();

    public UninstallPage()
    {
        InitializeComponent();

        
        
        Core.WheelRelay.Attach(this, AppList, LeftoverList);

        
        HubAiEntry.Visibility = App.Settings.Current.HubAiEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;

        
        AiFlyout.ContextProvider = BuildAiContext;
    }

    
    
    
    
    
    
    
    private PageContextDoc BuildAiContext()
    {
        var doc = new PageContextDoc(
            "应用卸载",
            "这一页列出本机已安装的软件，并提供卸载与残留清理。" +
            "「我装了什么」「这个能不能卸」「系统组件为什么没有卸载键」这类问题，直接按下表回答。");

        doc.Add("状态", new
        {
            正在扫描 = _loading,
            正在执行卸载或清理 = _busy,
            已安装总数 = _all.Count,
            当前分类 = AppCategoryLook.LabelOf(_category),
            当前分类key = _category.ToString(),
            当前显示 = _shown.Count,
            分类分布 = _all.GroupBy(r => r.App.Category)
                           .ToDictionary(g => AppCategoryLook.LabelOf(g.Key), g => g.Count()),
        });

        
        doc.Add("当前分类下的软件（前 30 个）", _shown.Take(30).Select(r => new
        {
            r.Name,
            版本 = r.App.Version,
            发布者 = r.App.Publisher,
            类别 = r.CategoryText,
            可卸载 = r.CanUninstall,
            商店应用 = r.App.IsStore,
            卸载方式 = r.App.IsStore ? "按包名走 PackageManager" :
                       r.App.QuietUninstallString.Length > 0 ? "静默卸载命令" :
                       r.App.UninstallString.Length > 0 ? "注册表卸载命令" : "无（需手动处理）",
        }).ToList());

        if (_shown.Count > 30)
            doc.Add("其余软件", new
            {
                条数 = _shown.Count - 30,
                名称 = _shown.Skip(30).Select(r => r.Name).ToList(),
            });

        doc.Action("卸载", "逐条确认后执行对应的卸载命令");
        doc.Action("清理残留", "卸载后扫一遍残留目录与注册表项，可勾选删除");
        doc.Action("重新扫描", "重读注册表与商店包，刷新清单");

        if (_leftoverRows.Count > 0)
        {
            doc.Add("残留清理中的软件", new
            {
                软件 = _leftoverAppName,
                残留条目数 = _leftoverRows.Count,
                条目 = _leftoverRows.Take(20).Select(r => new { 名称 = r.Title, 说明 = r.Meta }).ToList(),
            });
        }

        if (_all.Count == 0)
            doc.Say("还没扫描到已安装的软件（可能还在扫描，或读取注册表被安全软件拦了）");

        return doc;
    }

    
    
    
    
    private void HubAiEntry_Click(object sender, RoutedEventArgs e) => AiFlyout.Toggle();

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
        CountLine.Text = "正在扫描…";

        try
        {
            var apps = await Task.Run(() =>
            {
                var list = new List<InstalledApp>();
                list.AddRange(InstalledApps.Enumerate());              
                list.AddRange(InstalledApps.EnumerateStorePackages()); 
                return list.OrderBy(a => a.Name, StringComparer.CurrentCulture).ToList();
            });

            _all = apps.Select(a => new UninstallRow(a)).ToList();
            _ready = true;

            FillCategories();   
            ApplyFilter();

            
            
            
            _ = Task.Run(() =>
            {
                foreach (var a in apps) AppIconService.Prewarm(a.IconPath);
            });
        }
        catch (Exception ex)
        {
            AppLog.Exception("uninstall", ex, "扫描已安装软件");
            _all.Clear();
            _ready = true;
            AppList.ItemsSource = null;
            EmptyPanel.Visibility = Visibility.Visible;
            EmptyText.Text = "扫描失败，请点击「重新扫描」后重试。";
            CountLine.Text = "";
        }
        finally
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            RescanButton.IsEnabled = true;
            _loading = false;
        }
    }

    
    
    
    
    
    private void FillCategories()
    {
        var wanted = _category;

        var options = AppCategoryLook.Order
            .Select(c => new CategoryOption(c, AppCategoryLook.GlyphOf(c), AppCategoryLook.LabelOf(c),
                                           _all.Count(r => r.App.Category == c)))
            .ToList();

        
        
        CategoryBox.Items.Clear();
        foreach (var option in options)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            panel.Children.Add(new FontIcon
            {
                Glyph = option.Glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
            });
            panel.Children.Add(new TextBlock
            {
                Text = option.Label, VerticalAlignment = VerticalAlignment.Center,
            });
            panel.Children.Add(new TextBlock
            {
                Text = option.CountText, FontSize = 12, Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
            });

            CategoryBox.Items.Add(new ComboBoxItem { Content = panel, Tag = option });
        }

        
        
        var pick = options.FirstOrDefault(o => o.Category == wanted)
                   ?? options.FirstOrDefault(o => o.Count > 0)
                   ?? options[0];

        CategoryBox.SelectedIndex = options.IndexOf(pick);
        _category = pick.Category;
    }

    private void ApplyFilter()
    {
        if (!_ready) return;   

        var key = SearchBox.Text?.Trim() ?? "";
        var catName = AppCategoryLook.LabelOf(_category);

        var shown = _all
            .Where(r => r.App.Category == _category)
            .Where(r => key.Length == 0 ||
                        r.Name.Contains(key, StringComparison.CurrentCultureIgnoreCase) ||
                        r.Sub.Contains(key, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        AppList.ItemsSource = shown;

        
        _shown = shown;

        if (_all.Count == 0)
        {
            CountLine.Text = "未扫描到已安装的软件";
        }
        else if (key.Length > 0)
        {
            CountLine.Text = $"共 {_all.Count} 个应用 · 在{catName}中筛出 {shown.Count} 个";
        }
        else if (_category == AppCategory.System)
        {
            
            CountLine.Text = $"共 {_all.Count} 个应用 · {catName} {shown.Count} 个 · 系统组件，不提供卸载";
        }
        else
        {
            CountLine.Text = $"共 {_all.Count} 个应用 · {catName} {shown.Count} 个";
        }

        var empty = shown.Count == 0;
        EmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        if (empty)
        {
            EmptyText.Text = _all.Count == 0
                ? "没有扫描到已安装的软件。"
                : key.Length == 0
                    ? $"「{catName}」这一类目前没有内容。"
                    : $"「{catName}」里没有名称或发布者包含「{key}」的应用。";
        }
    }

    private void Search_Changed(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        ApplyFilter();
    }

    private void Category_Changed(object sender, SelectionChangedEventArgs e)
    {
        
        if (CategoryBox.SelectedItem is not ComboBoxItem item || item.Tag is not CategoryOption option) return;
        _category = option.Category;
        ApplyFilter();
    }

    private void Rescan_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    
    
    
    
    
    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var inLeftover = LeftoverHost.Visibility == Visibility.Visible;

        var dialog = new ContentDialog
        {
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
            XamlRoot = XamlRoot,
            Title = inLeftover ? "残留判定说明" : "扫描范围与注意事项",
            Content = inLeftover ? BuildLeftoverAboutBody() : BuildAboutBody(),
            CloseButtonText = "关闭",
        };

        await dialog.ShowAsync();
    }

    
    private static UIElement BuildLeftoverAboutBody()
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

        Item("判定方法",
             "卸载开始前，先记录一批常见位置的第一级内容（用户数据目录、程序目录、开始菜单、"
             + "注册表中的软件项、开机自启项、文件关联等）；卸载程序退出后再记录一次，"
             + "取两次记录的差集作为本次卸载新增的内容。该判据无需预先准备任何材料。");

        Item("证据等级的含义",
             "「确定」：名称与软件名一致，或位置位于该软件的安装目录之内。"
             + "「很可能」：名称与程序文件名一致。"
             + "「不确定」：仅符合「卸载过程中新增」，而名称不符。"
             + "仅「确定」一级默认勾选，其余需自行确认。");

        Item("「不确定」档的成因",
             "痕迹嵌套于两级目录之下时（如「厂商名\\软件名」），浅层扫描只能取到外层目录，名称自然不符；"
             + "卸载程序顺带写入的其他内容同样归入此档。");

        Item("清理前的处理",
             "先行备份，再行移除：注册表项导出为标准 .reg 文件（双击即可导回），文件原样移入隔离区目录。"
             + "两者均存放于本程序数据目录的 quarantine 下，不参与自动清理，可在「隔离区」中查看并取回。");

        Item("保护范围",
             "Windows 目录、程序目录下的公共组件（Common Files、WindowsApps、系统组件目录）、"
             + "Microsoft 与组策略的注册表区域、文件关联的系统键（CLSID、TypeLib 等），"
             + "以及本程序自身的数据目录。该名单为代码内固定常量，界面勾选不改变其判定结果。");

        return panel;
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

        Item("扫描范围",
             "Windows「程序和功能」的安装记录（64 位、32 位、当前用户三处），以及本账户注册的 Microsoft Store 应用。"
             + "免安装（绿色版）软件无安装记录，不会出现在列表中。");

        Item("卸载方式",
             "调用软件自带的卸载程序；需要管理员权限时将弹出系统授权提示。"
             + "本程序仅负责发起调用，不删除文件、不修改注册表。");

        Item("分类",
             "桌面应用：传统安装包。运行库：VC++、.NET 等供其他软件依赖的组件，卸载前请确认无程序正在使用。"
             + "微软应用商店：本账户注册的商店应用。系统组件：Windows 自带的部件。");

        Item("系统组件的限制",
             "该类包含「文件资源管理器」「开始」「设置」「讲述人」等 Windows 自身部件，移除后对应系统功能将直接失效。"
             + "因此该类仅作查看：界面不提供卸载入口，后端亦拒绝执行。");

        Item("可能的失败",
             "部分软件会拒绝卸载或要求重启，此类限制来自软件自身。"
             + "卸载前请先退出该软件，并保存正在编辑的内容。");

        return panel;
    }

    
    
    
    
    
    
    
    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not UninstallRow row) return;
        if (!row.CanUninstall) return;

        var uninstall = new MenuFlyoutItem
        {
            Text = "卸载",
            Icon = new FontIcon { Glyph = "\uE74D" },
        };
        uninstall.Click += (_, _) => _ = UninstallAsync(row);

        var flyout = new MenuFlyout();
        flyout.Items.Add(uninstall);
        flyout.ShowAt(fe);
    }

    private async Task UninstallAsync(UninstallRow row)
    {
        if (_busy) return;

        var dialog = new ContentDialog
        {
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
            XamlRoot = XamlRoot,
            Title = $"卸载「{row.Name}」？",
            Content = BuildConfirmBody(row),
            PrimaryButtonText = "卸载",
            CloseButtonText = "取消",
            
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _busy = true;
        RescanButton.IsEnabled = false;
        SearchBox.IsEnabled = false;
        CategoryBox.IsEnabled = false;
        CountLine.Text = $"正在卸载「{row.Name}」…";

        try
        {
            
            var run = await AppUninstaller.UninstallAndScanAsync(row.App);

            
            await Task.Delay(1200);
            await LoadAsync();

            
            if (!run.Outcome.Started)
            {
                CountLine.Text = run.Outcome.Message;
                await InfoAsync("无法启动卸载程序",
                    run.Outcome.Message
                    + "\n\n可以改用系统「设置 → 应用」来卸载；"
                    + "也可以确认该软件的卸载程序是否仍在其安装目录中。");
                return;
            }

            var stillThere = _all.Any(r =>
                string.Equals(r.Name, row.Name, StringComparison.CurrentCultureIgnoreCase));
            var stillThereNote =
                $"「{row.Name}」仍存在于已安装列表：卸载可能已取消、失败，或需重启后方可完成。"
                + "为避免误删其正在使用的数据，本次不执行残留清理。";

            if (!run.Scanned)
            {
                
                CountLine.Text = run.Outcome.Message;
                await InfoAsync("未能完成残留检查",
                    run.Outcome.Message + "\n\n本次没有检查残留，也没有移除任何东西。");
                return;
            }

            if (stillThere)
            {
                
                
                
                
                CountLine.Text = stillThereNote;
                await InfoAsync("卸载未完成", stillThereNote);
                return;
            }

            if (run.Leftovers.Count == 0)
            {
                
                
                CountLine.Text = $"「{row.Name}」已卸载 · 没有扫描到残留";
                await InfoAsync("卸载完成",
                    $"「{row.Name}」已卸载，没有扫描到残留的文件或注册表项。\n\n"
                    + "如需一并查看它留在 AppData 里的缓存目录，可以到「AppData 管理」中查找。");
                return;
            }

            EnterLeftoverState(row.Name, run.Leftovers);
        }
        finally
        {
            _busy = false;
            RescanButton.IsEnabled = true;
            SearchBox.IsEnabled = true;
            CategoryBox.IsEnabled = true;
        }
    }

    
    
    
    
    
    
    
    
    
    private async Task InfoAsync(string title, string body)
    {
        var dialog = new ContentDialog
        {
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = body, FontSize = 13, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "关闭",
        };
        await dialog.ShowAsync();
    }

    
    
    

    
    
    
    
    
    
    private void EnterLeftoverState(string appName, IReadOnlyList<LeftoverItem> items)
    {
        _leftoverAppName = appName;
        _leftoverRows = items.Select(i => new LeftoverRow(i)).ToList();
        foreach (var r in _leftoverRows) r.Changed += (_, _) => UpdateLeftoverBar();

        TitleText.Text = "卸载残留";
        SetRiskBar(leftover: true);

        
        CategoryBox.Visibility = Visibility.Collapsed;
        SearchBox.Visibility = Visibility.Collapsed;
        RescanButton.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Visible;

        var any = _leftoverRows.Count > 0;

        if (any)
        {
            var strong = items.Count(i => i.Evidence == LeftoverEvidence.Strong);
            var medium = items.Count(i => i.Evidence == LeftoverEvidence.Medium);
            var weak = items.Count(i => i.Evidence == LeftoverEvidence.Weak);

            CountLine.Text = $"「{appName}」已卸载 · 发现 {items.Count} 项残留";
            if (strong + medium > 0 || weak > 0)
                CountLine.Text += $"（确定 {strong} · 很可能 {medium} · 不确定 {weak}）";
        }
        else
        {
            
            CountLine.Text = "当前没有待处理的残留项";
        }

        LeftoverList.ItemsSource = _leftoverRows;
        LeftoverList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        LeftoverEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        LeftoverBar.Visibility = any ? Visibility.Visible : Visibility.Collapsed;

        ListHost.Visibility = Visibility.Collapsed;
        LeftoverHost.Visibility = Visibility.Visible;
        UpdateLeftoverBar();

        AppLog.Info("leftover", $"进入残留确认：{appName}，{items.Count} 项（默认勾选 {_leftoverRows.Count(r => r.IsChecked == true)} 项）");
    }

    
    
    
    
    
    
    
    private void LeftoverEntry_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        
        if (LeftoverHost.Visibility == Visibility.Visible) return;

        EnterLeftoverState("", Array.Empty<LeftoverItem>());
    }

    
    
    
    
    
    
    
    
    private async void Quarantine_Click(object sender, RoutedEventArgs e)
    {
        var entries = QuarantineStore.Scan();
        var body = new StackPanel { Spacing = 10 };

        if (entries.Count == 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = $"隔离区里还没有记录。\n\n位置：{QuarantineStore.Root}",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            body.Children.Add(new TextBlock
            {
                Text = $"共 {entries.Count} 条清理记录。移除的内容均备份在这里，可随时取回。",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
            });

            var host = new StackPanel { Spacing = 8 };
            foreach (var entry in entries) host.Children.Add(BuildQuarantineRow(entry));

            body.Children.Add(new ScrollViewer
            {
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = host,
            });
        }

        var dialog = new ContentDialog
        {
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
            XamlRoot = XamlRoot,
            Title = "隔离区",
            Content = body,
            CloseButtonText = "关闭",
        };

        
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            AppLog.Exception("quarantine", ex, "打开隔离区对话框");
        }
    }

    
    private StackPanel BuildQuarantineRow(QuarantineEntry entry)
    {
        var row = new StackPanel { Spacing = 2 };

        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = entry.Title,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 0);
        head.Children.Add(title);

        var open = new Button { Content = "打开文件夹" };
        open.Click += (_, _) =>
        {
            if (!QuarantineStore.OpenInExplorer(entry.Dir))
                _ = InfoAsync("没能打开", $"隔离区目录可能已被移走或删除：\n{entry.Dir}");
        };
        Grid.SetColumn(open, 1);
        head.Children.Add(open);

        row.Children.Add(head);
        row.Children.Add(new TextBlock
        {
            Text = $"{entry.TimeText}　·　{entry.SubText}",
            FontSize = 12,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });

        return row;
    }

    
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ExitLeftoverState();
        _ = LoadAsync();
    }

    private void ExitLeftoverState()
    {
        
        
        _leftoverRows = new List<LeftoverRow>();
        _leftoverAppName = "";

        LeftoverList.ItemsSource = null;
        LeftoverList.Visibility = Visibility.Visible;      
        LeftoverEmpty.Visibility = Visibility.Collapsed;
        LeftoverHost.Visibility = Visibility.Collapsed;
        ListHost.Visibility = Visibility.Visible;

        TitleText.Text = "应用卸载";
        SetRiskBar(leftover: false);
        CategoryBox.Visibility = Visibility.Visible;
        SearchBox.Visibility = Visibility.Visible;
        RescanButton.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
    }

    
    
    
    
    
    
    private void SetRiskBar(bool leftover)
    {
        if (leftover)
        {
            RiskBar.Title = "仅移除已勾选的项目";
            RiskBar.Message = "标注为「很可能」「不确定」的项目默认不勾选：其判定依据仅为名称或位置相似，"
                            + "不排除属于其他软件，请逐项确认后再行处理。";
        }
        else
        {
            RiskBar.Title = "此操作将删除文件与注册表项";
            RiskBar.Message = "清理卸载残留会移除文件与注册表项。被移除的内容均先行备份至隔离区，可在「隔离区」中查看并取回。";
        }
    }

    
    private void UpdateLeftoverBar()
    {
        var chosen = _leftoverRows.Where(r => r.IsChecked == true).ToList();
        var size = chosen.Where(r => r.Item.SizeBytes > 0).Sum(r => r.Item.SizeBytes);

        CleanButton.Content = chosen.Count == 0 ? "清理选中" : $"清理选中 ({chosen.Count})";
        CleanButton.IsEnabled = chosen.Count > 0;

        var weakNote = chosen.Count(r => r.Item.Evidence == LeftoverEvidence.Weak);
        var text = chosen.Count == 0
            ? "未勾选任何项目"
            : $"已选 {chosen.Count} 项" + (size > 0 ? $" · 约 {HumanSize(size)}" : "");

        
        if (weakNote > 0) text += $"（其中 {weakNote} 项为「不确定」）";

        LeftoverSummary.Text = text;
    }

    private static string HumanSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.##} GB",
    };

    
    
    
    
    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var chosen = _leftoverRows.Where(r => r.IsChecked == true).Select(r => r.Item).ToList();
        if (chosen.Count == 0) return;

        var size = chosen.Where(i => i.SizeBytes > 0).Sum(i => i.SizeBytes);

        var confirm = new ContentDialog
        {
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
            XamlRoot = XamlRoot,
            Title = $"确认清理 {chosen.Count} 项残留？",
            Content = BuildCleanConfirmBody(chosen.Count, size),
            PrimaryButtonText = "清理",
            CloseButtonText = "取消",
            
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        _busy = true;
        CleanButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        CleanButton.Content = "正在清理…";
        LeftoverSummary.Text = "正在清理（需要管理员权限的项目将再次弹出系统授权提示）…";

        CleanupReport report;
        try
        {
            report = await Task.Run(() => LeftoverCleaner.CleanAsync(chosen, _leftoverAppName));
        }
        catch (Exception ex)
        {
            AppLog.Exception("uninstall", ex, "清理卸载残留");
            report = null!;
        }
        finally
        {
            _busy = false;
            BackButton.IsEnabled = true;
        }

        if (report is null)
        {
            LeftoverSummary.Text = "清理过程中出错，未执行任何移除。";
            UpdateLeftoverBar();
            return;
        }

        
        var dialog = new ContentDialog
        {
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
            XamlRoot = XamlRoot,
            Title = "清理完成",
            Content = BuildReportBody(report),
            PrimaryButtonText = report.QuarantineDir.Length > 0 ? "打开隔离区" : "",
            CloseButtonText = "关闭",
            DefaultButton = report.QuarantineDir.Length > 0
                ? ContentDialogButton.Primary
                : ContentDialogButton.Close,
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary && report.QuarantineDir.Length > 0)
            OpenInExplorer(report.QuarantineDir);

        ExitLeftoverState();
        await LoadAsync();
    }

    
    private void LeftoverMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not LeftoverRow row) return;

        var flyout = new MenuFlyout();

        
        
        var openable = row.Item.Kind == FootprintKind.Path
                       && (File.Exists(row.Item.Path) || Directory.Exists(row.Item.Path));

        if (openable)
        {
            var open = new MenuFlyoutItem
            {
                Text = "打开所在文件夹",
                Icon = new FontIcon { Glyph = "\uE838" },
            };
            open.Click += (_, _) => RevealInExplorer(row.Item.Path);
            flyout.Items.Add(open);
        }

        var copy = new MenuFlyoutItem
        {
            Text = "复制完整路径",
            Icon = new FontIcon { Glyph = "\uE8C8" },
        };
        copy.Click += (_, _) => CopyText(row.Item.DisplayPath);
        flyout.Items.Add(copy);

        flyout.ShowAt(fe);
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Exception("uninstall", ex, $"打开文件夹（{path}）");
        }
    }

    
    private static void RevealInExplorer(string path)
    {
        try
        {
            var args = Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = args,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Exception("uninstall", ex, $"定位到文件夹（{path}）");
        }
    }

    private static void CopyText(string text)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            AppLog.Exception("uninstall", ex, "复制路径到剪贴板");
        }
    }

    
    private static UIElement BuildCleanConfirmBody(int count, long size)
    {
        var panel = new StackPanel { Spacing = 8 };

        void Line(string text, double opacity)
            => panel.Children.Add(new TextBlock
            {
                Text = text, FontSize = 12, Opacity = opacity, TextWrapping = TextWrapping.Wrap,
            });

        Line($"将移除 {count} 项残留条目" + (size > 0 ? $"，合计约 {HumanSize(size)}" : "") + "。", 0.9);

        Line("移除前先行备份：注册表项导出为标准 .reg 文件，文件原样移入隔离区，"
             + "两者均位于本程序数据目录的 quarantine 下，可在「隔离区」中查看并取回。", 0.7);

        Line("仅移除已勾选的位置，不涉及任何系统目录与系统注册表项。"
             + "该限制为代码内固定常量，界面勾选不改变其判定结果。", 0.7);

        Line("清理操作不可撤销（备份仅供还原）。请先确认目标软件确已卸载完成。", 0.7);

        return panel;
    }

    
    private static UIElement BuildReportBody(CleanupReport report)
    {
        var panel = new StackPanel { Spacing = 8 };

        void Line(string text, double opacity)
            => panel.Children.Add(new TextBlock
            {
                Text = text, FontSize = 12, Opacity = opacity, TextWrapping = TextWrapping.Wrap,
            });

        Line($"已移除 {report.RemovedTotal} 项（共提交 {report.Results.Count} 项）。", 0.9);

        var notBackedUp = report.Count(CleanStatus.RemovedNoBackup);
        if (notBackedUp > 0)
            Line($"其中 {notBackedUp} 项因体积超过备份上限（2 GB）未留存备份，已直接删除。", 0.7);

        var failed = report.Count(CleanStatus.Failed);
        if (failed > 0)
            Line($"{failed} 项移除失败，通常因被运行中的程序占用，或需重启后重试。"
                 + "相关条目仍列于本页，可稍后重新处理。", 0.7);

        var blocked = report.Count(CleanStatus.Blocked);
        if (blocked > 0)
            Line($"{blocked} 项位于受保护位置，已拒绝执行。", 0.7);

        
        var stillThere = report.Results.Count(r => r.Item.Selected && r.Item.StillThere);
        if (stillThere > 0)
            Line($"复扫结果：仍有 {stillThere} 项存在于原位置。", 0.7);

        if (report.QuarantineDir.Length > 0)
        {
            Line("备份位置：" + report.QuarantineDir, 0.7);
            Line("· registry-backup.reg —— 双击可导入注册表项", 0.6);
            Line("· files\\ —— 被移出的文件原样保存，剪切回原位置即可", 0.6);
        }
        else
        {
            Line("本轮隔离区未能建立，因此未留存备份。", 0.8);
        }

        return panel;
    }

    
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
        Line(row.CategoryText, 0.6);

        Line(row.App.IsStore
            ? "将按 Microsoft Store 应用方式移除（等同于系统「设置 → 应用」中的卸载操作）。"
            : "将调用该软件自带的卸载程序。过程中可能弹出其自身界面及系统管理员授权提示。", 0.75);

        
        if (row.App.Category == AppCategory.Runtime)
            Line("⚠ 该项目属于运行库：其他软件可能依赖它，移除后将导致相关软件启动失败。请确认无程序依赖后再继续。", 0.85);

        Line("卸载不可撤销，请先退出该软件并保存正在编辑的内容。", 0.75);

        return panel;
    }
}
