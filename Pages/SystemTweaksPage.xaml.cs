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

/// <summary>
/// 系统优化（需求清单第 20 条，2026-10-31）。对齐 Dism++「系统优化」里能安全落地的部分。
///
/// <para>
/// ⭐ 界面**逐行照抄 `AutoShutdownPage` 的写法**：一行一个 `<c>controls:SettingsCard</c>` 直接写在
/// XAML 里，开关就是卡片内容区里的 `<c>ToggleSwitch</c>`。
/// ⛔ 不要改成 `ItemsControl` + `DataTemplate` 包 SettingsCard —— 2026-10-31 试过，
/// SettingsCard 在模板里排不出内容区，右侧开关整个不渲染（Nick：「能抄就抄，别猜」）。
/// </para>
/// <para>
/// ⚠️ 这些项都在当前用户（<c>HKCU</c>），改完即时生效，**不弹 UAC**：
/// 切换只排队，点「应用更改」时统一写入；需要重启资源管理器才看得到的项，条上给按钮。
/// （机器级项在「系统功能」页，那里才提权。）
/// </para>
/// </summary>
public sealed partial class SystemTweaksPage : Page
{
    /// <summary>待应用的改动（按 Id 去重：来回切换只留最后一次）。</summary>
    private readonly Dictionary<string, bool> _pending = new(StringComparer.Ordinal);

    /// <summary>正在回填界面，此时的 Toggled 是程序触发的。</summary>
    private bool _loading;

    private bool _busy;

    public SystemTweaksPage()
    {
        InitializeComponent();

        // ⚠️ 必须等 Loaded 之后再读：`SettingsCard` 的内容要等模板套用才进视觉树，
        //    构造函数里找开关一个都找不到 —— 而 `ToggleSwitch` 默认就是关，
        //    于是"系统本来是深色模式"却显示成关（2026-10-31 Nick 指出的就是这个）。
        Loaded += (_, _) => LoadStates();
    }

    /// <summary>按开关上的 <c>Tag</c>（项的 Id）反查定义。</summary>
    private static TweakDef? DefOf(object? tag) =>
        tag is string id ? TweakCatalog.All.FirstOrDefault(t => t.Id == id) : null;

    /// <summary>把界面上所有带 Tag 的开关按注册表当前值摆一遍。</summary>
    private void LoadStates()
    {
        _loading = true;
        try
        {
            var count = 0;
            var on = new List<string>();

            foreach (var toggle in Toggles())
            {
                var def = DefOf(toggle.Tag);
                if (def is null) continue;

                toggle.IsOn = TweakStore.IsOn(def);
                count++;

                if (toggle.IsOn) on.Add(def.Id);
            }

            // 逐项打出来：截图工具在会抽掉右侧开关，靠日志核对"系统本来是深色"这类初始态最可靠
            AppLog.Step("tweaks",
                $"按当前系统设置回填 {count} 项开关；打开的：{(on.Count == 0 ? "无" : string.Join("、", on))}");
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// 页面里那批开关。
    /// <para>
    /// 先走 <c>RowsPanel.Children</c> 里每张 <c>SettingsCard</c> 的 <c>Content</c> —— 内容在 XAML 解析后
    /// 就挂在属性上，不依赖模板是否已经套用；视觉树那段留作兜底（将来卡片嵌进别的容器时仍找得到）。
    /// </para>
    /// </summary>
    private IEnumerable<ToggleSwitch> Toggles()
    {
        var seen = new HashSet<ToggleSwitch>();

        foreach (var child in RowsPanel.Children)
        {
            if (child is SettingsCard { Content: ToggleSwitch toggle }
                && toggle.Tag is string
                && seen.Add(toggle))
            {
                yield return toggle;
            }
        }

        foreach (var toggle in VisualToggles(this))
        {
            if (toggle.Tag is string && seen.Add(toggle))
                yield return toggle;
        }
    }

    /// <summary>递归找视觉树里的开关（兜底用）。</summary>
    private static List<ToggleSwitch> VisualToggles(DependencyObject root)
    {
        var found = new List<ToggleSwitch>();
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is ToggleSwitch toggle && toggle.Tag is string) found.Add(toggle);
                queue.Enqueue(child);
            }
        }

        return found;
    }

    // ── 切换 ─────────────────────────────────────────────────────────────

    private async void Row_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _busy) return;
        if (sender is not ToggleSwitch toggle) return;

        var def = DefOf(toggle.Tag);
        if (def is null) return;

        var want = toggle.IsOn;

        // 高危项：先问一句。取消就把开关拨回去（拨回时同样要压 _loading，否则会再弹一次）
        if (def.Risk == TweakRisk.Caution)
        {
            var ok = await ConfirmAsync(def.Header,
                $"将要修改系统设置：{def.Description}\n\n应用后若要还原，把这一项再关回去并重新应用。");

            if (!ok)
            {
                _loading = true;
                toggle.IsOn = !want;
                _loading = false;
                return;
            }
        }

        _pending[def.Id] = want;
        AppLog.Step("tweaks", $"排队：{def.Id} = {want}");

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
        RestartButton.Visibility = Visibility.Collapsed;   // 应用完之后才提示重启

        var restart = _pending.Keys.Any(id =>
            TweakCatalog.All.FirstOrDefault(t => t.Id == id)?.Apply == TweakApply.RestartExplorer);

        PendingText.Text = $"有 {_pending.Count} 项待应用" + (restart ? "，其中包含需要重启资源管理器的项" : "");
    }

    // ── 应用 ─────────────────────────────────────────────────────────────

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _pending.Count == 0) return;

        var changes = _pending
            .Select(kv => (Def: TweakCatalog.All.First(t => t.Id == kv.Key), On: kv.Value))
            .ToList();

        _busy = true;
        PendingText.Text = "正在应用…";

        try
        {
            var result = await TweakStore.ApplyAsync(changes);

            if (!result.Ok)
            {
                ShowNotice("有项没有写进去：" + result.Error, InfoBarSeverity.Warning);
                return;
            }

            _pending.Clear();
            LoadStates();

            if (result.Matched == result.Requested)
            {
                ShowNotice($"已应用 {result.Requested} 项。", InfoBarSeverity.Success);

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
