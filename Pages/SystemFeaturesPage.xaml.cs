using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data.SysFeatures;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 系统功能（需求清单第 20 条，2026-10-31 重做）。
///
/// <para>
/// 与「系统优化」页的分工：那一页全是 <c>HKCU</c> 的界面设置、不提权；这一页全是
/// <c>HKLM</c> 与 <c>powercfg</c>、**必须提权**，部分项要重启才生效。
/// </para>
///
/// <para>
/// 交互照 Nick 2026-10-31 的定案「排队 + 一次弹 UAC」：拨开关只进队列，
/// 点右下角提示条的主按钮时，<see cref="SystemFeatures.ApplyAsync"/> 把所有改动
/// 拼成一个临时脚本跑一次，只弹一次授权。
/// </para>
///
/// <para>
/// ⛔ 状态**只从服务层读**，页面不缓存：这些值用户可能在系统设置或组策略里改过，
/// 每次进来重读一遍最省事（读注册表是微秒级）。回填必须压在 <c>_loading</c> 里，
/// 否则 <c>Toggled</c> / <c>SelectionChanged</c> 会把程序写入当成用户操作 —— 这个坑踩过多次。
/// </para>
/// </summary>
public sealed partial class SystemFeaturesPage : Page
{
    private static readonly IReadOnlyList<SysFeatureDef> All = SystemFeatureCatalog.All;

    private static readonly Dictionary<string, SysFeatureDef> ById =
        All.ToDictionary(d => d.Id, StringComparer.Ordinal);

    /// <summary>待应用的改动：Id → 目标值（按 Id 去重，来回拨只留最后一次）。</summary>
    private readonly Dictionary<string, int> _pending = new(StringComparer.Ordinal);

    /// <summary>正在回填界面，此时的 Toggled / SelectionChanged 是程序触发的。</summary>
    private bool _loading;

    private bool _busy;

    /// <summary>确认框是否已经弹着 —— 同一时刻只允许一个 ContentDialog，第二个会抛异常。</summary>
    private bool _dialogOpen;

    /// <summary>用户点过「暂不应用」；之后再拨开关才重新弹。</summary>
    private bool _tipDismissed;

    private TipMode _tipMode = TipMode.Pending;

    private enum TipMode
    {
        Pending,
        Failed,
    }

    public SystemFeaturesPage()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            BuildChoices();   // 下拉框选项由清单生成（XAML 里写 ItemTemplate + x:Bind 会崩）
            LoadStates();
        };

        HubAiEntry.Visibility = App.Settings.Current.HubAiEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;

        AiFlyout.ContextProvider = BuildAiContext;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 从别的页面切回来时也重读一遍：可能刚在系统设置里改过
        if (IsLoaded) LoadStates();
    }

    // ── 读取 ─────────────────────────────────────────────────────────────

    private void LoadStates()
    {
        _loading = true;
        try
        {
            foreach (var def in All)
            {
                var value = def.Read();

                if (def.Kind == SysFeatureKind.Toggle)
                {
                    if (Find<ToggleSwitch>("Sw_", def.Id) is not { } toggle) continue;

                    toggle.IsOn = value is int v && v != 0;
                    toggle.IsEnabled = value is not null && !_busy;
                }
                else
                {
                    if (Find<ComboBox>("Cmb_", def.Id) is not { } combo) continue;

                    combo.SelectedIndex = value is int v ? IndexOf(def, v) : -1;
                    combo.IsEnabled = value is not null && !_busy;
                }
            }

            // 休眠关着时快速启动不可能生效：整行停用，比让用户点了没反应好
            if (SystemFeatures.Hibernate() != FeatureState.On &&
                Find<ToggleSwitch>("Sw_", "fast-startup") is { } fast)
                fast.IsEnabled = false;

            var bytes = SystemFeatures.HibernateFileBytes();
            HibernateFileText.Text = bytes > 0 ? AppLog.Size(bytes) : "不存在（休眠已关闭）";
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>把清单里的档位灌进对应下拉框（选项只有一个来源，别在 XAML 里再抄一遍字）。</summary>
    private void BuildChoices()
    {
        foreach (var def in All.Where(d => d.Kind == SysFeatureKind.Choice))
        {
            if (Find<ComboBox>("Cmb_", def.Id) is not { } combo || def.Choices is null) continue;

            combo.Items.Clear();
            foreach (var choice in def.Choices)
                combo.Items.Add(new ComboBoxItem { Content = choice.Label, Tag = choice.Value });
        }
    }

    private static int IndexOf(SysFeatureDef def, int value)
    {
        if (def.Choices is null) return -1;

        for (var i = 0; i < def.Choices.Count; i++)
            if (def.Choices[i].Value == value) return i;

        // 值不在档位里（系统里是别的数字）：不选中，别乱指一档骗人
        return -1;
    }

    /// <summary>按 <c>Sw_xxx_yyy</c> / <c>Cmb_xxx_yyy</c> 找 XAML 里那张卡上的控件。</summary>
    private T? Find<T>(string prefix, string id) where T : class =>
        FindName(prefix + id.Replace('-', '_')) as T;

    // ── 排队 ─────────────────────────────────────────────────────────────

    private void Row_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _busy) return;
        if (sender is not ToggleSwitch toggle || toggle.Tag is not string id) return;
        if (!ById.TryGetValue(id, out var def)) return;

        Queue(def, toggle.IsOn ? 1 : 0);
    }

    private void Row_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _busy) return;
        if (sender is not ComboBox combo || combo.Tag is not string id) return;
        if (!ById.TryGetValue(id, out var def)) return;
        if (combo.SelectedItem is not ComboBoxItem { Tag: int value }) return;

        Queue(def, value);
    }

    private void Queue(SysFeatureDef def, int target)
    {
        // 与当前系统值一致就不排队（否则"拨回去"也会留下一条什么都不改的待应用项）
        if (def.Read() == target) _pending.Remove(def.Id);
        else _pending[def.Id] = target;

        AppLog.Step("sysfeatures", _pending.ContainsKey(def.Id)
            ? $"排队：{def.Id} = {target}（{def.Label(target)}）"
            : $"取消排队：{def.Id}（与当前系统状态一致）");

        _tipDismissed = false;
        _tipMode = TipMode.Pending;
        UpdatePendingUi();
    }

    private void CancelPending_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        if (!ById.TryGetValue(id, out var def)) return;

        _pending.Remove(id);

        // 界面上那一行要拨回注册表里的真实值，否则它停在一个"看着改了、其实不会写入"的位置
        _loading = true;
        var actual = def.Read();

        if (def.Kind == SysFeatureKind.Toggle && Find<ToggleSwitch>("Sw_", id) is { } toggle)
            toggle.IsOn = actual is int v && v != 0;
        else if (Find<ComboBox>("Cmb_", id) is { } combo)
            combo.SelectedIndex = actual is int v2 ? IndexOf(def, v2) : -1;

        _loading = false;

        AppLog.Step("sysfeatures", $"取消排队：{id}");
        UpdatePendingUi();
    }

    // ── 提示条 ───────────────────────────────────────────────────────────

    private void UpdatePendingUi()
    {
        if (_pending.Count == 0)
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
        PendingButton.Content = $"待应用 {_pending.Count} 项";
        PendingButton.Visibility = ApplyTip.IsOpen ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>清单：每行「项名 → 目标值」+ 一颗「取消」。</summary>
    private void BuildPendingList()
    {
        PendingList.Items.Clear();

        foreach (var (id, value) in _pending)
        {
            if (!ById.TryGetValue(id, out var def)) continue;

            var row = new Grid { ColumnSpacing = 16, Margin = new Thickness(0, 7, 0, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13,
            };
            text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = def.Header });
            text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = "  →  " + def.Label(value),
                Foreground = ThemeBrush.Get(this, "TextFillColorSecondaryBrush"),
            });

            var cancel = new Button
            {
                Content = "取消",
                Tag = id,
                MinWidth = 0,
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            cancel.Click += CancelPending_Click;

            Grid.SetColumn(cancel, 1);
            row.Children.Add(text);
            row.Children.Add(cancel);

            PendingList.Items.Add(row);
        }
    }

    private void ShowTipPending()
    {
        ApplyTip.Title = $"有 {_pending.Count} 项待应用";
        ApplyTip.Subtitle = "应用时会请求一次管理员授权。";
        ApplyTip.ActionButtonContent = "应用更改";
        ApplyTip.CloseButtonContent = "暂不应用";

        ApplyTipMaxWidth(windowWidth: XamlRoot?.Size.Width ?? 1024);

        if (!_tipDismissed && !ApplyTip.IsOpen) ApplyTip.IsOpen = true;
    }

    private void ShowTipFailed()
    {
        ApplyTip.Title = $"有 {_pending.Count} 项未生效";
        ApplyTip.Subtitle = "可能被组策略限定。可以再试一次。";
        ApplyTip.ActionButtonContent = "重试";
        ApplyTip.CloseButtonContent = "关闭";

        ApplyTipMaxWidth(windowWidth: XamlRoot?.Size.Width ?? 1024);
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
        PendingButton.Content = $"待应用 {_pending.Count} 项";
        PendingButton.Visibility = _pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        PendingButton.Content = $"待应用 {_pending.Count} 项";
        PendingButton.Visibility = _pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
            AppLog.Exception("sysfeatures", ex, "应用更改");
            ShowNotice("应用失败：" + ex.Message, InfoBarSeverity.Error);
        }
    }

    // ── 应用 ─────────────────────────────────────────────────────────────

    private async Task ApplyPendingAsync()
    {
        if (_busy || _pending.Count == 0) return;

        if (_dialogOpen) return;

        var changes = _pending
            .Where(kv => ById.ContainsKey(kv.Key))
            .Select(kv => (Def: ById[kv.Key], Value: kv.Value))
            .ToList();

        if (changes.Count == 0) return;

        // 整批一次总确认：逐项弹窗的话，十几项要点十几次（与"一次 UAC"是同一个道理）
        var needRestart = changes.Any(c => c.Def.NeedsRestart);
        var warn = changes.FirstOrDefault(c => c.Def.Confirm is not null).Def?.Confirm;

        var message = $"将修改 {changes.Count} 项系统设置，需要一次管理员授权。" +
                      (warn is null ? "" : $"\n\n{warn}") +
                      (needRestart ? "\n\n其中有的项要重启后才生效。" : "");

        if (!await ConfirmAsync("应用系统设置", message)) return;

        _busy = true;
        ApplyTip.IsOpen = false;

        try
        {
            var result = await SystemFeatures.ApplyAsync(changes);

            if (!result.Ok)
            {
                ShowNotice(result.Error ?? "应用失败。", InfoBarSeverity.Error);
                _tipMode = TipMode.Failed;
                UpdatePendingUi();
                return;
            }

            if (result.AllMatched)
            {
                _pending.Clear();
                ShowNotice($"已应用 {result.Requested} 项。", InfoBarSeverity.Success);
            }
            else
            {
                // 只有确认没生效的项留在队列里，方便直接重试
                foreach (var id in result.Failed) _pending.Remove(id);

                ShowNotice(
                    $"已应用 {result.Matched}/{result.Requested} 项，未生效：{string.Join("、", result.Failed.Select(NameOf))}。",
                    InfoBarSeverity.Warning);

                _tipMode = _pending.Count > 0 ? TipMode.Failed : TipMode.Pending;
            }

            LoadStates();
        }
        finally
        {
            _busy = false;
            LoadStates();
            UpdatePendingUi();
        }
    }

    private static string NameOf(string id) => ById.TryGetValue(id, out var def) ? def.Header : id;

    // ── Hub AI ───────────────────────────────────────────────────────────

    private void HubAiEntry_Click(object sender, RoutedEventArgs e) => AiFlyout.Toggle();

    /// <summary>
    /// 给浮层准备这一页的现场数据。
    /// <para>
    /// 这一页的项直接动系统，用户最爱问的是"改了会不会有影响」「为什么点了没反应」，
    /// 所以每项都附**当前值 + 目标值 + 是否要重启 + 读不到的原因**。
    /// </para>
    /// </summary>
    private PageContextDoc BuildAiContext()
    {
        var doc = new PageContextDoc(
            "系统功能",
            "这一页修改的是整台电脑的系统设置（HKLM 与电源方案），需要管理员授权，部分项要重启才生效。" +
            "回答时以「全部条目」里的当前值为准，直接点名是哪一项；不要给与本页无关的建议。" +
            "⚠️ 你无法替用户改设置，也不要说「已经帮你改好了」—— 写入只能由用户拨动开关、" +
            "再点右下角提示条的主按钮完成，而且必须经过一次管理员授权。");

        doc.Add("状态", new
        {
            待应用项数 = _pending.Count,
            正在应用 = _busy,
            休眠文件 = SystemFeatures.HibernateFileBytes() is var b && b > 0 ? AppLog.Size(b) : "不存在",
        });

        doc.Add("全部条目", All.Select(d =>
        {
            var value = d.Read();
            return new
            {
                d.Id,
                d.Header,
                分组 = d.Group,
                说明 = d.Description,
                当前值 = value,
                当前显示 = value is int v ? d.Label(v) : "读取不到",
                可选档位 = d.Choices?.Select(c => new { c.Label, c.Value }).ToList(),
                待应用目标值 = _pending.TryGetValue(d.Id, out var t) ? t : (int?)null,
                需要重启 = d.NeedsRestart,
                改前提示 = d.Confirm,
            };
        }).ToList());

        doc.Add("界面怎么操作", new
        {
            拨开关或改下拉框 = "只排队，不改系统",
            应用更改 = "右下角提示条的主按钮，一次授权、一次写入全部待应用项",
            单项取消 = "提示条清单里每行右侧的「取消」",
            提示被收起 = "页面顶部出现「待应用 N 项」按钮",
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
            AppLog.Warn("sysfeatures", "确认框没能弹出：" + ex.Message);
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
