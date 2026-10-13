using System;
using System.Collections.Generic;
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

/// <summary>分类下拉里的一项。</summary>
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

    /// <summary>⚠️ x:Bind 默认一次性求值 —— 计数变化时靠**重建 ItemsSource**刷新，不是靠通知。</summary>
    public string CountText => Count.ToString("N0");
}

/// <summary>列表里的一行（把 <see cref="InstalledApp"/> 翻译成界面要的样子）。</summary>
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

        // 副标题：发布者。都取不到就退一步 —— 商店包的位置是 C:\Program Files\WindowsApps\…（一长串），
        // 不如显示包标识（Microsoft.WindowsCalculator_11.…）来得可读。
        //（⛔ 别写"未知"之类的占位符，看着像没加载出来）
        var bits = new List<string>();
        var publisher = FriendlyPublisher(app.Publisher);
        if (publisher.Length > 0) bits.Add(publisher);
        if (bits.Count == 0 && app.PackageFullName.Length > 0)
            bits.Add(app.PackageFullName.Split('_')[0]);
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
    public string CategoryText { get; }

    /// <summary>
    /// 软件自己的图标。**惰性取**：第一次被界面问到才去掏（见 <see cref="AppIconService"/>）。
    /// ⚠️ ListView 是虚拟化的，只有真正滚到的那几行会问 —— 所以几百个应用也不会一次性全掏一遍。
    /// ⚠️ 必须在 UI 线程求值（要建 XAML 对象）：x:Bind 在行模板实例化时求值，天然满足。
    /// </summary>
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

    /// <summary>掏不到图标时的占位字形 —— 按分类给，至少能一眼看出"这是个商店应用/运行库"。</summary>
    public string PlaceholderGlyph => AppCategoryLook.GlyphOf(App.Category);

    /// <summary>
    /// 发布者友好化：注册表里偶尔直接塞的是证书主题（<c>CN=Microsoft Corporation, O=…</c>），
    /// 那串摆到界面上没法看，取 CN= 后面那一截。
    /// </summary>
    private static string FriendlyPublisher(string raw)
    {
        if (raw.Length == 0) return "";
        if (!raw.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return raw;

        var s = raw[3..];
        var comma = s.IndexOf(',');
        return (comma > 0 ? s[..comma] : s).Trim();
    }
}

/// <summary>
/// 应用卸载页（2026-10-12 加，挂在「实验性功能」下；2026-10-13 两版改版）。
///
/// 主题是「以软件下载起步的程序，也管卸载」—— 把本机装了什么列出来，能直接卸。
/// 扫描走 <see cref="InstalledApps"/>（跟「本机核实」同一套数据源），
/// 卸载走 <see cref="AppUninstaller"/>（调厂商自己的卸载程序，我们不删文件）。
///
/// 2026-10-13 第二版（Nick 的第二轮反馈）：
///   ① **分类改下拉单选** —— 原来是四颗可独立开关的多选芯片，一次混着显示好几类，他说"不要混在一起"；
///   ② **列表 / 表格两种形式**（右上角切）—— 表格是「图标 | 名称 | 操作」三列，紧凑看全貌；
///   ③ **页宽回来** —— 上一版铺满，他说"其他页都有页宽，为什么这个被改没了"；
///   ④ 页脚那一大段「扫描范围与须知」收成一枚**带图标的「说明」按钮**（放标题行右侧）。
///
/// ⚠️ 扫描一定要丢后台：要遍历三个注册表视图的几百个键、还要问一遍 PackageManager，
///    某些机器（开着安全软件的）读注册表明显发涩，压 UI 线程会卡成一片白。
/// </summary>
public sealed partial class UninstallPage : Page
{
    private List<UninstallRow> _all = new();
    private AppCategory _category = AppCategory.Desktop;
    private bool _table;         // 当前是不是表格视图
    private bool _ready;         // 数据到位了没（构造期的控件事件要忽略）
    private bool _loading;       // 扫描中（防重入）
    private bool _busy;          // 正在跑一次卸载（防重入）

    public UninstallPage()
    {
        InitializeComponent();

        // 两侧留白 / 标题行上的滚轮也转给列表（见 Core/WheelRelay.cs）。
        // ⚠️ 接力目标是**列表视图**那个：表格视图与它共用同一个滚动语义，WheelRelay 找的是主容器。
        Core.WheelRelay.Attach(this, AppList);

        // 视图切换：初始「列表」。⚠️ 显式设一次 —— RadioButtons 在 Loaded 时是否自动选中第一项
        // 不能指望（LegalWindow 的 SelectorBar 踩过同样的坑）。
        ViewChoice.SelectedIndex = 0;
        ShowView();
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
        AppTable.ItemsSource = null;
        EmptyPanel.Visibility = Visibility.Collapsed;
        RescanButton.IsEnabled = false;
        CountText.Text = "正在扫描…";

        try
        {
            var apps = await Task.Run(() =>
            {
                var list = new List<InstalledApp>();
                list.AddRange(InstalledApps.Enumerate());              // 「程序和功能」
                list.AddRange(InstalledApps.EnumerateStorePackages()); // 商店应用
                return list.OrderBy(a => a.Name, StringComparer.CurrentCulture).ToList();
            });

            _all = apps.Select(a => new UninstallRow(a)).ToList();
            _ready = true;

            FillCategories();   // 计数在这会儿才算得出来
            ApplyFilter();

            // 图标预热：把"读文件掏图标"那一半丢后台先跑掉（剩下拼位图很快，见 AppIconService.Prewarm）。
            // ⚠️ 必须**排在列表出来之后**再开 —— 先让用户看到列表，图标慢慢补上，
            //    别为了几百个图标让"扫描中"多转两秒。
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
            AppTable.ItemsSource = null;
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

    /// <summary>
    /// 把四个分类灌进下拉（带各自计数）。
    /// ⚠️ 重建 ItemsSource 会让 <c>SelectedItem</c> 短暂变 null（触发一次 SelectionChanged）——
    ///    那个分支会读到 null 而**不改**当前分类，所以这里是安全的，不需要"抑制标志"。
    /// </summary>
    private void FillCategories()
    {
        var wanted = _category;

        var options = AppCategoryLook.Order
            .Select(c => new CategoryOption(c, AppCategoryLook.GlyphOf(c), AppCategoryLook.LabelOf(c),
                                           _all.Count(r => r.App.Category == c)))
            .ToList();

        // ⚠️ 选项内容在这里手建，XAML 那边只有一个空 ComboBox。
        //    （ItemTemplate + x:Bind 会让 XamlCompiler 在本页的代码生成阶段崩掉 —— 2026-10-13 实测）
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

        // 优先沿用用户当前选的那一类；重扫后那一类若空了，退到第一个有内容的；
        // 全空（本机什么都没扫到）就停在第一项，别把下拉搞成空白。
        var pick = options.FirstOrDefault(o => o.Category == wanted)
                   ?? options.FirstOrDefault(o => o.Count > 0)
                   ?? options[0];

        CategoryBox.SelectedIndex = options.IndexOf(pick);
        _category = pick.Category;
    }

    private void ApplyFilter()
    {
        if (!_ready) return;   // 构造期 TextChanged / SelectionChanged 也会进来，那时还没数据

        var key = SearchBox.Text?.Trim() ?? "";
        var catName = AppCategoryLook.LabelOf(_category);

        var shown = _all
            .Where(r => r.App.Category == _category)
            .Where(r => key.Length == 0 ||
                        r.Name.Contains(key, StringComparison.CurrentCultureIgnoreCase) ||
                        r.Sub.Contains(key, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        // 两个视图共用同一份结果（切换视图不重扫、不重算）
        AppList.ItemsSource = shown;
        AppTable.ItemsSource = shown;

        CountText.Text = _all.Count == 0
            ? "没有扫描到已安装的软件"
            : key.Length == 0
                ? $"共 {_all.Count} 个应用 · {catName} {shown.Count} 个"
                : $"共 {_all.Count} 个应用 · {catName}筛出 {shown.Count} 个";

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

    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Category_Changed(object sender, SelectionChangedEventArgs e)
    {
        // ⚠️ 重建下拉内容时 SelectedItem 会先变 null 再变新项 —— 那一下忽略掉
        if (CategoryBox.SelectedItem is not ComboBoxItem item || item.Tag is not CategoryOption option) return;
        _category = option.Category;
        ApplyFilter();
    }

    private void ViewChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewChoice.SelectedItem is not RadioButton rb || rb.Tag is not string tag) return;
        _table = tag == "table";
        ShowView();
    }

    private void ShowView()
    {
        AppList.Visibility = _table ? Visibility.Collapsed : Visibility.Visible;
        AppTable.Visibility = _table ? Visibility.Visible : Visibility.Collapsed;
        TableHeader.Visibility = _table ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Rescan_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    /// <summary>
    /// 「说明」按钮 —— 原来那一大段页脚说明（占将近四分之一屏）收进这里。
    /// ⚠️ ContentDialog 至少要给一个关闭钮（本项目硬规约）。
    /// </summary>
    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "扫描范围与须知",
            Content = BuildAboutBody(),
            CloseButtonText = "知道了",
        };

        await dialog.ShowAsync();
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
             + "免安装（绿色版）软件没有任何登记，不会出现在列表里。");

        Item("卸载方式",
             "调起软件自带的卸载程序（需要管理员权限时会弹出系统授权提示）。"
             + "本程序只负责发起，不删文件、不动注册表 —— 这比手动删目录安全。");

        Item("分类",
             "「系统组件」是 Windows 自带的组件包，既卸不掉也不该卸，所以不放在默认分类里 —— "
             + "需要查看时在上面的分类下拉里选它即可。");

        Item("可能的失败",
             "部分软件（尤其系统组件、以及其他账户下的商店应用）会拒绝卸载或要求重启，这些限制来自软件本身。"
             + "卸载前请先退出该软件，并保存好正在编辑的内容。");

        return panel;
    }

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
        CategoryBox.IsEnabled = false;
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
            CategoryBox.IsEnabled = true;
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
        Line(row.CategoryText, 0.6);

        Line(row.App.IsStore
            ? "将按 Microsoft Store 应用的方式移除（等同于在系统「设置 → 应用」里点卸载）。"
            : "将调起该软件自带的卸载程序。期间可能弹出它自己的界面，以及系统的管理员授权提示。", 0.75);
        Line("卸载不可撤销，请先退出该软件并保存好正在编辑的内容。", 0.75);

        return panel;
    }
}
