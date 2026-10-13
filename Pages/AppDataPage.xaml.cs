using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
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
/// 列表 / 表格里的一行。两种来源共用一个类型：
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
    private bool _table;        // 当前是不是表格视图
    private bool _measuring;    // 正在统计体积
    private bool _busy;         // 正在跑一次清理

    public AppDataPage()
    {
        InitializeComponent();

        // 两个视图叠着放，滚轮要能分别接力（同页互斥的两个滚动区都传进去）
        WheelRelay.Attach(this, EntryList, EntryTable);

        ViewChoice.SelectedIndex = 0;
        _table = false;

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
        EntryList.ItemsSource = null;
        EntryTable.ItemsSource = null;
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
            _category = option;
            ShowCategory();
        }
    }

    private void ViewChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewChoice.SelectedItem is RadioButton { Tag: string tag })
        {
            _table = tag == "table";
            ShowCategory();
        }
    }

    /// <summary>把当前分类的行摆上去（两个视图共用同一份结果）。</summary>
    private void ShowCategory()
    {
        if (!_ready || _category is null) return;

        var rows = RowsOf(_category);
        var any = rows.Count > 0;

        EntryList.ItemsSource = rows;
        EntryTable.ItemsSource = rows;

        EntryList.Visibility = !_table && any ? Visibility.Visible : Visibility.Collapsed;
        EntryTable.Visibility = _table && any ? Visibility.Visible : Visibility.Collapsed;
        TableHeader.Visibility = _table && any ? Visibility.Visible : Visibility.Collapsed;

        EmptyPanel.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (!any)
            EmptyText.Text = _category.IsOverview
                ? "这个根下没有可读的目录。"
                : $"「{_category.Label}」下没有找到可清理的缓存目录。";

        LoadingPanel.Visibility = Visibility.Collapsed;
        UpdateCountText();
    }

    /// <summary>阶段 A 失败时的落地状态：收起转圈、给一句人话。</summary>
    private void ShowEmpty(string text)
    {
        _ready = false;
        LoadingPanel.Visibility = Visibility.Collapsed;
        EntryList.Visibility = Visibility.Collapsed;
        EntryTable.Visibility = Visibility.Collapsed;
        TableHeader.Visibility = Visibility.Collapsed;
        EmptyText.Text = text;
        EmptyPanel.Visibility = Visibility.Visible;
        CountLine.Text = "";
    }

    // ── 打开所在目录 ────────────────────────────────────────────────────

    /// <summary>
    /// 「打开」：在资源管理器里看一眼这个目录到底装了什么。
    /// **只读动作**，对所有行都开放（只读体检行尤其需要 —— 光看体积排行回答不了「里面是什么」）。
    /// </summary>
    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not AppDataRow row) return;

        if (AppDataScan.OpenInExplorer(row.MeasuredPath)) return;

        await InfoAsync("没能打开", $"「{row.Name}」这个目录可能已经不在了，点「重新扫描」刷新一下。");
    }

    // ── 清理 ──────────────────────────────────────────────────────────

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not AppDataRow row) return;
        if (!row.CanClean || row.Entry is null) return;

        var running = AppDataScan.RunningApps(row.Entry.Kind);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"清理「{row.Name}」？",
            Content = BuildConfirmBody(row, running),
            PrimaryButtonText = "清理",
            CloseButtonText = "取消",
            // ⚠️ 默认落在「取消」：删的是别人的数据，别让一个回车就动手
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _busy = true;
        row.Busy = true;
        RescanButton.IsEnabled = false;
        CategoryBox.IsEnabled = false;

        var before = Math.Max(0, row.Bytes);

        try
        {
            var entry = row.Entry;
            var outcome = await Task.Run(() => AppDataScan.Clean(entry, CancellationToken.None));

            // 清完重新量一次，把新体积如实显示出来（而不是假报 0 或者留着旧数）
            var (bytes, files) = await Task.Run(() => AppDataScan.Measure(row.MeasuredPath, CancellationToken.None));
            row.Bytes = bytes;
            row.Files = files;

            if (outcome.Ok)
            {
                row.Status = outcome.Skipped > 0 ? $"已清空（跳过 {outcome.Skipped} 项）" : "已清空";
                ShowCategory();
                await InfoAsync("清理完成",
                    $"「{row.Name}」已清空，释放约 {AppLog.Size(before - bytes)}。\n"
                    + "这些是缓存文件，程序下次要用会自己重新生成。");
            }
            else
            {
                row.Status = "";
                await InfoAsync("没能清掉", outcome.Message.Length > 0
                    ? outcome.Message
                    : "有文件正被程序占用。先关掉相关程序，再点「重新扫描」后重试。");
            }
        }
        catch (Exception ex)
        {
            AppLog.Fail("appdata", ex, "清理：" + row.Name);
            await InfoAsync("清理出错", "详情见日志。");
        }
        finally
        {
            row.Busy = false;
            _busy = false;
            RescanButton.IsEnabled = true;
            CategoryBox.IsEnabled = true;
            RefreshCounts();
        }
    }

    private static UIElement BuildConfirmBody(AppDataRow row, IReadOnlyList<string> running)
    {
        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(new TextBlock
        {
            Text = $"将清空这个目录的内容（{AppLog.Size(Math.Max(0, row.Bytes))}，{row.Files:N0} 个文件），"
                 + "目录本身保留：",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        });

        // 完整路径单独给一块小字底 —— 确认框的意义就是让人看清到底动的是哪儿
        var pathBlock = new TextBlock
        {
            Text = row.MeasuredPath, FontSize = 12, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"),
        };
        var pathBorder = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Child = pathBlock,
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
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = body, FontSize = 13, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "知道了",
        };
        await dialog.ShowAsync();
    }

    // ── 说明 ──────────────────────────────────────────────────────────

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "AppData 管理：能做什么、不能做什么",
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

        Item("体积是怎么算的",
             "逐个文件累加得到，读不到的个别文件会跳过，所以数字可能略微偏小；"
             + "正被独占的文件统计不到，属于正常现象。");

        return panel;
    }
}
