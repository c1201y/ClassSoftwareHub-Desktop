using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 虚拟内存 / 分页文件（需求清单第 19 条，2026-10-31）。
///
/// <para>
/// 搬的是系统「虚拟内存」对话框，换成 WinUI 3 的排法：上面选驱动器，下面给这一个驱动器
/// 三选一（自定义 / 系统管理 / 无）。改动只进队列，点提示条的主按钮时**一次授权**写完。
/// </para>
///
/// <para>
/// ⛔ 状态**只从服务层读**，页面不缓存：这些配置可能刚在系统属性里改过。
/// 回填必须压在 <c>_loading</c> 里 —— <c>Toggled</c> / <c>Checked</c> / <c>ValueChanged</c>
/// 在程序赋值时同样会触发，不压住就会把"回填"当成"用户改了"。
/// </para>
///
/// <para>
/// ⚠️ 这一页的改动**必须重启才生效**（系统在启动阶段按配置创建或删除 pagefile.sys），
/// 所以页面上有一条常驻说明，应用成功后的提示也要再讲一次。
/// </para>
/// </summary>
public sealed partial class PageFilePage : Page
{
    /// <summary>本机的固定磁盘（含当前的与待应用的目标值）。</summary>
    private List<PageFileDrive> _drives = new();

    /// <summary>待应用的改动：盘符 → 目标配置。</summary>
    private readonly Dictionary<string, PageFilePlan> _pending = new(StringComparer.Ordinal);

    /// <summary>列表每行右列那个「分页文件大小」文本框，改值时只动它、不重建列表。</summary>
    private readonly Dictionary<string, TextBlock> _rowSizeText = new(StringComparer.Ordinal);

    /// <summary>当前注册表里是不是「自动管理」。</summary>
    private bool _autoCurrent;

    /// <summary>应用之后打算变成「自动管理」还是手动。</summary>
    private bool _autoPending;

    /// <summary>自动管理这一档有没有被改过（它本身也算一项待应用）。</summary>
    private bool _autoChanged;

    private string? _selected;

    /// <summary>正在回填界面，此时的控件事件是程序触发的。</summary>
    private bool _loading;

    /// <summary>正在重建列表并恢复选中，此时的 SelectionChanged 是程序触发的。</summary>
    private bool _syncing;

    private bool _busy;

    /// <summary>确认框是否已经弹着 —— 同一时刻只允许一个 ContentDialog，第二个会抛异常。</summary>
    private bool _dialogOpen;

    /// <summary>用户点过「暂不应用」；之后再改才重新弹。</summary>
    private bool _tipDismissed;

    private TipMode _tipMode = TipMode.Pending;

    private enum TipMode
    {
        Pending,
        Failed,
    }

    public PageFilePage()
    {
        InitializeComponent();

        Loaded += (_, _) => Reload();

        HubAiEntry.Visibility = App.Settings.Current.HubAiEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;

        AiFlyout.ContextProvider = BuildAiContext;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 这一页 NavigationCacheMode=Disabled，每次进来都是新实例，重读一次最省事
        if (IsLoaded) Reload();
    }

    // ── 读取 ─────────────────────────────────────────────────────────────

    private void Reload()
    {
        _loading = true;
        try
        {
            _drives = PageFileService.Drives();
            _autoCurrent = PageFileService.IsAutomatic();
            _autoPending = _autoCurrent;
            _autoChanged = false;
            _pending.Clear();

            SwAutomatic.IsOn = _autoCurrent;

            EmptyHint.Visibility = _drives.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            SumFloor.Text = $"{PageFileRules.MinAllowedMb} MB";

            var allocated = _drives.Sum(d => d.FileBytes);
            SumAllocated.Text = allocated > 0 ? AppLog.Size(allocated) : "无";

            // 默认选中系统盘：用户十有八九是来改它的
            _selected = _drives.FirstOrDefault(d => d.Drive == PageFileService.SystemDrive)?.Drive
                        ?? _drives.FirstOrDefault()?.Drive;

            RefreshRows();
            UpdateEditability();

            AppLog.Step("pagefile", $"读取：自动管理={_autoCurrent}，驱动器 {_drives.Count} 个（"
                + string.Join("、", _drives.Select(d => $"{d.Drive}={d.Plan.SizeText}")) + "）");
        }
        finally
        {
            _loading = false;
        }

        UpdatePendingUi();
    }

    /// <summary>重建列表并恢复选中，然后回填右侧选项区。</summary>
    private void RefreshRows()
    {
        BuildDriveRows();

        _syncing = true;
        try
        {
            var index = _selected is null ? -1 : _drives.FindIndex(d => d.Drive == _selected);
            DriveList.SelectedIndex = index;
        }
        finally
        {
            _syncing = false;
        }

        FillEditor(_selected);
    }

    /// <summary>
    /// 每行是一个 Grid（盘符与卷标 / 可用空间 / 目标大小）。
    /// ⚠️ 行在代码里构造：⛔ 不用 <c>ItemTemplate + x:Bind</c>，这套组合会崩（见 MustRead §3）。
    /// </summary>
    private void BuildDriveRows()
    {
        DriveList.Items.Clear();
        _rowSizeText.Clear();

        foreach (var drive in _drives)
        {
            var row = new Grid { ColumnSpacing = 16, Margin = new Thickness(0, 6, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

            var name = new TextBlock
            {
                Text = drive.Display,
                FontSize = 13.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var free = new TextBlock
            {
                Text = drive.FreeText,
                FontSize = 12,
                Opacity = 0.65,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(free, 1);

            // 右列显示**目标**大小（有待应用改动时就是改动后的），而不是当前的
            var target = _pending.TryGetValue(drive.Drive, out var plan) ? plan : drive.Plan;

            var size = new TextBlock
            {
                Text = target.SizeText,
                FontSize = 13,
                TextAlignment = TextAlignment.Right,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(size, 2);

            row.Children.Add(name);
            row.Children.Add(free);
            row.Children.Add(size);

            row.Tag = drive.Drive;
            DriveList.Items.Add(row);

            _rowSizeText[drive.Drive] = size;
        }
    }

    /// <summary>
    /// 只把某一行的右列文字换掉。
    /// <para>
    /// ⚠️ 不能在改数字时重建整张列表：重建会连带 <see cref="FillEditor"/> 把数字框重新赋值，
    /// 用户正在输入的光标位置会被重置（打「14336」打到一半跳回去）。所以这时只改一个 <c>TextBlock</c>。
    /// </para>
    /// </summary>
    private void UpdateRowSize(string driveId)
    {
        if (!_rowSizeText.TryGetValue(driveId, out var text)) return;

        var drive = _drives.FirstOrDefault(d => d.Drive == driveId);
        if (drive is null) return;

        text.Text = (_pending.TryGetValue(driveId, out var plan) ? plan : drive.Plan).SizeText;
    }

    /// <summary>把某个驱动器的目标配置回填到选项区。</summary>
    private void FillEditor(string? driveId)
    {
        var drive = _drives.FirstOrDefault(d => d.Drive == driveId);

        if (drive is null)
        {
            SelTitle.Text = "未选择驱动器";
            SelSub.Text = "";
            UpdateEditorEnabled();
            return;
        }

        var plan = _pending.TryGetValue(drive.Drive, out var target) ? target : drive.Plan;

        _loading = true;
        try
        {
            SelTitle.Text = $"所选驱动器：{drive.Display}";

            var parts = new List<string> { drive.FreeText, $"共 {drive.TotalMb:N0} MB" };
            if (drive.FileText.Length > 0) parts.Add($"当前文件 {drive.FileText}");
            SelSub.Text = string.Join(" · ", parts);

            RbCustom.IsChecked = plan.Mode == PageFileMode.Custom;
            RbSystem.IsChecked = plan.Mode == PageFileMode.SystemManaged;
            RbNone.IsChecked = plan.Mode == PageFileMode.None;

            // 切到「自定义」时给一对有依据的初值：已有配置就沿用，没有就按可用空间取一档
            NumMin.Value = plan.Mode == PageFileMode.Custom ? plan.MinMb : SuggestMin(drive);
            NumMax.Value = plan.Mode == PageFileMode.Custom ? plan.MaxMb : SuggestMax(drive);
        }
        finally
        {
            _loading = false;
        }

        UpdateEditorEnabled();
        ShowShapeHint(null, isError: false);
    }

    /// <summary>
    /// 选项区能不能改。
    /// <para>
    /// ⚠️ 逐个控件设 <c>IsEnabled</c>，不是给容器设 —— WinUI 的 <c>StackPanel</c> 派生自
    /// <c>Panel</c>、不是 <c>Control</c>，**根本没有 <c>IsEnabled</c>**。
    /// </para>
    /// <para>
    /// 数字框另有一条：只有选中「自定义大小」时才填得了，与系统那个对话框一致。
    /// </para>
    /// </summary>
    private void UpdateEditorEnabled()
    {
        var editable = !SwAutomatic.IsOn && _drives.Count > 0;

        RbCustom.IsEnabled = editable;
        RbSystem.IsEnabled = editable;
        RbNone.IsEnabled = editable;

        var custom = editable && RbCustom.IsChecked == true;
        NumMin.IsEnabled = custom;
        NumMax.IsEnabled = custom;
    }

    /// <summary>
    /// 没有现成配置时的初始大小。取可用空间的 1/8，卡在 512 ~ 4096 之间 ——
    /// 只是个起点，用户可以随手改，⛔ 不是"推荐值"（推荐值怎么算各家说法不一，不在这里编）。
    /// </summary>
    private static int SuggestMin(PageFileDrive drive) =>
        Math.Clamp(drive.UsableMb / 8, 512, 4096);

    private static int SuggestMax(PageFileDrive drive) =>
        Math.Clamp(drive.UsableMb / 4, 1024, 8192);

    // ── 交互 ─────────────────────────────────────────────────────────────

    private void Automatic_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _busy) return;

        _autoPending = SwAutomatic.IsOn;
        _autoChanged = _autoPending != _autoCurrent;

        // 自动管理由系统全权决定各盘，逐盘的手动改动不再有意义
        if (_autoPending) _pending.Clear();

        UpdateEditability();
        RefreshRows();

        AppLog.Step("pagefile", _autoChanged
            ? $"排队：自动管理 = {(_autoPending ? "开" : "关")}"
            : "自动管理回到原状态");

        _tipDismissed = false;
        _tipMode = TipMode.Pending;
        UpdatePendingUi();
    }

    private void DriveList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;

        if (DriveList.SelectedItem is FrameworkElement { Tag: string drive })
        {
            _selected = drive;
            FillEditor(drive);
        }
        else
        {
            FillEditor(null);
        }
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || _busy) return;

        // 勾到「自定义」而数字框还是空的：先把建议值填上，否则会立刻报"不能小于 16"
        var drive = _drives.FirstOrDefault(d => d.Drive == _selected);
        if (drive is not null && RbCustom.IsChecked == true && IsBlank(NumMin))
        {
            _loading = true;
            NumMin.Value = SuggestMin(drive);
            NumMax.Value = SuggestMax(drive);
            _loading = false;
        }

        UpdateEditorEnabled();
        QueueFromEditor();
    }

    private void Number_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || _busy) return;

        // 清空数字框时 Value 是 NaN，此时不要拿 0 去覆盖用户正在输入的内容
        if (double.IsNaN(args.NewValue)) return;

        QueueFromEditor();
    }

    private static bool IsBlank(NumberBox box) => box.Value is double v && double.IsNaN(v);

    private static int IntOf(NumberBox box) =>
        box.Value is double v && !double.IsNaN(v) ? (int)Math.Round(v) : 0;

    /// <summary>把选项区当前的选择写成一条目标配置（校验不过就只提示、不入队）。</summary>
    private void QueueFromEditor()
    {
        var drive = _drives.FirstOrDefault(d => d.Drive == _selected);
        if (drive is null) return;

        var mode = RbNone.IsChecked == true ? PageFileMode.None
                 : RbSystem.IsChecked == true ? PageFileMode.SystemManaged
                 : PageFileMode.Custom;

        var plan = new PageFilePlan(drive.Drive, mode, IntOf(NumMin), IntOf(NumMax));

        var error = PageFileRules.Validate(plan, drive.UsableMb);
        if (error is not null)
        {
            // ⚠️ 校验不过**不排队**，但要把原因当场显示出来 —— 否则用户以为设好了，
            //    等重启之后才发现还是原来的配置。
            ShowShapeHint(error, isError: true);
            return;
        }

        if (PageFileRules.SameAs(new[] { plan }, new[] { drive.Plan })) _pending.Remove(drive.Drive);
        else _pending[drive.Drive] = plan;

        AppLog.Step("pagefile", _pending.ContainsKey(drive.Drive)
            ? $"排队：{drive.Drive} = {plan.SizeText}"
            : $"取消排队：{drive.Drive}（与当前配置一致）");

        ShowShapeHint(null, isError: false);

        // 只换这一行的右列文字，不重建列表（见 UpdateRowSize 的注释）
        UpdateRowSize(drive.Drive);

        _tipDismissed = false;
        _tipMode = TipMode.Pending;
        UpdatePendingUi();
    }

    private void ShowShapeHint(string? text, bool isError)
    {
        if (string.IsNullOrEmpty(text))
        {
            ShapeHint.Visibility = Visibility.Collapsed;
            return;
        }

        ShapeHint.Text = text;
        ShapeHint.Visibility = Visibility.Visible;
        ShapeHint.Foreground = isError
            ? ThemeBrush.Get(this, "SystemFillColorCriticalBrush")
            : ThemeBrush.Get(this, "TextFillColorSecondaryBrush");
    }

    private void UpdateEditability()
    {
        // 自动管理开着时，逐盘设置由系统接管：控件走自带的禁用视觉，
        // ⛔ 不自己调 Opacity 造灰（见 MustRead §1.0）
        DriveList.IsEnabled = !SwAutomatic.IsOn;
        UpdateEditorEnabled();
    }

    // ── 待应用与提示条 ───────────────────────────────────────────────────

    private int PendingCount => (_autoChanged ? 1 : 0) + (_autoPending ? 0 : _pending.Count);

    private void UpdatePendingUi()
    {
        var count = PendingCount;

        if (count == 0)
        {
            ApplyTip.IsOpen = false;
            _tipMode = TipMode.Pending;
            _tipDismissed = false;
            PendingButton.Visibility = Visibility.Collapsed;
            return;
        }

        BuildPendingList();

        if (_tipMode == TipMode.Pending) ShowTipPending();

        // 两个入口不同时出现：提示开着就不显示顶部按钮
        PendingButton.Content = $"待应用 {count} 项";
        PendingButton.Visibility = ApplyTip.IsOpen ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>清单：每行「对象 → 目标值」+ 一颗「取消」。</summary>
    private void BuildPendingList()
    {
        PendingList.Items.Clear();

        if (_autoChanged)
        {
            AddPendingRow("自动管理所有驱动器的分页文件大小",
                _autoPending ? "开启" : "关闭",
                cancel: null);
        }

        if (_autoPending) return;

        foreach (var (drive, plan) in _pending)
            AddPendingRow(drive, plan.SizeText, cancel: drive);
    }

    private void AddPendingRow(string title, string value, string? cancel)
    {
        var row = new Grid { ColumnSpacing = 16, Margin = new Thickness(0, 7, 0, 7) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
        };
        text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = title });
        text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            Text = "  →  " + value,
            Foreground = ThemeBrush.Get(this, "TextFillColorSecondaryBrush"),
        });

        row.Children.Add(text);

        if (cancel is not null)
        {
            var button = new Button
            {
                Content = "取消",
                Tag = cancel,
                MinWidth = 0,
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            button.Click += CancelPending_Click;

            Grid.SetColumn(button, 1);
            row.Children.Add(button);
        }

        PendingList.Items.Add(row);
    }

    private void CancelPending_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string driveId }) return;

        _pending.Remove(driveId);

        // 选项区要拨回真实配置，否则它停在一个"看着改了、其实不会写入"的位置
        if (_selected == driveId) FillEditor(driveId);
        UpdateRowSize(driveId);

        AppLog.Step("pagefile", $"取消排队：{driveId}");
        UpdatePendingUi();
    }

    private void ShowTipPending()
    {
        ApplyTip.Title = $"有 {PendingCount} 项待应用";
        ApplyTip.Subtitle = _autoPending
            ? "应用后需重启生效。"
            : "应用时会请求一次管理员授权，重启后生效。";
        ApplyTip.ActionButtonContent = "应用更改";
        ApplyTip.CloseButtonContent = "暂不应用";

        ApplyTipMaxWidth(XamlRoot?.Size.Width ?? 1024);

        if (!_tipDismissed && !ApplyTip.IsOpen) ApplyTip.IsOpen = true;
    }

    private void ShowTipFailed()
    {
        ApplyTip.Title = "改动没有生效";
        ApplyTip.Subtitle = "可能被组策略或安全软件限定，可以再试一次。";
        ApplyTip.ActionButtonContent = "重试";
        ApplyTip.CloseButtonContent = "关闭";

        ApplyTipMaxWidth(XamlRoot?.Size.Width ?? 1024);
        ApplyTip.IsOpen = true;
    }

    /// <summary>
    /// 提示宽度上限按窗口算，不写死 —— 写死一个比内容区大的值会把右侧「取消」推出可视区
    /// （2026-10-31 在系统优化页连错两次的教训）。
    /// </summary>
    private void ApplyTipMaxWidth(double windowWidth) =>
        ApplyTip.MaxWidth = Math.Clamp(windowWidth - 120, 320, 520);

    private void ApplyTip_Closed(TeachingTip sender, object args)
    {
        PendingButton.Content = $"待应用 {PendingCount} 项";
        PendingButton.Visibility = PendingCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PendingButton_Click(object sender, RoutedEventArgs e)
    {
        _tipDismissed = false;
        _tipMode = TipMode.Pending;
        UpdatePendingUi();
    }

    private void ApplyTip_CloseClick(TeachingTip sender, object args)
    {
        // 只是收起来，队列原样保留
        _tipDismissed = true;
        PendingButton.Content = $"待应用 {PendingCount} 项";
        PendingButton.Visibility = PendingCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ApplyTip_ActionClick(TeachingTip sender, object args)
    {
        try
        {
            await ApplyPendingAsync();
        }
        catch (Exception ex)
        {
            // async void 里漏出去的异常会直接终止进程 —— 兜到底
            AppLog.Exception("pagefile", ex, "应用更改");
            ShowNotice("应用失败：" + ex.Message, InfoBarSeverity.Error);
        }
    }

    // ── 应用 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 提交时用的完整配置：界面上每个盘的当前选择，**加上界面上看不到的盘**。
    /// <para>
    /// ⚠️ 后半句是关键：<c>PagingFiles</c> 是整体覆盖写的。如果某台机器配了一个当前没挂上的盘
    /// （U 盘位、没分配盘符的卷），不带回去就等于替用户删掉了那一行配置。
    /// </para>
    /// </summary>
    private List<PageFilePlan> BuildTargetPlans()
    {
        var plans = _drives
            .Select(d => _pending.TryGetValue(d.Drive, out var plan) ? plan : d.Plan)
            .ToList();

        plans.AddRange(PageFileService.OrphanPlans());

        return plans;
    }

    private async Task ApplyPendingAsync()
    {
        if (_busy || PendingCount == 0) return;
        if (_dialogOpen) return;

        var plans = BuildTargetPlans();

        // 与服务层同一套校验：越界的大小写进去之后，Windows 不会报错，只是这个盘实际没有分页文件
        if (!_autoPending)
        {
            if (PageFileRules.ValidateAll(plans) is { } none)
            {
                ShowNotice(none, InfoBarSeverity.Warning);
                return;
            }

            foreach (var plan in plans)
            {
                var free = _drives.FirstOrDefault(d => d.Drive == plan.Drive)?.UsableMb ?? 0;
                if (PageFileRules.Validate(plan, free) is { } error)
                {
                    ShowNotice($"{plan.Drive} {error}", InfoBarSeverity.Warning);
                    return;
                }
            }
        }

        var summary = _autoPending
            ? "改为由系统自动管理所有驱动器的分页文件大小。"
            : string.Join("\n", plans
                .Where(p => p.Mode != PageFileMode.None)
                .Select(p => $"{p.Drive}  {p.SizeText}"));

        if (!await ConfirmAsync("应用虚拟内存设置",
                $"{summary}\n\n需要一次管理员授权。改动在**重启之后**生效。"))
            return;

        _busy = true;
        ApplyTip.IsOpen = false;

        try
        {
            var result = await PageFileService.ApplyAsync(_autoPending, plans);

            if (!result.Ok)
            {
                ShowNotice(result.Error ?? "应用失败。", InfoBarSeverity.Warning);
                _tipMode = TipMode.Failed;
                UpdatePendingUi();
                return;
            }

            _pending.Clear();
            _autoCurrent = _autoPending;
            _autoChanged = false;

            Reload();
            ShowNotice("已应用，重启后生效。", InfoBarSeverity.Success);
        }
        finally
        {
            // ⚠️ 这里**只**放开忙碌标记，不重读。
            //    重读会把 _pending 清空 —— 而失败分支特意保留了队列好让用户直接重试，
            //    在 finally 里一读就白保留了（2026-10-31 自检发现）。
            _busy = false;
        }
    }

    // ── Hub AI ───────────────────────────────────────────────────────────

    private void HubAiEntry_Click(object sender, RoutedEventArgs e) => AiFlyout.Toggle();

    /// <summary>
    /// 给浮层准备这一页的现场数据。
    /// <para>
    /// 分页文件是少数「改错了不会立刻报错、但会以别的症状出现」的设置，所以把每个盘的
    /// 可用空间、当前配置、待应用目标都摆给模型 —— 它才能答"这盘够不够""我该设多大"。
    /// </para>
    /// </summary>
    private PageContextDoc BuildAiContext()
    {
        var doc = new PageContextDoc(
            "虚拟内存",
            "这一页管理的是分页文件（虚拟内存），配置存在 HKLM，需要管理员授权，**重启后才生效**。" +
            "回答时按下面的实际数据说话，直接点名是哪个盘；不要给与本页无关的建议。" +
            "⚠️ 你无法替用户改设置，也不要说「已经帮你改好了」—— 改动只能由用户自己选驱动器、" +
            "改选项、再点右下角提示条的主按钮完成。");

        doc.Add("状态", new
        {
            自动管理 = _autoCurrent,
            目标自动管理 = _autoChanged ? _autoPending : (bool?)null,
            待应用项数 = PendingCount,
            正在应用 = _busy,
            允许的最小值MB = PageFileRules.MinAllowedMb,
            当前已分配 = PageFileService.AllocatedBytes() is var b && b > 0 ? AppLog.Size(b) : "无",
        });

        doc.Add("驱动器", _drives.Select(d => new
        {
            d.Drive,
            卷标 = d.Label,
            可用MB = d.FreeMb,
            可用于分页文件MB = d.UsableMb,
            总容量MB = d.TotalMb,
            当前设置 = d.Plan.SizeText,
            当前初始大小MB = d.Plan.MinMb,
            当前最大值MB = d.Plan.MaxMb,
            当前文件大小 = d.FileText,
            待应用目标 = _pending.TryGetValue(d.Drive, out var t) ? t.SizeText : null,
        }).ToList());

        doc.Add("界面上看不到但配置里存在的驱动器",
            PageFileService.OrphanPlans().Select(p => new { p.Drive, 设置 = p.SizeText }).ToList());

        doc.Add("本页的硬规则", new
        {
            大小下限 = "16 MB",
            上限 = "不能超过该盘可用空间，否则分页文件建不出来，而系统不会报错",
            至少一个 = "不允许所有驱动器都设为「无分页文件」",
            生效时机 = "重启",
            三种设置的含义 = new
            {
                自定义大小 = "固定占用，初始值立即分配、最大值为上限",
                系统管理的大小 = "由系统按需要伸缩，注册表里写作 0 0",
                无分页文件 = "这个盘不放分页文件（注册表里整条不写，不是写 0）",
            },
        });

        doc.Add("界面怎么操作", new
        {
            选驱动器 = "点列表里的一行",
            改这一盘的设置 = "在下方三选一，选「自定义大小」时填初始与最大",
            应用 = "右下角提示条的主按钮，一次授权",
            单项取消 = "提示条清单里每行右侧的「取消」",
        });

        return doc;
    }

    // ── 界面小工具 ───────────────────────────────────────────────────────

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        if (_dialogOpen) return false;

        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "继续",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            AppLog.Warn("pagefile", "确认框没能弹出：" + ex.Message);
            return false;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        // NoticeBar 平时 Collapsed（避免那条 8px 外边距在页头下面撑出空档），用之前先放出来
        NoticeBar.Visibility = Visibility.Visible;
        NoticeBar.Severity = severity;
        NoticeBar.Message = message;
        NoticeBar.IsOpen = true;
    }
}
