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

    /// <summary>
    /// 行尾那颗「⋯」露不露。
    /// ⛔ 系统组件**永远不露** —— 它们没有任何可执行的操作，留一颗按钮只会让人以为点得动
    ///    （Nick 2026-10-14 截图报的正是这个：一整列「文件资源管理器」「开始」都带着卸装按钮）。
    /// </summary>
    public Visibility ActionsVisibility => CanUninstall ? Visibility.Visible : Visibility.Collapsed;

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
/// 残留清单里的一行（把 <see cref="LeftoverItem"/> 翻译成界面要的样子）。
///
/// ⚠️ 单独包一层而不直接把 <see cref="LeftoverItem"/> 丢给模板，是因为<b>勾选状态要能双向跑</b>：
///    用户点勾选框 → 模型要知道（算"已选几项"）；代码里改模型（默认勾选）→ 界面也要跟着动。
///    后者必须有属性通知，所以这里实现 <see cref="System.ComponentModel.INotifyPropertyChanged"/>。
/// </summary>
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

    /// <summary>主标题：目录/文件名，或注册表值的名字。</summary>
    public string Title => Item.DisplayLeaf;

    /// <summary>副标题：它待在哪儿（环境变量前缀已缩写）。</summary>
    public string Location => Item.DisplayParent;

    /// <summary>完整位置（提示气泡用 —— 缩写过的版本会藏掉真路径）。</summary>
    public string FullPath => Item.DisplayPath;

    /// <summary>「文件夹 · 12.4 MB」这一小串。</summary>
    public string Meta { get; }

    /// <summary>为什么判成这一档（提示气泡）。</summary>
    public string Reason => Item.Reason.Length > 0 ? Item.Reason : "无可用判定依据";

    /// <summary>
    /// 勾选状态。
    /// ⚠️ 用 <c>bool?</c> 而不是 <c>bool</c>：<c>CheckBox.IsChecked</c> 是 <c>bool?</c>，
    ///    x:Bind 虽然能做隐式转换，但<b>三态</b>下 <c>null</c> 会被折成 false，语义就不对了 ——
    ///    这里直接把类型对上，免得以后有人开三态时踩坑。
    /// </summary>
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

    /// <summary>勾选状态变了（页面拿它更新底部那条统计）。</summary>
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

/// <summary>
/// 应用卸载页（2026-10-12 加，挂在「实验性功能」下；2026-10-13 两版、2026-10-14 第三、四版）。
///
/// 主题是「以软件下载起步的程序，也管卸载」—— 把本机装了什么列出来，能直接卸。
/// 扫描走 <see cref="InstalledApps"/>（跟「本机核实」同一套数据源），
/// 卸载走 <see cref="AppUninstaller"/>（调厂商自己的卸载程序，我们不删文件）。
///
/// 2026-10-14 第三版（Nick 的第三轮反馈，全在"像网页"这件事上）：
///   ① **表格视图砍掉** —— 与列表内容完全相同，是伪功能；
///   ② 标题下的解释话 + 工具条卡片一起删，标题下方改成动态计数；
///   ③ 分类 / 搜索 / 重新扫描 / 说明 并到标题行右侧，搜索换 `AutoSuggestBox`；
///   ④ 行尾大按钮改透明图标钮「⋯」+ 手建 `MenuFlyout`。
///   ⛔⛔ 另外堵了一个**真会出事的**漏洞：「系统组件」分类里的包 IsStore=true ⇒ 旧判据认为可卸，
///   界面上「文件资源管理器」「开始」「设置」都带着能点的卸载键。现在后端两道闸 + 界面不给入口。
///
/// ⚠️ 扫描一定要丢后台：要遍历三个注册表视图的几百个键、还要问一遍 PackageManager，
///    某些机器（开着安全软件的）读注册表明显发涩，压 UI 线程会卡成一片白。
/// </summary>
public sealed partial class UninstallPage : Page
{
    private List<UninstallRow> _all = new();
    private AppCategory _category = AppCategory.Desktop;
    private bool _ready;         // 数据到位了没（构造期的控件事件要忽略）
    private bool _loading;       // 扫描中（防重入）
    private bool _busy;          // 正在跑一次卸载或清理（防重入）

    /// <summary>残留状态：当前摆出来的那些行。</summary>
    private List<LeftoverRow> _leftoverRows = new();

    /// <summary>残留状态：这次卸的是哪个软件（清理记录里要写）。</summary>
    private string _leftoverAppName = "";

    public UninstallPage()
    {
        InitializeComponent();

        // 两侧留白 / 标题行上的滚轮也转给列表（见 Core/WheelRelay.cs）。
        // ⚠️ 两个列表都传进去：同一页里互斥的两个状态各有一个滚动区，指针压在哪个就转给哪个。
        Core.WheelRelay.Attach(this, AppList, LeftoverList);
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
        CountLine.Text = "正在扫描…";

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

        AppList.ItemsSource = shown;

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
            // 只读的一类：把"为什么没有卸载键"写在计数这行，省得用户找
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
        // ⚠️ 只在用户真的敲字时筛 —— 程序改 Text 同样会触发这个事件，靠 Reason 区分
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        ApplyFilter();
    }

    private void Category_Changed(object sender, SelectionChangedEventArgs e)
    {
        // ⚠️ 重建下拉内容时 SelectedItem 会先变 null 再变新项 —— 那一下忽略掉
        if (CategoryBox.SelectedItem is not ComboBoxItem item || item.Tag is not CategoryOption option) return;
        _category = option.Category;
        ApplyFilter();
    }

    private void Rescan_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    /// <summary>
    /// 「说明」按钮 —— 原来那一大段页脚说明（占将近四分之一屏）收进这里。
    /// ⚠️ 两个状态共用一个按钮，只是正文不一样（残留状态下讲的是"凭什么说这些是残留"）。
    /// ⚠️ ContentDialog 至少要给一个关闭钮（本项目硬规约）。
    /// </summary>
    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var inLeftover = LeftoverHost.Visibility == Visibility.Visible;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = inLeftover ? "残留判定说明" : "扫描范围与注意事项",
            Content = inLeftover ? BuildLeftoverAboutBody() : BuildAboutBody(),
            CloseButtonText = "关闭",
        };

        await dialog.ShowAsync();
    }

    /// <summary>残留状态下的说明：讲清判据与边界，别让人以为这是"灵媒"。</summary>
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
             + "两者均存放于本程序数据目录的 quarantine 下，不参与自动清理，可随时还原。");

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

    /// <summary>
    /// 行尾「⋯」—— 弹出这一行的操作菜单。
    ///
    /// ⚠️ 菜单在这里**手建**，不把 `MenuFlyout` 写进 `DataTemplate`：Flyout 不在可视树里，
    ///    模板内的 `{x:Bind}` 求值上下文很脆，而 XamlCompiler 对这个组合本身就有前科
    ///    （见文件头注释里的 WMC9999）。手建时用闭包直接捕获这一行，最稳。
    /// </summary>
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
        CountLine.Text = $"正在卸载「{row.Name}」…";

        try
        {
            // 卸载 + 残留体检（前后两份浅层快照求差集，见 Services/AppUninstaller.UninstallAndScanAsync）
            var run = await AppUninstaller.UninstallAndScanAsync(row.App);

            // 卸载程序退出了不等于登记表就更新了（有些要等一两拍）→ 稍等再重扫
            await Task.Delay(1200);
            await LoadAsync();

            // ⚠️ 提示必须放在 LoadAsync **之后** —— 它会把 CountLine 重写成统计值
            if (!run.Outcome.Started)
            {
                CountLine.Text = run.Outcome.Message;
                return;
            }

            var stillThere = _all.Any(r =>
                string.Equals(r.Name, row.Name, StringComparison.CurrentCultureIgnoreCase));

            if (!run.Scanned)
            {
                // 没做体检（超时 / 用户取消了 UAC 之后的回落）—— 如实说，别假装"没有残留"
                CountLine.Text = run.Outcome.Message;
                return;
            }

            // ⛔⛔ 卸载记录**还在**（卸载被取消 / 失败 / 要重启才生效）⇒ 一律不进残留状态。
            //     原因：这时候"名字对得上的痕迹"里有一大半是这个软件**正在用的活数据**
            //     （它的 AppData、它自己的注册表键、它的安装目录），清掉就等于把一个还在装的软件
            //     拆坏。宁可什么都不做，也不能在这个状态下摆出一份"看起来像残留"的清单。
            if (stillThere)
            {
                CountLine.Text =
                    $"「{row.Name}」仍存在于已安装列表：卸载可能已取消、失败，或需重启后方可完成。"
                    + "为避免误删其正在使用的数据，本次不执行残留清理。";
                return;
            }

            if (run.Leftovers.Count == 0)
            {
                CountLine.Text = $"「{row.Name}」已卸载，未发现残留。";
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

    // ══════════════════════════════════════════════════════════════════════════
    //  状态二：卸载残留确认
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 切到残留状态。
    /// ⛔ 这里只切**状态**，不改导航 —— 它是同一页里的第二屏（顶栏那些控件换岗）。
    /// </summary>
    private void EnterLeftoverState(string appName, IReadOnlyList<LeftoverItem> items)
    {
        _leftoverAppName = appName;
        _leftoverRows = items.Select(i => new LeftoverRow(i)).ToList();
        foreach (var r in _leftoverRows) r.Changed += (_, _) => UpdateLeftoverBar();

        TitleText.Text = "卸载残留";
        SetRiskBar(leftover: true);

        // 顶栏换岗：列表那一组收起来，露出「返回列表」
        CategoryBox.Visibility = Visibility.Collapsed;
        SearchBox.Visibility = Visibility.Collapsed;
        RescanButton.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Visible;

        var strong = items.Count(i => i.Evidence == LeftoverEvidence.Strong);
        var medium = items.Count(i => i.Evidence == LeftoverEvidence.Medium);
        var weak = items.Count(i => i.Evidence == LeftoverEvidence.Weak);

        CountLine.Text = $"「{appName}」已卸载 · 发现 {items.Count} 项残留";
        if (strong + medium > 0 || weak > 0)
            CountLine.Text += $"（确定 {strong} · 很可能 {medium} · 不确定 {weak}）";

        LeftoverList.ItemsSource = _leftoverRows;

        ListHost.Visibility = Visibility.Collapsed;
        LeftoverHost.Visibility = Visibility.Visible;
        UpdateLeftoverBar();

        AppLog.Info("leftover", $"进入残留确认：{appName}，{items.Count} 项（默认勾选 {_leftoverRows.Count(r => r.IsChecked == true)} 项）");
    }

    /// <summary>回到应用列表状态。⛔ 什么都**没删** —— 「不用了」和「返回列表」走的是同一条路。</summary>
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ExitLeftoverState();
        _ = LoadAsync();
    }

    private void ExitLeftoverState()
    {
        // 挂着的事件订阅不用逐个退订：这批 LeftoverRow 马上就要被整体丢弃，
        // 订阅关系是互相引用的一小圈对象，GC 一起收走（⛔ 别写 `r.Changed -= (_, _) => {}` —— 那是句废话）
        _leftoverRows = new List<LeftoverRow>();
        _leftoverAppName = "";

        LeftoverList.ItemsSource = null;
        LeftoverHost.Visibility = Visibility.Collapsed;
        ListHost.Visibility = Visibility.Visible;

        TitleText.Text = "应用卸载";
        SetRiskBar(leftover: false);
        CategoryBox.Visibility = Visibility.Visible;
        SearchBox.Visibility = Visibility.Visible;
        RescanButton.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 顶部那条风险提示的两个文案。⛔ 只在两个状态**之间**换，不新增控件。
    /// ⚠️ 提示条是**常驻**的（`IsClosable="False"`）：这个是会动系统和注册表的功能，
    ///    红条不允许被关掉，⛔ 别为了"少一点打扰"把关闭钮加回来。
    /// ⛔ 文案一律书面语（项目硬规约：界面文字不出现口语、不出现"你"、不用"啥/搞定/一下"这类词）。
    /// </summary>
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
            RiskBar.Message = "清理卸载残留会移除文件与注册表项。所有被移除的内容均先行备份至隔离区，可随时还原。";
        }
    }

    /// <summary>底部那条：已选几项、大约多少体积；主按钮的字数也跟着变。</summary>
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

        // 不确定的那些要额外提醒一句 —— 它们是"名字对不上"的，勾了就是用户自己的判断
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

    /// <summary>
    /// 执行清理。⚠️ 真正的删除在后台线程上跑（注册表遍历 + 文件移动都可能慢），
    /// 但 <see cref="LeftoverCleaner"/> 内部全程同步 IO，所以包一层 Task.Run。
    /// </summary>
    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var chosen = _leftoverRows.Where(r => r.IsChecked == true).Select(r => r.Item).ToList();
        if (chosen.Count == 0) return;

        var size = chosen.Where(i => i.SizeBytes > 0).Sum(i => i.SizeBytes);

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"确认清理 {chosen.Count} 项残留？",
            Content = BuildCleanConfirmBody(chosen.Count, size),
            PrimaryButtonText = "清理",
            CloseButtonText = "取消",
            // 不可逆操作，默认落在取消上
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

        // 汇报：清完了什么、还剩什么、后悔药在哪
        var dialog = new ContentDialog
        {
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

    /// <summary>行尾「⋯」：给残留行用的（打开所在文件夹 / 复制路径）。菜单同样是**手建**的。</summary>
    private void LeftoverMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not LeftoverRow row) return;

        var flyout = new MenuFlyout();

        // 文件/文件夹才有"打开所在位置"；注册表项没有可靠的"跳过去"命令行参数，
        // ⛔ 不要假装有（regedit 不支持定位到某个键），只给复制路径。
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

    /// <summary>在资源管理器里**选中**这个文件/文件夹（不是打开它）。</summary>
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

    /// <summary>清理确认框的正文：说清"会发生什么、后悔药在哪"。</summary>
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
             + "两者均位于本程序数据目录的 quarantine 下，可随时还原。", 0.7);

        Line("仅移除已勾选的位置，不涉及任何系统目录与系统注册表项。"
             + "该限制为代码内固定常量，界面勾选不改变其判定结果。", 0.7);

        Line("清理操作不可撤销（备份仅供还原）。请先确认目标软件确已卸载完成。", 0.7);

        return panel;
    }

    /// <summary>清理结果：实际删掉什么、还剩什么、备份在哪。</summary>
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

        // ⛔ 这一条必须写：报告讲的是"实际结果"，不是"我们打算做的事"
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
            ? "将按 Microsoft Store 应用方式移除（等同于系统「设置 → 应用」中的卸载操作）。"
            : "将调用该软件自带的卸载程序。过程中可能弹出其自身界面及系统管理员授权提示。", 0.75);

        // 运行库不是"用户主动装的应用"，卸掉多半会连累别的软件 —— 这里必须多一句
        if (row.App.Category == AppCategory.Runtime)
            Line("⚠ 该项目属于运行库：其他软件可能依赖它，移除后将导致相关软件启动失败。请确认无程序依赖后再继续。", 0.85);

        Line("卸载不可撤销，请先退出该软件并保存正在编辑的内容。", 0.75);

        return panel;
    }
}
