using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClassSoftwareHub.Desktop.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 「程序专杀」页（实验性功能，tag = <c>procguard</c>）。
///
/// 与「白板专杀」是**两个独立功能**（Nick 2026-09-28 明确要求不合并）：
/// 白板专杀盯死白板5 家族，本页盯用户在页面上自行指定的任意进程。
///
/// 逻辑全在 <see cref="ProcessGuard"/>（扫进程 / 判窗口 / 结束 / 定时），
/// 这里只管界面，外加一个 <see cref="_ready"/> 闸门挡住构造期的假事件。
/// </summary>
public sealed partial class ProcessGuardPage : Page
{
    private ProcessGuardConfig _cfg = new();

    /// <summary>
    /// ⚠️ 构造期给 ToggleSwitch 设初值会**触发 Toggled**，那一刻 _cfg 还没铺好，
    ///    回写就把存档冲成默认值了。所以铺完初值再放行（跟白板专杀页同一个套路）。
    /// </summary>
    private bool _ready;

    public ProcessGuardPage()
    {
        InitializeComponent();

        _cfg = ProcessGuardConfig.Load();
        EnableSwitch.IsOn = _cfg.Enabled;                  // ⚠️ 别写进 XAML

        RefreshRules();
        RefreshProcs();
        UpdateHint();

        _ready = true;
    }

    // ── 总开关 ──────────────────────────────────────────────

    private void EnableSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _cfg.Enabled = EnableSwitch.IsOn;
        _cfg.Save();
        UpdateHint();
    }

    private void UpdateHint()
    {
        var armed = _cfg.Rules.Count(r => r.Enabled);
        var withTime = _cfg.Rules.Count(r => r.Enabled && r.Times.Count > 0);

        SwitchHint.Text = _cfg.Enabled
            ? (withTime == 0
                ? "已开启，但还没有可执行的时间点 —— 为程序添加时间点后才会自动结束。"
                : $"已开启：{withTime} 个程序已设时间点，将在每天到达对应时间点时检查。本程序未运行时不会生效。")
            : (armed > 0
                ? "已关闭。开启后，到达各程序设定的时间点将自动结束其后台进程。"
                : "已关闭。添加程序并设定时间点后，可在此开启自动执行。");
    }

    // ── 添加规则 ─────────────────────────────────────────────

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var name = ProcessGuardConfig.NormalizeName(NameBox.Text);

        if (name.Length == 0)
        {
            AddHint.Text = "请输入进程名。";
            return;
        }

        if (_cfg.Rules.Exists(r => string.Equals(
                ProcessGuardConfig.NormalizeName(r.ProcessName), name, StringComparison.OrdinalIgnoreCase)))
        {
            AddHint.Text = $"{name} 已在列表中。";
            return;
        }

        _cfg.Rules.Add(new ProcessGuardRule { ProcessName = name });
        _cfg.Save();
        NameBox.Text = "";

        RefreshRules();
        RefreshProcs();
        UpdateHint();
        AddHint.Text = $"已添加 {name}。请在下方为它设定结束时间。";
    }

    // ── 规则列表 ─────────────────────────────────────────────

    private void RefreshRules()
    {
        RulesHost.Children.Clear();

        if (_cfg.Rules.Count == 0)
        {
            RulesHost.Children.Add(new TextBlock
            {
                Text = "尚未添加程序。在上方输入进程名即可添加。",
                FontSize = 12,
                Opacity = 0.55,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var rule in _cfg.Rules.ToArray())
            RulesHost.Children.Add(BuildRuleCard(rule));
    }

    /// <summary>一条规则 = 一张内嵌卡片（进程名 / 启用 / 删除 / 时间点 / 使用中跳过）。</summary>
    private Border BuildRuleCard(ProcessGuardRule rule)
    {
        var body = new StackPanel { Spacing = 8 };

        // ── 第 1 行：进程名 + 启用 + 删除 ──
        var head = new Grid { ColumnSpacing = 10 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = rule.ProcessName,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };

        var enable = new ToggleSwitch
        {
            IsOn = rule.Enabled,
            OnContent = "启用",
            OffContent = "停用",
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        enable.Toggled += (_, _) =>
        {
            rule.Enabled = enable.IsOn;
            _cfg.Save();
            UpdateHint();
            RefreshProcs();
        };

        var del = new Button
        {
            Content = "删除",
            FontSize = 12,
            Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center
        };
        del.Click += (_, _) =>
        {
            _cfg.Rules.Remove(rule);
            _cfg.Save();
            RefreshRules();
            RefreshProcs();
            UpdateHint();
        };

        Grid.SetColumn(enable, 1);
        Grid.SetColumn(del, 2);
        head.Children.Add(name);
        head.Children.Add(enable);
        head.Children.Add(del);

        // ── 第 2 行：加时间点 ──
        var pickRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var pick = new TimePicker
        {
            ClockIdentifier = "24HourClock",
            MinuteIncrement = 5,
            Time = new TimeSpan(17, 30, 0)
        };
        var addTime = new Button
        {
            Content = "添加时间",
            VerticalAlignment = VerticalAlignment.Center
        };
        addTime.Click += (_, _) =>
        {
            var hhmm = $"{pick.Time.Hours:D2}:{pick.Time.Minutes:D2}";
            if (!rule.Times.Contains(hhmm))
            {
                rule.Times.Add(hhmm);
                rule.Times.Sort(StringComparer.Ordinal);      // 按时间先后排
                _cfg.Save();
                RefreshRules();
                UpdateHint();
            }
        };
        pickRow.Children.Add(new TextBlock
        {
            Text = "结束时间点",
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.8
        });
        pickRow.Children.Add(pick);
        pickRow.Children.Add(addTime);

        // ── 第 3 行：已有时间点 ──
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (rule.Times.Count == 0)
        {
            chips.Children.Add(new TextBlock
            {
                Text = "尚未设定时间点。",
                FontSize = 12,
                Opacity = 0.55,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        else
        {
            foreach (var t in rule.Times.ToArray())
            {
                var chip = new Button
                {
                    Content = t + "  ✕",
                    FontSize = 12,
                    Padding = new Thickness(10, 4, 10, 4),
                    Tag = t
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, $"移除时间点 {t}");
                chip.Click += (_, _) =>
                {
                    rule.Times.Remove(t);
                    _cfg.Save();
                    RefreshRules();
                    UpdateHint();
                };
                chips.Children.Add(chip);
            }
        }

        // ── 第 4 行：正在使用时是否跳过 ──
        var skipRow = new StackPanel { Spacing = 2 };
        var skip = new ToggleSwitch
        {
            IsOn = rule.SkipWhenRunning,
            OnContent = "跳过",
            OffContent = "强制结束",
            MinWidth = 0
        };
        skip.Toggled += (_, _) =>
        {
            rule.SkipWhenRunning = skip.IsOn;
            _cfg.Save();
        };
        skipRow.Children.Add(skip);
        skipRow.Children.Add(new TextBlock
        {
            Text = "程序存在可见窗口（正在使用）时的处理方式：跳过则不结束，等下一个时间点。",
            FontSize = 11.5,
            Opacity = 0.55,
            TextWrapping = TextWrapping.Wrap
        });

        body.Children.Add(head);
        body.Children.Add(pickRow);
        body.Children.Add(chips);
        body.Children.Add(skipRow);

        return new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 12, 14, 12),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush", Microsoft.UI.Colors.Transparent),
            Background = ThemeBrush("CardBackgroundFillColorDefaultBrush", Microsoft.UI.Colors.Transparent),
            Child = body
        };
    }

    /// <summary>
    /// 按资源键取当前主题的画刷。
    /// ⚠️ 这类主题色键**写不进 XAML 的代码构造**，只能在运行时查表（跟带状态徽章的卡片同一个原因）。
    ///    取不到就退回透明色，绝不因为一个画刷把整页拖崩。
    /// </summary>
    private static Brush ThemeBrush(string key, Windows.UI.Color fallback)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b) return b;
        }
        catch { }
        return new SolidColorBrush(fallback);
    }

    // ── 手动执行 ─────────────────────────────────────────────

    private void Check_Click(object sender, RoutedEventArgs e) =>
        Show(ProcessGuard.RunOnce(_cfg, dryRun: true), "仅检测");

    private void Clean_Click(object sender, RoutedEventArgs e)
    {
        var r = ProcessGuard.RunOnce(_cfg, dryRun: false);

        // 手动执行也留一条日志 —— 以后翻 procguard.log 能分清"到点自己跑的"和"手动点的"
        ProcessGuard.Log($"手动结束：{r.Summary}"
            + (r.KilledNames.Count > 0 ? " 结束=" + string.Join("、", r.KilledNames) : "")
            + (r.Errors.Count > 0 ? " 失败=" + string.Join("；", r.Errors) : ""));

        Show(r, "立即结束");
    }

    private void Show(ProcessGuard.GuardResult r, string title)
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

    // ── 当前命中进程 ─────────────────────────────────────────

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshProcs();

    private void RefreshProcs()
    {
        ProcHost.Children.Clear();

        var list = ProcessGuard.ScanAll(_cfg);
        if (list.Count == 0)
        {
            ProcHost.Children.Add(new TextBlock
            {
                Text = "当前未检测到列表中的进程。",
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
