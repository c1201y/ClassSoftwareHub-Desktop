using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data.Tweaks;
using ClassSoftwareHub.Desktop.Services;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassSoftwareHub.Desktop.Pages;































public sealed partial class SystemTweaksPage : Page
{
    
    private readonly Dictionary<string, int> _pending = new(StringComparer.Ordinal);

    
    private bool _loading;

    private bool _busy;

    
    private bool _dialogOpen;

    
    
    
    
    private readonly Dictionary<SettingsCard, object?> _originalDescription = new();

    
    private TipMode _tipMode = TipMode.Pending;

    
    
    
    
    
    private bool _tipDismissed;

    private enum TipMode
    {
        Pending,
        Applied,
        Failed,
    }

    public SystemTweaksPage()
    {
        InitializeComponent();

        
        
        
        Loaded += OnLoaded;

        
        HubAiEntry.Visibility = App.Settings.Current.HubAiEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;

        
        AiFlyout.ContextProvider = BuildAiContext;
    }

    

    
    private void HubAiEntry_Click(object sender, RoutedEventArgs e) => AiFlyout.Toggle();

    
    
    
    
    
    
    
    
    
    
    
    
    
    private PageContextDoc BuildAiContext()
    {
        var build = SystemInfo.OsBuild();
        var all = TweakCatalog.All;

        var on = all.Where(TweakStore.IsOn).ToList();
        var unavailable = all.Where(t => !TweakRules.IsApplicable(t, build)).ToList();

        var doc = new PageContextDoc(
            "系统优化",
            "这一页把当前用户（HKCU）的界面与使用习惯设置收成一份清单，写入不需要管理员权限、不弹 UAC。" +
            "提问的人通常想知道：这一项具体改了什么、开了会不会影响上课 / 投影 / 打印、为什么拨了没反应。" +
            "回答时以「全部条目」里的项名、分组、注册表路径与当前值为准，直接点名是哪一项、在哪一组；" +
            "不要给「清理磁盘」「更新驱动」这类与本页无关的泛泛建议。" +
            "另有一条硬约束：你无法替用户改设置 —— 写入只能由他自己拨开关、再点右下角提示条的主按钮完成，" +
            "所以不要说你「已经帮你打开了」，要告诉他去点哪一项。" +
            "说话用大白话：UAC、CLSID 这类术语只供你自己判断，用户很可能不知道它们是什么；" +
            "被问到「为什么没有某项」时，用一句「会动到系统安全与系统服务，这一页不提供」说清即可，不要罗列术语。");

        doc.Add("状态", new
        {
            系统build = build,
            系统版本 = build >= TweakCatalog.Win11Build ? "Windows 11" : "Windows 10",
            本页条目总数 = all.Count,
            已启用的项数 = on.Count,
            本机不适用的项数 = unavailable.Count,
            待应用项数 = _pending.Count,
            正在应用 = _busy,
        });

        
        doc.Add("当前已启用", on.Select(t => new { t.Id, t.Header, 分组 = t.Group }).ToList());

        doc.Add("本机不适用（界面上整卡置灰、点不动）", unavailable.Select(t => new
        {
            t.Id,
            t.Header,
            原因 = t.MinBuild > 0
                ? $"需要 Windows 11 及以上（build ≥ {t.MinBuild}），本机 {build}"
                : $"仅 Windows 10 有效（build ≤ {t.MaxBuild}），本机 {build}",
        }).ToList());

        doc.Add("已排队待应用（拨了但还没写入注册表）", _pending.Select(kv => new
        {
            Id = kv.Key,
            项名 = all.FirstOrDefault(t => t.Id == kv.Key)?.Header ?? kv.Key,
            目标值 = kv.Value,
            写入后 = all.FirstOrDefault(t => t.Id == kv.Key) is { } d
                ? (kv.Value == d.OnValue ? "开" : kv.Value == d.OffValue ? "关" : $"第 {kv.Value} 档")
                : "",
        }).ToList());

        doc.Add("全部条目", all.Select(t => new
        {
            t.Id,
            t.Header,
            分组 = t.Group,
            说明 = t.Description,
            注册表 = t.FullPath,
            当前值 = TweakStore.EffectiveValue(t),
            当前是否开启 = TweakStore.IsOn(t),
            开 = t.OnValue,
            关 = t.OffValue,
            可选档位 = t.IsChoice
                ? t.Choices.Select(c => new { 文字 = c.Label, 值 = c.Value }).ToList()
                : null,
            待应用的目标值 = _pending.TryGetValue(t.Id, out var target) ? target : (int?)null,
            需要刷新 = t.Refresh switch
            {
                TweakRefresh.Tray => "任务栏（写完发 TraySettings 通知）",
                TweakRefresh.Desktop => "桌面（写完发刷新通知）",
                _ => "不需要",
            },
            改前需二次确认 = t.Risk == TweakRisk.Caution,
            本机是否适用 = TweakRules.IsApplicable(t, build),
        }).ToList());

        doc.Add("本页不做的事（被问到就这样答，不要自己推测能不能改）", new[]
        {
            "UAC、SmartScreen、Defender 等安全设置 —— 改错会让程序打不开，本页一律不提供",
            "系统服务（SysMain、搜索索引、打印后台）—— 停了机器变慢且很难联想到是优化工具所为",
            "Windows 更新策略 —— 教室机不更新比更新更麻烦",
            "右键菜单禁用项 —— 靠删 CLSID 实现，影响面大且难还原",
            "IE / Edge / WinRAR 等具体软件的设置 —— 装了才有意义，不属于系统层",
            "机器级设置（休眠、快速启动、分页文件）—— 在「系统功能」页，那里才提权",
        });

        doc.Add("界面怎么操作", new
        {
            拨开关或改下拉框 = "只排队，不立即写入注册表",
            应用更改 = "页面右下角提示条的主按钮，一次写入全部待应用项",
            单项取消 = "提示条的任务清单里，每行右侧有「取消」",
            提示被收起后 = "页面顶部出现「待应用 N 项」按钮，点它把提示叫回来",
            提示条的位置 = "右下角浮出，不随页面滚动",
        });

        return doc;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildChoices();    
        ApplyOsFilter();   
        LoadStates();
    }

    
    private static TweakDef? DefOf(object? tag) =>
        tag is string id ? TweakCatalog.All.FirstOrDefault(t => t.Id == id) : null;

    
    private static string? TagOf(SettingsCard card) => card.Content switch
    {
        ToggleSwitch toggle => toggle.Tag as string,
        ComboBox combo => combo.Tag as string,
        _ => null,
    };

    

    
    private void BuildChoices()
    {
        
        
        _loading = true;
        try
        {
            foreach (var combo in Combos())
            {
                var def = DefOf(combo.Tag);
                if (def is null || !def.IsChoice) continue;

                combo.Items.Clear();
                foreach (var choice in def.Choices)
                    combo.Items.Add(new ComboBoxItem { Content = choice.Label, Tag = choice.Value });
            }
        }
        finally
        {
            _loading = false;
        }
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private void ApplyOsFilter()
    {
        var build = SystemInfo.OsBuild();
        var disabled = new List<string>();

        foreach (var child in RowsPanel.Children)
        {
            if (child is not SettingsCard card) continue;

            var def = DefOf(TagOf(card));
            if (def is null) continue;

            var available = TweakRules.IsApplicable(def, build);

            card.IsEnabled = available;
            if (available) RestoreDescription(card);
            else
            {
                NoteUnsupported(card, def);
                disabled.Add(def.Id);
            }
        }

        if (disabled.Count == 0)
            AppLog.Step("tweaks", $"系统 build {build}：全部 {TweakCatalog.All.Count} 项均可用");
        else
            AppLog.Step("tweaks", $"系统 build {build}：停用 {disabled.Count} 项（{string.Join("、", disabled)}）");
    }

    
    private void NoteUnsupported(SettingsCard card, TweakDef def)
    {
        
        if (!_originalDescription.TryGetValue(card, out var original))
        {
            original = card.Description;
            _originalDescription[card] = original;
        }

        var text = original switch
        {
            TextBlock block => block.Text,
            string plain => plain,
            _ => string.Empty,
        };

        var note = $"此项需要 {RequiredSystemName(def)}，当前系统不支持，已停用。";

        
        card.Description = new TextBlock
        {
            Text = text.Length == 0 ? note : text + "\n" + note,
            TextWrapping = TextWrapping.Wrap,
        };
    }

    
    private void RestoreDescription(SettingsCard card)
    {
        if (_originalDescription.TryGetValue(card, out var original) && original is not null)
            card.Description = original;
    }

    
    private static string RequiredSystemName(TweakDef def)
    {
        if (def.MinBuild >= TweakCatalog.Win11Build) return "Windows 11 及以上系统";
        if (def.MaxBuild > 0 && def.MaxBuild < TweakCatalog.Win11Build) return "Windows 10";
        return "更高的系统版本";
    }

    
    private void LoadStates()
    {
        _loading = true;
        try
        {
            var seen = new List<string>();

            foreach (var toggle in Toggles())
            {
                var def = DefOf(toggle.Tag);
                if (def is null) continue;

                toggle.IsOn = TweakStore.EffectiveValue(def) == def.OnValue;
                seen.Add($"{def.Id}={(toggle.IsOn ? "开" : "关")}");
            }

            foreach (var combo in Combos())
            {
                var def = DefOf(combo.Tag);
                if (def is null || !def.IsChoice) continue;

                var index = IndexOfValue(def, TweakStore.EffectiveValue(def));
                combo.SelectedIndex = index;
                seen.Add($"{def.Id}={(index >= 0 ? def.Choices[index].Label : "未识别")}");
            }

            
            AppLog.Step("tweaks", $"按当前系统设置回填 {seen.Count} 项；当前状态：{string.Join("、", seen)}");
        }
        finally
        {
            _loading = false;
        }
    }

    private static int IndexOfValue(TweakDef def, int value)
    {
        for (var i = 0; i < def.Choices.Count; i++)
            if (def.Choices[i].Value == value) return i;

        return -1;   
    }

    

    
    
    
    
    
    
    
    private IEnumerable<ToggleSwitch> Toggles()
    {
        var seen = new HashSet<ToggleSwitch>();

        foreach (var child in RowsPanel.Children)
            if (child is SettingsCard { Content: ToggleSwitch toggle }
                && toggle.Tag is string
                && seen.Add(toggle))
                yield return toggle;

        foreach (var toggle in VisualDescendants<ToggleSwitch>(this))
            if (toggle.Tag is string && seen.Add(toggle))
                yield return toggle;
    }

    
    private IEnumerable<ComboBox> Combos()
    {
        var seen = new HashSet<ComboBox>();

        foreach (var child in RowsPanel.Children)
            if (child is SettingsCard { Content: ComboBox combo }
                && combo.Tag is string
                && seen.Add(combo))
                yield return combo;

        foreach (var combo in VisualDescendants<ComboBox>(this))
            if (combo.Tag is string && seen.Add(combo))
                yield return combo;
    }

    
    private static List<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is T match) found.Add(match);
                queue.Enqueue(child);
            }
        }

        return found;
    }

    

    private async void Row_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _busy) return;
        if (sender is not ToggleSwitch toggle) return;

        var def = DefOf(toggle.Tag);
        if (def is null) return;

        var want = toggle.IsOn;
        var target = want ? def.OnValue : def.OffValue;

        
        if (def.Risk == TweakRisk.Caution)
        {
            
            
            
            if (_dialogOpen || XamlRoot is null)
            {
                Revert(toggle, want);
                return;
            }

            bool ok;
            _dialogOpen = true;
            try
            {
                ok = await ConfirmAsync(def.Header,
                    $"将要修改系统设置：{def.Description}\n\n应用后若要还原，把这一项再改回去并重新应用。");
            }
            catch (Exception ex)
            {
                
                AppLog.Warn("tweaks", $"确认框未显示（{def.Id}）：{ex.Message}");
                ok = false;
            }
            finally
            {
                _dialogOpen = false;
            }

            if (!ok)
            {
                Revert(toggle, want);
                return;
            }
        }

        Queue(def, target, want ? "开" : "关");
    }

    
    
    
    
    
    
    
    private void Row_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _busy) return;
        if (sender is not ComboBox combo) return;

        var def = DefOf(combo.Tag);
        if (def is null || !def.IsChoice) return;

        var index = combo.SelectedIndex;
        if (index < 0 || index >= def.Choices.Count) return;

        var choice = def.Choices[index];
        Queue(def, choice.Value, choice.Label);
    }

    
    
    
    
    
    
    
    private void Queue(TweakDef def, int target, string label)
    {
        if (TweakStore.ReadValue(def) == target) _pending.Remove(def.Id);
        else _pending[def.Id] = target;

        AppLog.Step("tweaks", _pending.ContainsKey(def.Id)
            ? $"排队：{def.Id} = {target}（{label}）"
            : $"取消排队：{def.Id}（与当前系统状态一致）");

        
        _tipDismissed = false;
        _tipMode = TipMode.Pending;

        RefreshPendingUi();
    }

    
    private void Revert(ToggleSwitch toggle, bool want)
    {
        _loading = true;
        try { toggle.IsOn = !want; }
        finally { _loading = false; }
    }

    
    
    
    
    
    
    
    private void RefreshPendingUi()
    {
        if (_pending.Count == 0)
        {
            ApplyTip.IsOpen = false;
            _tipMode = TipMode.Pending;
            _tipDismissed = false;
            PendingButton.Visibility = Visibility.Collapsed;
            return;
        }

        RebuildPendingList();

        var needsRefresh = _pending.Keys.Any(id =>
            TweakCatalog.All.FirstOrDefault(t => t.Id == id)?.Refresh != TweakRefresh.None);

        
        if (_tipMode == TipMode.Pending) ShowTipPending(needsRefresh);

        RefreshPendingButton();
    }

    
    private void RefreshPendingButton()
    {
        PendingButton.Content = $"待应用 {_pending.Count} 项";
        PendingButton.Visibility = _pending.Count > 0 && !ApplyTip.IsOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    
    
    
    
    
    
    private void RebuildPendingList()
    {
        PendingList.Items.Clear();

        foreach (var (id, target) in _pending)
        {
            var def = TweakCatalog.All.FirstOrDefault(t => t.Id == id);
            if (def is null) continue;

            var label = new TextBlock
            {
                Text = $"{def.Header} → {DescribeTarget(def, target)}",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var cancel = new Button
            {
                Content = "取消",
                Tag = def.Id,
                MinWidth = 0,                                        
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            cancel.Click += CancelPending_Click;

            var row = new Grid { ColumnSpacing = 16, Margin = new Thickness(0, 7, 0, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(label);
            Grid.SetColumn(cancel, 1);
            row.Children.Add(cancel);

            PendingList.Items.Add(row);
        }
    }

    
    private static string DescribeTarget(TweakDef def, int value)
    {
        if (def.IsChoice)
            foreach (var choice in def.Choices)
                if (choice.Value == value) return choice.Label;

        return value == def.OnValue ? "开" : "关";
    }

    
    
    
    
    private void CancelPending_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;

        if (_pending.Remove(id)) AppLog.Step("tweaks", $"从任务清单取消：{id}");

        _tipMode = TipMode.Pending;
        LoadStates();
        RefreshPendingUi();
    }

    
    private void PendingButton_Click(object sender, RoutedEventArgs e)
    {
        _tipDismissed = false;
        _tipMode = TipMode.Pending;
        RefreshPendingUi();
    }

    
    private void ApplyTip_Closed(TeachingTip sender, object args)
    {
        RefreshPendingButton();
    }

    
    
    
    
    
    
    
    
    private void FitTipToWindow()
    {
        var window = XamlRoot?.Size.Width ?? 1024;
        ApplyTip.MaxWidth = Math.Clamp(window - 120, 320, 520);
    }

    
    
    
    
    private void ShowTipPending(bool needsRefresh)
    {
        FitTipToWindow();

        ApplyTip.Title = $"有 {_pending.Count} 项待应用";

        
        ApplyTip.Subtitle = needsRefresh
            ? "应用后自动刷新任务栏与桌面。"
            : "只写当前用户设置，无需管理员权限。";
        ApplyTip.ActionButtonContent = "应用更改";
        ApplyTip.CloseButtonContent = "暂不应用";

        if (!_tipDismissed && !ApplyTip.IsOpen) ApplyTip.IsOpen = true;
    }

    

    
    
    
    private async Task ApplyPendingAsync()
    {
        if (_busy || _pending.Count == 0) return;

        var changes = _pending
            .Select(kv => (Def: TweakCatalog.All.First(t => t.Id == kv.Key), Value: kv.Value))
            .ToList();

        _busy = true;

        
        ApplyTip.IsEnabled = false;
        ApplyTip.Title = "正在应用…";
        ApplyTip.Subtitle = "正在写入当前用户设置，并按实际值核对结果。";

        TweakApplyResult result;
        try
        {
            result = await TweakStore.ApplyAsync(changes);
        }
        catch (Exception ex)
        {
            
            AppLog.Exception("tweaks", ex, "应用更改");
            ShowNotice("应用失败：" + ex.Message, InfoBarSeverity.Error);
            RefreshPendingUi();
            return;
        }
        finally
        {
            _busy = false;              
            ApplyTip.IsEnabled = true;
        }

        
        
        
        _pending.Clear();
        foreach (var (id, target) in TweakRules.RemainingAfterApply(changes, result.Failed))
            _pending[id] = target;

        LoadStates();   

        if (result.Matched == result.Requested)
        {
            
            ShowNotice(
                result.Ok
                    ? $"已应用 {result.Matched} 项。"
                    : $"已应用 {result.Matched} 项，当前状态与目标一致，但写入过程有报错：{result.Error}",
                result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning);

            
            
            
            if (changes.Any(c => c.Def.Refresh != TweakRefresh.None)) ShowTipApplied(result.Matched);
            else RefreshPendingUi();
        }
        else
        {
            ShowNotice(
                $"已应用 {result.Matched}/{result.Requested} 项；未生效：{DescribeFailed(result.Failed)}。"
                + "可能被组策略或其他安全软件固定，剩下的项已留在任务列表里可重试。",
                InfoBarSeverity.Warning);

            
            _tipMode = TipMode.Failed;
            RefreshPendingUi();
            ShowTipFailed();
        }
    }

    
    private void ShowTipApplied(int applied)
    {
        FitTipToWindow();

        
        _tipMode = TipMode.Applied;
        ApplyTip.Title = $"已应用 {applied} 项";
        ApplyTip.Subtitle = "已通知系统刷新。若任务栏或桌面没有变化，重启资源管理器即可看到效果。";
        ApplyTip.ActionButtonContent = "重启资源管理器";
        ApplyTip.CloseButtonContent = "关闭";
        ApplyTip.IsOpen = true;

        
        
        RefreshPendingButton();
    }

    
    private void ShowTipFailed()
    {
        FitTipToWindow();

        ApplyTip.Title = $"有 {_pending.Count} 项未生效";
        ApplyTip.Subtitle = "可能被组策略或其他安全软件固定。可以再试一次，或重启系统后再看。";
        ApplyTip.ActionButtonContent = "重试";
        ApplyTip.CloseButtonContent = "关闭";
        ApplyTip.IsOpen = true;
        RefreshPendingButton();
    }

    

    private async void ApplyTip_ActionClick(TeachingTip sender, object args)
    {
        if (_tipMode == TipMode.Applied)
        {
            RestartExplorer_Click(sender, new RoutedEventArgs());
            return;
        }

        try
        {
            await ApplyPendingAsync();
        }
        catch (Exception ex)
        {
            AppLog.Exception("tweaks", ex, "应用更改（底部提示）");
            ShowNotice("应用失败：" + ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ApplyTip_CloseClick(TeachingTip sender, object args)
    {
        
        _tipDismissed = true;
    }

    
    private static string DescribeFailed(IReadOnlyList<string> failed)
    {
        if (failed.Count == 0) return "无";
        return string.Join("、", failed.Select(id =>
            TweakCatalog.All.FirstOrDefault(t => t.Id == id)?.Header ?? id));
    }

    private void RestartExplorer_Click(object sender, RoutedEventArgs e)
    {
        TweakStore.RestartExplorer();

        
        _tipMode = TipMode.Pending;
        RefreshPendingUi();

        ShowNotice("资源管理器已重启。若任务栏没有回来，用任务管理器重新运行 explorer.exe。", InfoBarSeverity.Success);
    }

    

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "应用这项",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        
        NoticeBar.Visibility = Visibility.Visible;
        NoticeBar.Severity = severity;
        NoticeBar.Message = message;
        NoticeBar.IsOpen = true;
    }

    
    private void NoticeBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        sender.Visibility = Visibility.Collapsed;
    }
}
