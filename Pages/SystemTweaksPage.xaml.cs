using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data.Tweaks;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>列表里的一行（界面绑定用）。</summary>
public sealed class TweakRow : INotifyPropertyChanged
{
    private bool _isOn;

    public TweakRow(TweakDef def)
    {
        Def = def;
        IsOn = TweakStore.IsOn(def);
    }

    public TweakDef Def { get; }

    public string Header => Def.ScopeNote.Length > 0 ? $"{Def.Header}（{Def.ScopeNote}）" : Def.Header;

    public string Description => Def.Description;

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value) return;
            _isOn = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOn)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 系统优化（需求清单第 20 条，2026-10-31）。对齐 Dism++「系统优化」里能安全落地的部分。
///
/// <para>
/// ⭐ 交互按 Nick 的要求：**切换只排队，点「应用更改」时整批一次提权**（写 HKLM 必须管理员，
/// 但一屏十几项只弹一次 UAC）；需要重启资源管理器才看得到的项，条上直接给按钮。
/// </para>
/// <para>
/// ⚠️ 切换高危项（<see cref="TweakRisk.Caution"/>）先弹二次确认，取消要把开关拨回去 ——
/// 拨回时同样要压 <c>_loading</c>，否则会再触发一次 Toggled 把确认框弹两遍。
/// </para>
/// </summary>
public sealed partial class SystemTweaksPage : Page
{
    private readonly List<TweakRow> _rows = new();

    /// <summary>待应用的改动（按 Id 去重：来回切换只留最后一次）。</summary>
    private readonly Dictionary<string, bool> _pending = new(StringComparer.Ordinal);

    /// <summary>正在回填界面，此时的 Toggled 是程序触发的。</summary>
    private bool _loading;

    private bool _busy;

    public SystemTweaksPage()
    {
        InitializeComponent();

        foreach (var def in TweakCatalog.All) _rows.Add(new TweakRow(def));

        TaskbarRows.ItemsSource = RowsOf(TweakCatalog.GroupTaskbar);
        ExplorerRows.ItemsSource = RowsOf(TweakCatalog.GroupExplorer);
        VisualRows.ItemsSource = RowsOf(TweakCatalog.GroupVisual);
        InputRows.ItemsSource = RowsOf(TweakCatalog.GroupInput);

        UpdatePendingBar();
    }

    private List<TweakRow> RowsOf(string group) =>
        _rows.Where(r => string.Equals(r.Def.Group, group, StringComparison.Ordinal)).ToList();

    // ── 切换 ─────────────────────────────────────────────────────────────

    private async void Row_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _busy) return;
        if (sender is not ToggleSwitch toggle || toggle.DataContext is not TweakRow row) return;

        var want = toggle.IsOn;

        // 高危项：先问一句。取消就把开关拨回去（同 AppData 卡的"值没变就忽略"思路）
        if (row.Def.Risk == TweakRisk.Caution)
        {
            var ok = await ConfirmAsync(row.Def.Header,
                $"将要修改系统设置：{row.Def.Description}\n\n应用后如需还原，把这一项再关回去并重新应用。");

            if (!ok)
            {
                _loading = true;
                toggle.IsOn = !want;
                _loading = false;
                return;
            }
        }

        row.IsOn = want;
        _pending[row.Def.Id] = want;

        AppLog.Step("tweaks", $"排队：{row.Def.Id} = {want}");

        UpdatePendingBar();
    }

    private void UpdatePendingBar()
    {
        if (_pending.Count == 0)
        {
            PendingBar.Visibility = Visibility.Collapsed;
            RestartButton.Visibility = Visibility.Collapsed;
            return;
        }

        PendingBar.Visibility = Visibility.Visible;

        var restart = _pending.Keys.Any(id =>
            TweakCatalog.All.First(t => t.Id == id).Apply == TweakApply.RestartExplorer);

        PendingText.Text = $"有 {_pending.Count} 项待应用" + (restart ? "，其中包含需要重启资源管理器的项" : "");
        RestartButton.Visibility = Visibility.Collapsed;   // 应用完之后才提示重启
    }

    // ── 应用 ─────────────────────────────────────────────────────────────

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _pending.Count == 0) return;

        var changes = _pending
            .Select(kv => (Def: TweakCatalog.All.First(t => t.Id == kv.Key), On: kv.Value))
            .ToList();

        _busy = true;
        PendingText.Text = "正在请求管理员授权…";

        try
        {
            var result = await TweakStore.ApplyAsync(changes);

            if (!result.Ok)
            {
                ShowNotice("未完成：授权被取消或导入失败。" + result.Error, InfoBarSeverity.Warning);
                return;
            }

            _pending.Clear();
            ReloadRows();

            if (result.Matched == result.Requested)
            {
                ShowNotice($"已应用 {result.Requested} 项。", InfoBarSeverity.Success);

                // 只有确实改了需要重启资源管理器的项，才把按钮露出来
                if (changes.Any(c => c.Def.Apply == TweakApply.RestartExplorer))
                {
                    PendingBar.Visibility = Visibility.Visible;
                    PendingText.Text = "改动已写入，部分项需要重启资源管理器才会显示效果。";
                    RestartButton.Visibility = Visibility.Visible;
                }
            }
            else
            {
                ShowNotice(
                    $"已应用 {result.Matched}/{result.Requested} 项。未生效的项可能被组策略或其他安全软件固定，重启后再看一次。",
                    InfoBarSeverity.Warning);
            }
        }
        finally
        {
            _busy = false;
            if (_pending.Count == 0) UpdatePendingBar();
        }
    }

    private void ReloadRows()
    {
        _loading = true;
        try
        {
            foreach (var row in _rows) row.IsOn = TweakStore.IsOn(row.Def);
        }
        finally
        {
            _loading = false;
        }
    }

    private void RestartExplorer_Click(object sender, RoutedEventArgs e)
    {
        TweakStore.RestartExplorer();

        RestartButton.Visibility = Visibility.Collapsed;
        PendingBar.Visibility = Visibility.Collapsed;

        ShowNotice("资源管理器已重启。若任务栏没有回来，用任务管理器重新运行 explorer.exe。", InfoBarSeverity.Success);
    }

    // ── 界面小工具 ────────────────────────────────────────────────────────

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
        NoticeBar.Severity = severity;
        NoticeBar.Message = message;
        NoticeBar.IsOpen = true;
    }
}
