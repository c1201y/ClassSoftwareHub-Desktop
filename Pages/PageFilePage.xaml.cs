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




















public sealed partial class PageFilePage : Page
{
    
    private List<PageFileDrive> _drives = new();

    
    private readonly Dictionary<string, PageFilePlan> _pending = new(StringComparer.Ordinal);

    
    private readonly Dictionary<string, TextBlock> _rowSizeText = new(StringComparer.Ordinal);

    
    private bool _autoCurrent;

    
    private bool _autoPending;

    
    private bool _autoChanged;

    
    private OsVolumeLock _osLockCurrent = OsVolumeLock.Unknown;

    
    private bool _osLockInitial;

    
    private bool _osLockPending;

    
    private bool _osLockChanged;

    private string? _selected;

    
    private bool _loading;

    
    private bool _syncing;

    private bool _busy;

    
    private bool _dialogOpen;

    
    private bool _tipDismissed;

    private TipMode _tipMode = TipMode.Pending;

    
    private const string OsLockKey = "oslock";

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

        
        if (IsLoaded) Reload();
    }

    

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

            
            _osLockCurrent = PageFileService.ReadOsVolumeLock();
            _osLockInitial = _osLockCurrent == OsVolumeLock.Locked;
            _osLockPending = _osLockInitial;
            _osLockChanged = false;

            SwOsLock.IsOn = _osLockPending;
            SwOsLock.IsEnabled = _osLockCurrent != OsVolumeLock.Unknown;

            EmptyHint.Visibility = _drives.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            SumFloor.Text = $"{PageFileRules.MinAllowedMb} MB";

            var allocated = _drives.Sum(d => d.FileBytes);
            SumAllocated.Text = allocated > 0 ? AppLog.Size(allocated) : "无";

            
            _selected = _drives.FirstOrDefault(d => d.Drive == PageFileService.SystemDrive)?.Drive
                        ?? _drives.FirstOrDefault()?.Drive;

            RefreshRows();
            UpdateEditability();

            AppLog.Step("pagefile", $"读取：自动管理={_autoCurrent}，驱动器 {_drives.Count} 个（"
                + string.Join("、", _drives.Select(d => $"{d.Drive}={d.Plan.SizeText}")) + "）"
                + $"，系统盘锁定={_osLockCurrent}");
        }
        finally
        {
            _loading = false;
        }

        UpdatePendingUi();
    }

    
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

    
    
    
    
    
    
    
    private void UpdateRowSize(string driveId)
    {
        if (!_rowSizeText.TryGetValue(driveId, out var text)) return;

        var drive = _drives.FirstOrDefault(d => d.Drive == driveId);
        if (drive is null) return;

        text.Text = (_pending.TryGetValue(driveId, out var plan) ? plan : drive.Plan).SizeText;
    }

    
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

    
    
    
    
    private static int SuggestMin(PageFileDrive drive) =>
        Math.Clamp(drive.UsableMb / 8, 512, 4096);

    private static int SuggestMax(PageFileDrive drive) =>
        Math.Clamp(drive.UsableMb / 4, 1024, 8192);

    

    private void Automatic_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _busy) return;

        _autoPending = SwAutomatic.IsOn;
        _autoChanged = _autoPending != _autoCurrent;

        
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

    
    
    
    private void OsLock_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _busy) return;

        _osLockPending = SwOsLock.IsOn;
        _osLockChanged = _osLockPending != _osLockInitial;

        AppLog.Step("pagefile", _osLockChanged
            ? $"排队：系统盘锁定 = {(_osLockPending ? "开" : "解除")}"
            : "系统盘锁定回到原状态");

        _tipDismissed = false;
        _tipMode = TipMode.Pending;
        UpdatePendingUi();
    }

    private void DriveList_SelectionChanged(object sender, SelectionChangedEventArgs e)    {
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

        
        if (double.IsNaN(args.NewValue)) return;

        QueueFromEditor();
    }

    private static bool IsBlank(NumberBox box) => box.Value is double v && double.IsNaN(v);

    private static int IntOf(NumberBox box) =>
        box.Value is double v && !double.IsNaN(v) ? (int)Math.Round(v) : 0;

    
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
            
            
            ShowShapeHint(error, isError: true);
            return;
        }

        if (PageFileRules.SameAs(new[] { plan }, new[] { drive.Plan })) _pending.Remove(drive.Drive);
        else _pending[drive.Drive] = plan;

        AppLog.Step("pagefile", _pending.ContainsKey(drive.Drive)
            ? $"排队：{drive.Drive} = {plan.SizeText}"
            : $"取消排队：{drive.Drive}（与当前配置一致）");

        ShowShapeHint(null, isError: false);

        
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
        
        
        DriveList.IsEnabled = !SwAutomatic.IsOn;
        UpdateEditorEnabled();
    }

    

    private int PendingCount =>
        (_autoChanged ? 1 : 0) + (_autoPending ? 0 : _pending.Count) + (_osLockChanged ? 1 : 0);

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

        
        PendingButton.Content = $"待应用 {count} 项";
        PendingButton.Visibility = ApplyTip.IsOpen ? Visibility.Collapsed : Visibility.Visible;
    }

    
    private void BuildPendingList()
    {
        PendingList.Items.Clear();

        if (_autoChanged)
        {
            AddPendingRow("自动管理所有驱动器的分页文件大小",
                _autoPending ? "开启" : "关闭",
                cancel: null);
        }

        
        
        
        if (_osLockChanged)
        {
            AddPendingRow("系统盘的分页文件锁定",
                _osLockPending ? "开启" : "解除",
                cancel: OsLockKey);
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
        if (sender is not Button { Tag: string key }) return;

        
        if (key == OsLockKey)
        {
            _loading = true;
            try { SwOsLock.IsOn = _osLockInitial; }
            finally { _loading = false; }

            _osLockPending = _osLockInitial;
            _osLockChanged = false;

            AppLog.Step("pagefile", "取消排队：系统盘锁定");
            UpdatePendingUi();
            return;
        }

        _pending.Remove(key);

        
        if (_selected == key) FillEditor(key);
        UpdateRowSize(key);

        AppLog.Step("pagefile", $"取消排队：{key}");
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
            
            AppLog.Exception("pagefile", ex, "应用更改");
            ShowNotice("应用失败：" + ex.Message, InfoBarSeverity.Error);
        }
    }

    

    
    
    
    
    
    
    
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

        
        var plansChanged = _autoChanged || _pending.Count > 0;

        
        if (plansChanged && !_autoPending)
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

        var summaryParts = new List<string>();

        if (plansChanged)
        {
            summaryParts.Add(_autoPending
                ? "改为由系统自动管理所有驱动器的分页文件大小。"
                : string.Join("\n", plans
                    .Where(p => p.Mode != PageFileMode.None)
                    .Select(p => $"{p.Drive}  {p.SizeText}")));
        }

        if (_osLockChanged)
            summaryParts.Add(_osLockPending ? "允许系统在系统盘保留分页文件。" : "解除系统盘的分页文件锁定。");

        var summary = string.Join("\n", summaryParts);

        if (!await ConfirmAsync("应用虚拟内存设置",
                $"{summary}\n\n需要一次管理员授权。改动在**重启之后**生效。"))
            return;

        _busy = true;
        ApplyTip.IsOpen = false;

        try
        {
            var result = await PageFileService.ApplyAsync(
                _autoPending, plans, plansChanged, _osLockChanged ? _osLockPending : null);

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
            
            
            
            _busy = false;
        }
    }

    

    private void HubAiEntry_Click(object sender, RoutedEventArgs e) => AiFlyout.Toggle();

    
    
    
    
    
    
    
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
            系统盘锁定 = _osLockCurrent switch
            {
                OsVolumeLock.Locked => "已锁定：分页文件只能留在系统盘",
                OsVolumeLock.Unlocked => "已解除",
                OsVolumeLock.Absent => "没有设置（等于不干预）",
                _ => "读不到",
            },
            目标系统盘锁定 = _osLockChanged ? _osLockPending : (bool?)null,
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
            系统盘锁定 = "系统盘锁定开着时，即使把每个盘的分页文件都设到别处，系统仍会在系统盘另建一份；" +
                        "用户说「设了不管用、C 盘还是有 pagefile」时先看这一项",
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
        
        NoticeBar.Visibility = Visibility.Visible;
        NoticeBar.Severity = severity;
        NoticeBar.Message = message;
        NoticeBar.IsOpen = true;
    }
}
