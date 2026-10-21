using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Controls;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 列表里的一行。两种来源共用一个类型：
///  - 体检行（<see cref="FromFolder"/>）：一级目录，**永远不可清理**（操作列显示"只读"）；
///  - 清理行（<see cref="FromEntry"/>）：命中白名单的缓存目录。
///
/// ⚠️ 体积是**后台一个个填进来的**，所以 <c>SizeText</c>/<c>CountText</c>/<c>CanClean</c> 在 XAML 里
///    必须写 <c>Mode=OneWay</c> —— <c>x:Bind</c> 在 DataTemplate 里默认是 OneTime，
///    漏了就永远停在"统计中…"（本项目已经踩过一次）。
/// </summary>
public sealed partial class AppDataRow : INotifyPropertyChanged
{
    private long _bytes = -1;      // -1 = 还没统计
    private int _files;
    private bool _busy;
    private string _status = "";

    private AppDataRow(string name, string sub, string glyph, bool cleanable,
        AppDataEntry? entry, AppDataKind kind, string measuredPath)
    {
        Name = name;
        Sub = sub;
        Glyph = glyph;
        Cleanable = cleanable;
        Entry = entry;
        Kind = kind;
        MeasuredPath = measuredPath;
    }

    /// <summary>体检行：一级目录，只读。</summary>
    public static AppDataRow FromFolder(AppDataFolder f) => new(
        f.Name,
        AppDataLook.ZoneLabelOf(f.Zone) + " · 一级目录（只读体检，不提供清理）",
        "\uE8B7",
        cleanable: false, entry: null, AppDataKind.Temp, f.FullPath);

    /// <summary>清理行：白名单命中的缓存目录。</summary>
    public static AppDataRow FromEntry(AppDataEntry e) => new(
        e.RelPath, e.Note, AppDataLook.GlyphOf(e.Kind),
        cleanable: true, e, e.Kind, e.FullPath);

    public string Name { get; }
    public string Sub { get; }
    public string Glyph { get; }
    public bool Cleanable { get; }
    public AppDataEntry? Entry { get; }
    public AppDataKind Kind { get; }

    /// <summary>统计体积时用的路径（概览行是一级目录本身，清理行是那个缓存目录）。</summary>
    public string MeasuredPath { get; }

    public long Bytes
    {
        get => _bytes;
        set
        {
            if (_bytes == value) return;
            _bytes = value;
            Raise(nameof(SizeText));
            Raise(nameof(CanClean));
            Raise(nameof(ActionTip));
        }
    }

    public int Files
    {
        get => _files;
        set
        {
            if (_files == value) return;
            _files = value;
            Raise(nameof(CountText));
        }
    }

    public bool Busy
    {
        get => _busy;
        set
        {
            if (_busy == value) return;
            _busy = value;
            Raise(nameof(CanClean));
            Raise(nameof(CountText));
            Raise(nameof(ActionTip));
        }
    }

    /// <summary>清理完之后的一句状态（"已清空" / "已清空（跳过 N 项）"），压过文件数显示。</summary>
    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            Raise(nameof(CountText));
        }
    }

    public string SizeText => _bytes < 0 ? "统计中…" : AppLog.Size(_bytes);

    public string CountText =>
        _busy ? "清理中…"
        : _status.Length > 0 ? _status
        : _files > 0 ? _files.ToString("N0") + " 个文件"
        : "";

    /// <summary>⛔ 体积没统计完不给点：确认弹窗要写清"将释放多少"，没数字就没法交代。</summary>
    public bool CanClean => Cleanable && !_busy && _bytes >= 0;

    public string ActionTip =>
        !Cleanable ? ""
        : _busy ? "正在清理"
        : _bytes < 0 ? "等体积统计完再清理"
        : $"清理「{Name}」（永久删除，删掉找不回来）";

    public Visibility CleanVisibility => Cleanable ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>复选框只在可清理分类下出现（只读体检那一层没有删除入口）。</summary>
    public Visibility CheckVisibility => Cleanable ? Visibility.Visible : Visibility.Collapsed;

    private bool _checked;

    /// <summary>
    /// 这一行有没有被勾上。
    ///
    /// ⚠️ 2026-10-16 起勾选状态**存在行自己身上**，不再借 `ListView.SelectedItems`：
    ///    先前用的是系统多选（`SelectionMode=Multiple`）—— 那样勾选框由 `ListViewItemPresenter` 画，
    ///    取的是 `ListViewItemCheckBoxBrush`（前景色系），深色下是"浅灰底 + 白勾"，
    ///    既不是强调色、也与「应用卸载」页残留列表的标准 CheckBox 观感割裂。
    ///    改回标准 CheckBox 后，勾选这件事就归行自己管，页面只读它。
    /// </summary>
    public bool IsChecked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Raise(nameof(IsChecked));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// AppData 管理（2026-10-13 新功能）。三层边界的定义全在 <see cref="AppDataScan"/> 的类注释里，
/// 这一层只负责：**把体积一个个问出来、按分类摆好、把删除动作交给服务层**。
///
/// 扫描是**两段式**的（照 AppIconService 预热那个路子）：
///  阶段 A：只读目录结构 —— 秒出，列表立刻有内容，体积先显示"统计中…"；
///  阶段 B：后台逐个递归统计体积 —— 不冻 UI、可取消，填一个刷一个。
/// 顺序是「本地一级目录 → 漫游一级目录 → 可清理项」，这样默认那页（概览 · 本地）的数字最先齐。
/// </summary>
public sealed partial class AppDataPage : Page
{
    /// <summary>下拉里的一项（只在本页用，所以是私有嵌套类 —— 不需要进 XAML）。</summary>
    private sealed class CategoryOption
    {
        public CategoryOption(bool isOverview, AppDataZone zone, AppDataKind kind, string glyph, string label)
        {
            IsOverview = isOverview;
            Zone = zone;
            Kind = kind;
            Glyph = glyph;
            Label = label;
        }

        public bool IsOverview { get; }
        public AppDataZone Zone { get; }
        public AppDataKind Kind { get; }
        public string Glyph { get; }
        public string Label { get; }

        /// <summary>下拉项里那个计数 TextBlock 的引用 —— 统计完要**就地**改它（重建下拉会把选中项弄丢）。</summary>
        public TextBlock? CountBlock { get; set; }
    }

    private List<AppDataRow> _foldersLocal = new();
    private List<AppDataRow> _foldersRoaming = new();
    private List<AppDataRow> _clean = new();

    private CategoryOption? _category;
    private CancellationTokenSource? _cts;

    private bool _ready;        // 目录结构到位了没（构造期的事件要忽略）
    private bool _measuring;    // 正在统计体积
    private bool _busy;         // 正在跑一次清理

    public AppDataPage()
    {
        InitializeComponent();

        // 两侧留白 / 标题行上的滚轮也转给列表（Core/WheelRelay.cs）
        WheelRelay.Attach(this, EntryList);

        // 构造期控件还没进可视化树，扫描放 Loaded（与其它页一致）
        Loaded += (_, _) =>
        {
            if (!_ready && _cts is null) _ = ScanAsync();
        };
    }

    /// <summary>离开页面就掐掉后台统计 —— 上百个目录逐个递归，别让它跟着跑到别的页去。</summary>
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
            AppLog.Exception("appdata", ex, "取消 AppData 统计");
        }
    }

    // ── 扫描 ──────────────────────────────────────────────────────────

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (_busy) return;

        CancelScan();
        var cts = new CancellationTokenSource();
        _cts = cts;

        _ready = false;
        _measuring = true;

        LoadingPanel.Visibility = Visibility.Visible;
        EmptyPanel.Visibility = Visibility.Collapsed;
        CleanBar.Visibility = Visibility.Collapsed;
        EntryList.ItemsSource = null;
        CountLine.Text = "正在读取目录结构…";

        // ── 阶段 A：只读目录结构（秒出）──
        List<AppDataFolder> folders;
        List<AppDataEntry> clean;

        try
        {
            (folders, clean) = await Task.Run(() =>
            {
                var f = AppDataScan.TopFolders().ToList();
                var c = AppDataScan.FindCleanable().ToList();
                return (f, c);
            }, cts.Token);
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            AppLog.Fail("appdata", ex, "读取 AppData 目录结构");
            _measuring = false;
            ShowEmpty("读取目录结构失败，详情见日志。");
            return;
        }

        if (cts.IsCancellationRequested) return;

        _foldersLocal = folders.Where(f => f.Zone == AppDataZone.Local).Select(AppDataRow.FromFolder).ToList();
        _foldersRoaming = folders.Where(f => f.Zone == AppDataZone.Roaming).Select(AppDataRow.FromFolder).ToList();
        _clean = clean.Select(AppDataRow.FromEntry).ToList();

        _ready = true;
        FillCategories();
        ShowCategory();
        LoadingPanel.Visibility = Visibility.Collapsed;

        AppLog.Info("appdata",
            $"目录结构读取完成：本地 {_foldersLocal.Count} 个一级目录、漫游 {_foldersRoaming.Count} 个、可清理 {_clean.Count} 项");

        // ── 阶段 B：后台逐个统计体积 ──
        var queue = new List<AppDataRow>(_foldersLocal.Count + _foldersRoaming.Count + _clean.Count);
        queue.AddRange(_foldersLocal);
        queue.AddRange(_foldersRoaming);
        queue.AddRange(_clean);

        await MeasureAllAsync(queue, cts);

        if (cts.IsCancellationRequested) return;

        _measuring = false;
        ShowCategory();
    }

    private async Task MeasureAllAsync(List<AppDataRow> queue, CancellationTokenSource cts)
    {
        foreach (var row in queue)
        {
            if (cts.IsCancellationRequested) return;

            var path = row.MeasuredPath;
            (long Bytes, int Files) measured;

            try
            {
                measured = await Task.Run(() => AppDataScan.Measure(path, cts.Token), cts.Token);
            }
            catch (Exception ex)
            {
                if (cts.IsCancellationRequested) return;
                AppLog.Exception("appdata", ex, "统计体积：" + path);
                continue;
            }

            if (cts.IsCancellationRequested) return;

            row.Bytes = measured.Bytes;
            row.Files = measured.Files;
            RefreshCounts();

            // 体积一到位这一行才「能清」（`CanClean` 要求统计完），所以动作条也要跟着重算 ——
            // 否则「全选」勾完、后台又统计出几项，按钮上的数字会一直停在勾选那一刻。
            // ⚠️ 只在可清理分类下才真干活（测量顺序是"两个概览在前、可清理在后"），开销可忽略。
            UpdateActionBar();
        }

        if (cts.IsCancellationRequested) return;

        // 统计完按体积重排一次 —— "谁在占地方"这个问题的答案本来就要按体积看
        _foldersLocal = _foldersLocal.OrderByDescending(r => r.Bytes).ToList();
        _foldersRoaming = _foldersRoaming.OrderByDescending(r => r.Bytes).ToList();
        _clean = _clean.OrderByDescending(r => r.Bytes).ToList();

        var biggest = _foldersLocal.FirstOrDefault();
        if (biggest is not null && biggest.Bytes > 0)
            AppLog.Info("appdata",
                $"体积统计完成；本地最大的一个一级目录是「{biggest.Name}」（{AppLog.Size(biggest.Bytes)}）");
    }

    // ── 分类 ──────────────────────────────────────────────────────────

    private void FillCategories()
    {
        var wanted = _category;

        var options = new List<CategoryOption>
        {
            new(true, AppDataZone.Local, AppDataKind.Temp, "\uE8B7", "概览 · 本地"),
            new(true, AppDataZone.Roaming, AppDataKind.Temp, "\uE8B7", "概览 · 漫游"),
        };

        foreach (var kind in AppDataLook.Order)
            options.Add(new(false, AppDataZone.Local, kind, AppDataLook.GlyphOf(kind),
                "可清理 · " + AppDataLook.LabelOf(kind)));

        // ⚠️ 选项内容在这里手建：`ComboBox.ItemTemplate` + `x:Bind` 会让 XamlCompiler 崩在 PageCodeGen
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

            var count = new TextBlock
            {
                FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center,
            };
            option.CountBlock = count;
            panel.Children.Add(count);

            CategoryBox.Items.Add(new ComboBoxItem { Content = panel, Tag = option });
        }

        var pick = options.FirstOrDefault(o => wanted is not null && o.Label == wanted.Label) ?? options[0];
        _category = pick;
        CategoryBox.SelectedIndex = options.IndexOf(pick);

        RefreshCounts();
    }

    /// <summary>刷新下拉里每一项的计数（统计中会变），顺带更新当前分类的计数文案。</summary>
    private void RefreshCounts()
    {
        foreach (var option in CategoryBox.Items.OfType<ComboBoxItem>()
                     .Select(i => i.Tag).OfType<CategoryOption>())
        {
            if (option.CountBlock is not null)
                option.CountBlock.Text = RowsOf(option).Count.ToString("N0");
        }

        UpdateCountText();
    }

    private void UpdateCountText()
    {
        if (!_ready || _category is null) return;

        var rows = RowsOf(_category);
        var label = _category.Label;
        var done = rows.Count(r => r.Bytes >= 0);

        if (_measuring && done < rows.Count)
        {
            CountLine.Text = $"{label} · {rows.Count} 项，正在统计体积（{done}/{rows.Count}）";
            return;
        }

        CountLine.Text = rows.Count == 0
            ? $"{label} · 没有东西"
            : $"{label} · {rows.Count} 项 · 合计 {AppLog.Size(rows.Sum(r => Math.Max(0, r.Bytes)))}";
    }

    private List<AppDataRow> RowsOf(CategoryOption option) =>
        option.IsOverview
            ? (option.Zone == AppDataZone.Local ? _foldersLocal : _foldersRoaming)
            : _clean.Where(r => r.Kind == option.Kind).ToList();

    private void Category_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryBox.SelectedItem is ComboBoxItem { Tag: CategoryOption option })
        {
            // 切分类就把勾清空：跨分类保留勾选只会让人记不清自己到底勾了哪一类。
            // ⚠️ 以前是靠切 `SelectionMode` 顺带清掉的，改用标准 CheckBox 后得显式清。
            ClearAllChecks();

            _category = option;
            ShowCategory();
        }
    }

    /// <summary>把当前分类的行摆上去。</summary>
    private void ShowCategory()
    {
        if (!_ready || _category is null) return;

        var rows = RowsOf(_category);
        var any = rows.Count > 0;

        // ⛔ 复选框只给可清理分类：只读体检那一层**没有删除入口**，行模板里按 `CheckVisibility` 隐藏。
        //    ⚠️ 勾选状态自 2026-10-16 起跟着**行对象**走（`AppDataRow.IsChecked`），
        //       切分类不会自动清 —— 由 `Category_Changed` 显式清空。
        EntryList.ItemsSource = rows;
        EntryList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;

        EmptyPanel.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (!any)
            EmptyText.Text = _category.IsOverview
                ? "这个根下没有可读的目录。"
                : $"「{_category.Label}」下没有找到可清理的缓存目录。";

        LoadingPanel.Visibility = Visibility.Collapsed;
        UpdateCountText();
        UpdateActionBar();
    }

    /// <summary>阶段 A 失败时的落地状态：收起转圈、给一句人话。</summary>
    private void ShowEmpty(string text)
    {
        _ready = false;
        LoadingPanel.Visibility = Visibility.Collapsed;
        EntryList.Visibility = Visibility.Collapsed;
        CleanBar.Visibility = Visibility.Collapsed;
        EmptyText.Text = text;
        EmptyPanel.Visibility = Visibility.Visible;
        CountLine.Text = "";
    }

    // ── 批量选择（底部动作条） ──────────────────────────────────────────
    //
    // ⚠️ 勾选状态**只有 `AppDataRow.IsChecked` 那一份**，这里不另存一份。
    //    自己再维护一个 `List<AppDataRow> _checked` 是这类界面最常见的错法 ——
    //    列表一重建、行一重排，两份状态就会分家，最后出现"界面上没勾、程序却记着"。

    /// <summary>批量改写勾选时把逐行的刷新压住：几百项全选时不至于刷几百次动作条。</summary>
    private bool _suppressCheckEvent;

    /// <summary>
    /// 某一行被勾上 / 取消。
    ///
    /// ⚠️⚠️ 这里**必须**把复选框当下的值显式写回行，不能只调 `UpdateActionBar()`。
    ///    原因：`Checked` / `Unchecked` 事件**先于** `x:Bind` 的 TwoWay 回写触发 ——
    ///    事件里读到的 `AppDataRow.IsChecked` 还是**上一拍**的值，计数就会永远慢一步。
    ///    2026-10-16 Nick 抓到的正是它：「我没勾起来，为什么还能清理？」
    ///    （勾上又取消之后，界面上没有勾、动作条却写着「已选 1 项」。）
    ///
    /// ⚠️ 用 `Click` 而不是 `Checked`/`Unchecked` 还有一层好处：`Click` 只在**用户交互**时触发，
    ///    代码里批量改 `IsChecked`（全选 / 清空选择）不会反过来打进这里。
    /// </summary>
    private void RowCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressCheckEvent) return;

        if (sender is CheckBox { DataContext: AppDataRow row } box)
            row.IsChecked = box.IsChecked == true;

        UpdateActionBar();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_category is null || _category.IsOverview) return;

        // 只勾**能清**的：体积还在统计的那些不进选择区，
        // 否则按钮上那个数字里会混着清不掉的项，点下去才发现少清了几个。
        _suppressCheckEvent = true;
        try
        {
            foreach (var row in RowsOf(_category)) row.IsChecked = row.CanClean;
        }
        finally
        {
            _suppressCheckEvent = false;
        }

        UpdateActionBar();
    }

    private void ClearSel_Click(object sender, RoutedEventArgs e) => ClearAllChecks();

    /// <summary>
    /// 把所有行上的勾清掉。
    /// ⛔ 清的是**全部行**，不是当前分类那一屏 —— 切分类与清理收尾都要用它，
    ///    跨分类留着勾只会让人记不清自己到底勾了哪一类。
    /// </summary>
    private void ClearAllChecks()
    {
        _suppressCheckEvent = true;
        try
        {
            foreach (var row in _foldersLocal.Concat(_foldersRoaming).Concat(_clean))
                row.IsChecked = false;
        }
        finally
        {
            _suppressCheckEvent = false;
        }

        UpdateActionBar();
    }

    private async void CleanSelected_Click(object sender, RoutedEventArgs e)
    {
        var chosen = ChosenRows();
        if (chosen.Count == 0) return;
        await CleanAsync(chosen);
    }

    /// <summary>当前勾选里**真能清**的那些（还在统计体积、正在清理的不算）。</summary>
    private List<AppDataRow> ChosenRows() =>
        _category is null
            ? new List<AppDataRow>()
            : RowsOf(_category).Where(r => r.IsChecked && r.CanClean).ToList();

    /// <summary>刷新底部动作条。⛔ 只读体检分类、空分类、没准备好时整条藏起来。</summary>
    private void UpdateActionBar()
    {
        var show = _ready
                   && _category is { IsOverview: false }
                   && RowsOf(_category).Count > 0;

        CleanBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        var chosen = ChosenRows();
        var size = chosen.Sum(r => Math.Max(0, r.Bytes));

        SelectAllButton.IsEnabled = !_busy;
        ClearSelButton.IsEnabled = !_busy && chosen.Count > 0;
        CleanSelectedButton.Content = chosen.Count > 0 ? $"清理选中 ({chosen.Count})" : "清理选中";
        CleanSelectedButton.IsEnabled = !_busy && chosen.Count > 0;

        CleanBarSummary.Text = chosen.Count == 0
            ? "勾选左侧复选框可一次处理多项。"
            : $"已选 {chosen.Count} 项" + (size > 0 ? $" · 合计约 {AppLog.Size(size)}" : "");
    }

    // ── 行尾菜单 ────────────────────────────────────────────────────────

    /// <summary>
    /// 行尾「⋯」—— 弹出这一行的操作。
    ///
    /// 顺序固定：**三项只读动作在前**（复制完整路径 / 打开文件夹 / 在网上搜索），
    /// 分隔线之后才是「清理」（只有白名单命中的行有）。
    /// 这样即使手快，落在最上面的也是不会改系统的那些。
    ///
    /// ⚠️ 菜单在这里**手建**，不把 `MenuFlyout` 写进 `DataTemplate`：Flyout 不在可视树里，
    ///    模板内的 `{x:Bind}` 求值上下文很脆，而 XamlCompiler 对这个组合本身就有前科
    ///    （本页踩过的 WMC9999 就是这一类）。手建时用闭包直接捕获这一行，最稳。
    /// </summary>
    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not AppDataRow row) return;

        var flyout = new MenuFlyout();

        var copy = new MenuFlyoutItem { Text = "复制完整路径", Icon = new FontIcon { Glyph = "\uE8C8" } };
        copy.Click += (_, _) => _ = CopyPathAsync(row);
        flyout.Items.Add(copy);

        // 「打开文件夹」对所有行都开放 —— 只读体检行尤其需要：
        // 光看体积排行回答不了「里面到底装了什么」。
        var open = new MenuFlyoutItem { Text = "打开文件夹", Icon = new FontIcon { Glyph = "\uE838" } };
        open.Click += (_, _) => _ = OpenFolderAsync(row);
        flyout.Items.Add(open);

        // 「搜索」同理：面对 Local 下七十个一级目录，最缺的就是"这名字到底是个啥"。
        // ⛔ 查的是**完整路径**，不是目录名 —— 详见 `SearchOnline` 的注释。
        var search = new MenuFlyoutItem
        {
            Text = "在网上搜索完整路径", Icon = new FontIcon { Glyph = "\uE721" },
        };
        search.Click += (_, _) => _ = SearchAsync(row);
        flyout.Items.Add(search);

        // 「清理」只有白名单命中的行才有（⛔ 只读项永远不出这一项）
        if (row.CanClean && row.Entry is not null)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());

            var clean = new MenuFlyoutItem { Text = "清理", Icon = new FontIcon { Glyph = "\uE74D" } };
            clean.Click += (_, _) => _ = CleanAsync(new[] { row });
            flyout.Items.Add(clean);
        }

        flyout.ShowAt(fe);
    }

    // ── 打开所在目录 / 复制路径 / 搜索（三个只读动作） ──────────────────

    /// <summary>在资源管理器里看一眼这个目录到底装了什么。**只读动作**。</summary>
    private async Task OpenFolderAsync(AppDataRow row)
    {
        if (AppDataScan.OpenInExplorer(row.MeasuredPath)) return;

        await InfoAsync("没能打开", $"「{row.Name}」这个目录可能已经不在了，点「重新扫描」刷新一下。");
    }

    private async Task CopyPathAsync(AppDataRow row)
    {
        if (CopyText(row.MeasuredPath)) return;

        await InfoAsync("没能复制", "写入剪贴板失败，详情见日志。");
    }

    private async Task SearchAsync(AppDataRow row)
    {
        if (SearchOnline(row.MeasuredPath)) return;

        await InfoAsync("没能打开浏览器",
            "本机可能没有可用的默认浏览器。可以手动复制路径后在搜索框里查：" + row.MeasuredPath);
    }

    /// <summary>复制到剪贴板。⚠️ 必须带 <c>Flush()</c> —— 不加这句，本程序一退出剪贴板里就空了。</summary>
    private static bool CopyText(string text)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Exception("appdata", ex, "复制路径：" + text);
            return false;
        }
    }

    /// <summary>
    /// 用**系统默认浏览器**去 Bing 搜这个目录。
    ///
    /// ⛔⛔ 查询词就是**完整路径本身，一个字都不加**（Nick 2026-10-15 定案）。
    ///     曾经拼过 `名称 + " AppData 文件夹"`，看着"更懂搜索"，实际是在**替用户概括** ——
    ///     概括就会失真：同一个名字挂在不同根下含义完全不同，而搜索结果页最擅长的恰好就是
    ///     由整条路径反查到具体软件。加了后缀反而把原始线索冲淡，还会带偏到泛泛的"AppData 是什么"。
    ///
    /// ⛔ 这里**不内置 WebView2**（Nick 提过这个方案）：本机唯一那个 WebView2 是只读 `WebSheet`，
    ///    再起一个实例要摊用户数据目录与生命周期，而这个动作本来就是"去互联网查资料" ——
    ///    交给真浏览器能开标签、能带登录态、能继续点进去看，也不用我们常驻一个内核
    ///    （本项目在内存上是激进派的：切页即收内存）。
    ///
    /// ⚠️ <c>UseShellExecute = true</c> 才会走 shell 的默认程序关联；不加这句会去 `PATH` 里
    ///    找一个叫 `https://...` 的文件。
    /// </summary>
    private static bool SearchOnline(string fullPath)
    {
        var url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(fullPath);

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Exception("appdata", ex, "打开浏览器搜索：" + fullPath);
            return false;
        }
    }

    // ── 清理 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 清理一批目录。
    ///
    /// ⛔ 行菜单里的「清理」和底部动作条的「清理选中」**走的是同一个方法** ——
    ///    两条入口各写一份的话，确认框规格、占用检测、结果统计迟早会分家。
    ///    单行只是"这一批只有一个"的特例。
    /// </summary>
    private async Task CleanAsync(IReadOnlyList<AppDataRow> rows)
    {
        if (_busy) return;

        var work = rows.Where(r => r.CanClean && r.Entry is not null).ToList();
        if (work.Count == 0) return;

        // 占用检测：这批涉及哪几类就问哪几类，别按行重复问
        var running = work.Select(r => r.Entry!.Kind)
                          .Distinct()
                          .SelectMany(AppDataScan.RunningApps)
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToList();

        var total = work.Sum(r => Math.Max(0, r.Bytes));
        var fileCount = work.Sum(r => Math.Max(0, r.Files));

        var dialog = new ContentDialog
        {
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
            XamlRoot = XamlRoot,
            Title = work.Count == 1 ? $"清理「{work[0].Name}」？" : $"清理选中的 {work.Count} 个目录？",
            Content = BuildConfirmBody(work, total, fileCount, running),
            PrimaryButtonText = "清理",
            CloseButtonText = "取消",
            // ⚠️ 默认落在「取消」：删的是别人的数据，别让一个回车就动手
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _busy = true;
        RescanButton.IsEnabled = false;
        CategoryBox.IsEnabled = false;
        UpdateActionBar();                       // 让三颗按钮一起变灰

        var okCount = 0;
        var failCount = 0;
        var skipped = 0;
        var freed = 0L;
        var notes = new List<string>();

        foreach (var row in work)
        {
            row.Busy = true;
            var before = Math.Max(0, row.Bytes);

            try
            {
                // ⚠️ **串行**跑：这是秒级动作，几条并行没有意义，
                //    而串行能保证同一父目录下的两条规则不会互相把对方刚重建的目录当成残留。
                var entry = row.Entry!;
                var outcome = await Task.Run(() => AppDataScan.Clean(entry, CancellationToken.None));

                // 清完重新量一次，把新体积如实显示出来（而不是假报 0 或者留着旧数）
                var (bytes, files) = await Task.Run(() => AppDataScan.Measure(row.MeasuredPath, CancellationToken.None));
                row.Bytes = bytes;
                row.Files = files;

                if (outcome.Ok)
                {
                    okCount++;
                    skipped += outcome.Skipped;
                    freed += before - bytes;
                    row.Status = outcome.Skipped > 0 ? $"已清空（跳过 {outcome.Skipped} 项）" : "已清空";
                }
                else
                {
                    failCount++;
                    row.Status = "";
                    notes.Add($"「{row.Name}」：{outcome.Message}");
                }
            }
            catch (Exception ex)
            {
                failCount++;
                row.Status = "";
                AppLog.Fail("appdata", ex, "清理：" + row.Name);
                notes.Add($"「{row.Name}」：遇到错误，详情见日志。");
            }
            finally
            {
                row.Busy = false;
            }
        }

        _busy = false;
        RescanButton.IsEnabled = true;
        CategoryBox.IsEnabled = true;

        // ⛔ 走 ShowCategory 而不是就地改：清完体积变小，计数行、下拉里的数字、动作条都要跟着重算。
        //    顺带把勾选清空 —— 已经清过的项留在勾选区里只会让人以为还能再清一次。
        ClearAllChecks();
        ShowCategory();
        RefreshCounts();

        await InfoAsync(failCount == 0 ? "清理完成" : "部分完成",
            BuildResultBody(okCount, failCount, freed, skipped, notes));
    }

    private static UIElement BuildConfirmBody(
        IReadOnlyList<AppDataRow> rows, long total, int fileCount, IReadOnlyList<string> running)
    {
        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(new TextBlock
        {
            Text = rows.Count == 1
                ? $"将清空这个目录的内容（{AppLog.Size(total)}，{fileCount:N0} 个文件），目录本身保留："
                : $"将清空以下 {rows.Count} 个目录的内容（合计 {AppLog.Size(total)}，{fileCount:N0} 个文件），"
                  + "目录本身保留：",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        });

        // 清单单独给一块小字底 —— 确认框的意义就是让人看清到底动的是哪儿。
        // 单项给**完整路径**；多项按「相对路径 + 体积」列，超过 10 行就滚起来（确认框不该比页面还长）。
        var lines = rows.Count == 1
            ? rows[0].MeasuredPath
            : string.Join("\n", rows.Take(10).Select(r => $"{r.Name}    {AppLog.Size(Math.Max(0, r.Bytes))}"))
              + (rows.Count > 10 ? $"\n…… 还有 {rows.Count - 10} 个" : "");

        var pathBlock = new TextBlock
        {
            Text = lines, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
        };
        var pathBorder = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Child = new ScrollViewer
            {
                MaxHeight = 190,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = pathBlock,
            },
        };
        var subtle = Res("CardBackgroundFillColorSecondaryBrush");
        if (subtle is not null) pathBorder.Background = subtle;
        panel.Children.Add(pathBorder);

        // ⚠️ 这条**必须**是醒目的警示色，不能是灰小字：永久删除，没有回收站兜底（Nick 2026-10-14 定案）
        var danger = new TextBlock
        {
            Text = "\uE7BA  内容是永久删除、不进回收站，删掉就找不回来了。\n"
                 + "以上都是缓存文件，程序下次要用会自己重新生成。不会碰配置和账号数据，目录本身也会保留。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        var cautionBrush = Res("SystemFillColorCautionBrush");
        if (cautionBrush is not null) danger.Foreground = cautionBrush;
        panel.Children.Add(danger);

        if (running.Count > 0)
        {
            var warn = new TextBlock
            {
                Text = $"检测到 {string.Join("、", running)} 正在运行 —— 先关掉它再清理会更彻底，"
                     + "否则被占用的文件会被跳过。",
                FontSize = 12, TextWrapping = TextWrapping.Wrap,
            };
            var caution = Res("SystemFillColorCautionBrush");
            if (caution is not null) warn.Foreground = caution;
            panel.Children.Add(warn);
        }

        return panel;
    }

    /// <summary>
    /// 清理结果的收尾说明。
    /// ⚠️ 讲的必须是**实际结果**，不是"我们打算做的事" —— 跳过几项、失败几项都要如实报。
    /// </summary>
    private static string BuildResultBody(
        int okCount, int failCount, long freed, int skipped, IReadOnlyList<string> notes)
    {
        var sb = new StringBuilder();

        sb.Append(okCount == 1
            ? $"已清理 1 个目录，释放约 {AppLog.Size(freed)}。"
            : $"已清理 {okCount} 个目录，合计释放约 {AppLog.Size(freed)}。");

        if (skipped > 0)
            sb.Append($"\n有 {skipped} 项正被程序占用，已跳过 —— 关掉相关程序后可以再清一次。");

        if (failCount > 0)
        {
            sb.Append($"\n有 {failCount} 个目录没能清掉：");
            foreach (var note in notes.Take(5)) sb.Append("\n· " + note);
            if (notes.Count > 5) sb.Append($"\n· …… 还有 {notes.Count - 5} 条");
        }

        sb.Append("\n这些是缓存文件，程序下次要用会自己重新生成。");
        return sb.ToString();
    }

    /// <summary>拿主题画刷；拿不到就返回 null（⛔ 别写死 RGB —— 深色浅色两套主题都要对）。</summary>
    private static Brush? Res(string key)
    {
        try
        {
            return Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;
        }
        catch (Exception ex)
        {
            AppLog.Debug("appdata", $"取主题画刷失败（{key}）：{ex.Message}");
            return null;
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

    // ── 说明 ──────────────────────────────────────────────────────────

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
            XamlRoot = XamlRoot,
            Title = "AppData 管理：能做什么、不能做什么",
            Content = BuildAboutBody(),
            CloseButtonText = "关闭",
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

        Item("只读体检",
             $"扫的是 {AppDataScan.LocalRoot} 和 {AppDataScan.RoamingRoot} 下面的一级目录，"
             + "只列体积排行。这一层没有清理入口 —— 它的用处是回答「到底是谁在占地方」。");

        Item("可清理的只有这几类",
             "系统临时文件、浏览器缓存、商店应用缓存、开发工具缓存、图形着色器缓存。"
             + "判据是「删了应用自己会重建，最坏也只是下次访问重新下载」。"
             + "够不上这个判据的目录，一律不会出现在清理列表里。");

        Item("删除是永久的",
             "清理不进回收站，是直接把文件删掉 —— 所以按下去之前请看清路径。"
             + "之所以敢这么删：这一层清的全是缓存，程序下次要用会自己重新生成。"
             + "目录本身会保留（是清空内容，不是删掉目录）。");

        Item("绝不碰的东西",
             "一级目录永远不会被整目录删除，所以有些目录看着挺大也不会出现在清理列表里 —— 这是故意的。"
             + "典型例子：Programs 是程序安装本体，Tencent / QQ 里是聊天记录，"
             + "*\\Settings 和 *.db 是配置与账号数据。这些东西删掉不可恢复，不属于缓存，不提供清理入口。");

        Item("为什么有些清不掉",
             "正被程序占用的文件删不动，会被跳过，并且如实告诉你跳过了几项。"
             + "关掉相关程序（比如浏览器）再清理，能清得更干净。");

        Item("批量选择",
             "「可清理」分类下每一行都带复选框，底部动作条可以一次处理多项。"
             + "「全选」只勾选体积已经统计完的项 —— 还在统计的暂时不能清，按钮上的数字里也就不含它们。"
             + "切换分类会清空勾选：跨分类保留勾选，只会让人记不清自己到底勾了哪一类。");

        Item("复制路径与搜索",
             "行尾「⋯」里可以复制完整路径，也可以调起系统默认浏览器在 Bing 上检索它。"
             + "检索词就是这一项的完整路径原样，不附加任何后缀 —— 由整条路径反查具体软件，"
             + "比只拿一段目录名去搜准得多。"
             + "两者都只读，不修改本机任何内容，任何一行都可以用。");

        Item("体积是怎么算的",
             "逐个文件累加得到，读不到的个别文件会跳过，所以数字可能略微偏小；"
             + "正被独占的文件统计不到，属于正常现象。");

        return panel;
    }
}
