using System;
using System.Text;
using ClassSoftwareHub.Desktop.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 「白板专杀」页（实验性功能，tag = <c>easiguard</c>）。
///
/// 逻辑全在 <see cref="EasiNoteGuard"/>（扫进程 / 判窗口 / 杀 / 定时），
/// 这里只管界面，外加一个 <see cref="_ready"/> 闸门挡住构造期的假事件。
/// </summary>
public sealed partial class EasiNoteGuardPage : Page
{
    private EasiNoteGuardConfig _cfg = new();

    /// <summary>
    /// ⚠️ 构造期给 ToggleSwitch 设初值会**触发 Toggled**，那一刻 _cfg 还没铺好，
    ///    回写就把存档冲成默认值了。所以铺完初值再放行（跟 ClockToolPage 同一个套路）。
    /// </summary>
    private bool _ready;

    public EasiNoteGuardPage()
    {
        InitializeComponent();

        _cfg = EasiNoteGuardConfig.Load();
        EnableSwitch.IsOn = _cfg.Enabled;                  // ⚠️ 别写进 XAML
        TimePick.Time = new TimeSpan(17, 30, 0);

        RefreshTimes();
        RefreshProcs();
        UpdateHint();

        _ready = true;
    }

    // ── 开关 / 时间点 ────────────────────────────────────────

    private void EnableSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _cfg.Enabled = EnableSwitch.IsOn;
        _cfg.Save();
        UpdateHint();
    }

    private void UpdateHint()
    {
        var n = _cfg.Times.Count;
        SwitchHint.Text = _cfg.Enabled
            ? (n == 0
                ? "已开启，但尚未设定时间点。添加时间点后才会执行。"
                : $"已开启：每天 {string.Join("、", _cfg.Times)} 各检查一次。本程序未运行时不会生效。")
            : "已关闭。开启后，到达设定时间点将自动结束白板5 的后台滞留进程。";
    }

    private void RefreshTimes()
    {
        TimeHost.Children.Clear();

        if (_cfg.Times.Count == 0)
        {
            TimeHost.Children.Add(new TextBlock
            {
                Text = "尚未设定时间点。添加时间点（例如 17:30）后，每天到达该时间点检查一次。",
                FontSize = 12,
                Opacity = 0.55,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var t in _cfg.Times.ToArray())
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = t,
                FontSize = 13.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            var del = new Button
            {
                Content = "删除",
                FontSize = 12,
                Padding = new Thickness(10, 4, 10, 4),
                Tag = t
            };
            del.Click += RemoveTime_Click;

            Grid.SetColumn(del, 1);
            row.Children.Add(label);
            row.Children.Add(del);
            TimeHost.Children.Add(row);
        }
    }

    private void AddTime_Click(object sender, RoutedEventArgs e)
    {
        var t = TimePick.Time;
        var hhmm = $"{t.Hours:D2}:{t.Minutes:D2}";

        if (_cfg.Times.Contains(hhmm))
        {
            ResultText.Text = $"时间点 {hhmm} 已存在。";
            return;
        }

        _cfg.Times.Add(hhmm);
        _cfg.Times.Sort(StringComparer.Ordinal);           // 列表按时间先后排，看着舒服
        _cfg.Save();

        RefreshTimes();
        UpdateHint();
        ResultText.Text = $"已添加 {hhmm}。本程序运行期间，每天到达该时间点检查一次。";
    }

    private void RemoveTime_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string t)
        {
            _cfg.Times.Remove(t);
            _cfg.Save();
            RefreshTimes();
            UpdateHint();
        }
    }

    // ── 手动执行 ─────────────────────────────────────────────

    private void Check_Click(object sender, RoutedEventArgs e) =>
        Show(EasiNoteGuard.RunOnce(dryRun: true), "仅检测");

    private void Clean_Click(object sender, RoutedEventArgs e)
    {
        var r = EasiNoteGuard.RunOnce(dryRun: false);

        // 手动清理也留一条日志 —— 以后翻 easiguard.log 能分清「是到点自己杀的」还是「我手点的」
        // （到点那条在 EasiNoteGuard.Tick 里记）
        EasiNoteGuard.Log($"手动清理：{r.Summary}"
            + (r.KilledNames.Count > 0 ? " 清掉=" + string.Join("、", r.KilledNames) : "")
            + (r.Errors.Count > 0 ? " 失败=" + string.Join("；", r.Errors) : ""));

        Show(r, "立即结束");
    }

    private void Show(EasiNoteGuard.GuardResult r, string title)
    {
        var sb = new StringBuilder();
        sb.Append(title).Append("：").Append(r.Summary);

        if (r.KilledNames.Count > 0)
            sb.Append('\n').Append(r.DryRun ? "可结束的进程：" : "已结束的进程：").Append(string.Join("、", r.KilledNames));
        if (r.SkippedNames.Count > 0)
            sb.Append('\n').Append("因存在可见窗口而跳过的：").Append(string.Join("、", r.SkippedNames));
        if (r.Errors.Count > 0)
            sb.Append('\n').Append("结束失败：").Append(string.Join("；", r.Errors));

        ResultText.Text = sb.ToString();
        RefreshProcs();
    }

    // ── 当前进程 ─────────────────────────────────────────────

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshProcs();

    private void RefreshProcs()
    {
        ProcHost.Children.Clear();

        var list = EasiNoteGuard.Scan();
        if (list.Count == 0)
        {
            ProcHost.Children.Add(new TextBlock
            {
                Text = "当前未检测到希沃白板5 进程。",
                FontSize = 12.5,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var p in list)
        {
            ProcHost.Children.Add(new TextBlock
            {
                Text = $"{p.Name}（PID {p.Pid}）—— " + (p.HasWindow
                    ? "存在可见窗口，正在使用 → 将跳过"
                    : "无可见窗口，后台驻留 → 将结束"),
                FontSize = 12.5,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap
            });
        }
    }
}
