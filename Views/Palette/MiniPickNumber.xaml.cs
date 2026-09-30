using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using ClassSoftwareHub.Desktop.Data;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Views.Palette;

/// <summary>
/// 浮窗版随机抽号：号码 / 名单两种抽法 + 个数 + 是否重复 + 大号结果。
/// ⚠️ 存档和「内置工具 → 随机抽号」是**同一个文件、同一个类型**（<see cref="PickNumberConfig"/>），
/// 所以两边的范围/个数/不重复设置、抽过的号、导入的名单都是通的；每次显示时重新读一遍，避免两边各改各的。
/// 名单本身在工具页导入（浮窗塞不下文件选择那一套），这里只负责抽。
/// </summary>
public sealed partial class MiniPickNumber : UserControl
{
    private const int RollTicks = 10;

    private readonly DispatcherQueueTimer _roll;
    private readonly List<int> _used = new();

    // 名单模式（2026-09-27 加，与工具页共用同一份存档）
    private readonly List<string> _roster = new();
    private readonly List<string> _usedNames = new();
    private string _rosterSource = "";   // 浮窗不显示它，只是替工具页保管 —— 存档是整份覆盖的，丢了就找不回来

    private List<int> _pending = new();
    private List<string> _pendingNames = new();
    private int _ticks;
    private bool _ready;
    private bool _updating;      // 正在刷新提示（里面的夹取会触发 ValueChanged，要忽略掉）

    public MiniPickNumber()
    {
        InitializeComponent();

        _roll = DispatcherQueue.CreateTimer();
        _roll.Interval = TimeSpan.FromMilliseconds(60);
        _roll.IsRepeating = true;
        _roll.Tick += (_, _) => RollTick();

        // 模式切换：事件先挂（此刻 _ready 仍是 false，不会回调），选中在 Reload 里按存档设
        ModeRangeRadio.Checked += (_, _) => OnPoolModeChanged();
        ModeRosterRadio.Checked += (_, _) => OnPoolModeChanged();

        FromStepper.ValueChanged += (_, _) => OnSettingChanged();
        ToStepper.ValueChanged += (_, _) => OnSettingChanged();
        CountStepper.ValueChanged += (_, _) => OnSettingChanged();
        NoRepeatBox.Checked += (_, _) => OnSettingChanged();
        NoRepeatBox.Unchecked += (_, _) => OnSettingChanged();

        _ready = true;
        Reload();
    }

    // 数字输入用 NumberStepper（大按钮、能长按连发、触屏不会全选弹复制）
    private int From => FromStepper.Value;
    private int To => ToStepper.Value;
    private int Lo => Math.Min(From, To);
    private int Hi => Math.Max(From, To);
    private int PoolSize => Hi - Lo + 1;

    private int WantCount => Math.Max(1, CountStepper.Value);
    private bool NoRepeat => NoRepeatBox.IsChecked == true;

    private bool UseRoster => ModeRosterRadio.IsChecked == true;

    private int UsedInRange => _used.Where(n => n >= Lo && n <= Hi).Distinct().Count();

    /// <summary>每次浮窗显示 / 切到这个工具时调一次：重新读存档 + 刷新提示。</summary>
    public void Reload()
    {
        var cfg = PickNumberConfig.Load();

        _ready = false;
        FromStepper.Value = cfg.From;
        ToStepper.Value = cfg.To;
        CountStepper.Value = Math.Clamp(cfg.Count, 1, 50);
        NoRepeatBox.IsChecked = cfg.NoRepeat;
        if (cfg.Mode == 1) ModeRosterRadio.IsChecked = true;
        else ModeRangeRadio.IsChecked = true;
        _ready = true;

        _used.Clear();
        _used.AddRange(cfg.Used);
        _roster.Clear();
        _roster.AddRange(cfg.Roster);
        _usedNames.Clear();
        _usedNames.AddRange(cfg.UsedNames);
        _rosterSource = cfg.RosterSource;

        SyncPoolMode();
        RefreshHints();
    }

    private void OnPoolModeChanged()
    {
        if (!_ready) return;
        SyncPoolMode();
        Save();
        RefreshHints();
    }

    /// <summary>按当前模式收起 / 放出对应的输入行。</summary>
    private void SyncPoolMode()
    {
        var roster = UseRoster;
        RangeRow.Visibility = roster ? Visibility.Collapsed : Visibility.Visible;
        RosterRow.Visibility = roster ? Visibility.Visible : Visibility.Collapsed;

        if (roster)
            RosterInfoText.Text = _roster.Count > 0 ? $"名单：{_roster.Count} 人" : "未导入名单";
    }

    /// <summary>「去导入」：名单在工具页导入，把人送过去（顺手收起浮窗）。</summary>
    private void OpenRosterTool_Click(object sender, RoutedEventArgs e)
    {
        Views.ToolPaletteWindow.HidePalette();
        App.MainWindow?.OpenToolSettings(typeof(Pages.Tools.PickNumberToolPage));
    }

    private void OnSettingChanged()
    {
        if (!_ready || _updating) return;      // _updating：刷新提示时改 Maximum 会夹取 Value，别让它再回头存一遍
        Save();
        RefreshHints();
    }

    private void RefreshHints()
    {
        _updating = true;
        try
        {
            RefreshHintsCore();
        }
        finally
        {
            _updating = false;
        }
    }

    private void RefreshHintsCore()
    {
        ResultText.Opacity = 1;
        DrawButton.Content = WantCount > 1 ? $"抽取 {WantCount} 个" : "抽取";

        if (UseRoster)
        {
            var total = _roster.Count;
            CountStepper.Maximum = Math.Max(1, total);

            if (total == 0)
            {
                HintText.Text = "尚未导入名单。在随机抽号页中选择「导入名单」，导入 Excel 或 txt 文件。";
                UsedText.Text = "";
                return;
            }

            if (NoRepeat)
            {
                var remain = Math.Max(0, total - _usedNames.Count);
                HintText.Text = remain > 0
                    ? $"名单共 {total} 人，剩余 {remain} 人未抽取"
                    : "名单人员已全部抽取，可单击「重置」重新抽取";
                UsedText.Text = _usedNames.Count > 0 ? $"已抽取 {_usedNames.Count}" : "";
            }
            else
            {
                HintText.Text = $"名单共 {total} 人（允许重复）";
                UsedText.Text = _usedNames.Count > 0 ? $"已抽取 {_usedNames.Count}" : "";
            }
            return;
        }

        CountStepper.Maximum = Math.Max(1, PoolSize);

        if (PoolSize <= 1)
        {
            HintText.Text = "请设置号码范围，例如 1 ~ 50";
            UsedText.Text = "";
            return;
        }

        if (NoRepeat)
        {
            var remain = Math.Max(0, PoolSize - UsedInRange);
            HintText.Text = remain > 0
                ? $"范围 {Lo} ~ {Hi}，剩余 {remain} 个未抽取"
                : $"范围 {Lo} ~ {Hi} 已全部抽取，可单击「重置」重新抽取";
            UsedText.Text = UsedInRange > 0 ? $"已抽取 {UsedInRange}" : "";
        }
        else
        {
            HintText.Text = $"范围 {Lo} ~ {Hi}（允许重复）";
            UsedText.Text = _used.Count > 0 ? $"已抽取 {_used.Count}" : "";
        }
    }

    private static int RandInt(int n) => n <= 1 ? 0 : RandomNumberGenerator.GetInt32(n);

    /// <summary>抽 k 个号码（Fisher–Yates，跟完整版页面同一套算法）。</summary>
    private List<int>? PickOnce(int k)
    {
        var size = PoolSize;
        if (size <= 0 || k > size) return null;

        List<int> available;
        if (NoRepeat)
        {
            var usedSet = new HashSet<int>(_used);
            available = Enumerable.Range(Lo, size).Where(n => !usedSet.Contains(n)).ToList();
            if (available.Count < k) return null;
        }
        else
        {
            available = Enumerable.Range(Lo, size).ToList();
        }

        for (var i = available.Count - 1; i > 0; i--)
        {
            var j = RandInt(i + 1);
            (available[i], available[j]) = (available[j], available[i]);
        }
        return available.Take(k).ToList();
    }

    /// <summary>抽 k 个人名（同一套洗牌）。</summary>
    private List<string>? PickNamesOnce(int k)
    {
        if (_roster.Count == 0 || k > _roster.Count) return null;

        List<string> available;
        if (NoRepeat)
        {
            var usedSet = new HashSet<string>(_usedNames, StringComparer.OrdinalIgnoreCase);
            available = _roster.Where(n => !usedSet.Contains(n)).ToList();
            if (available.Count < k) return null;
        }
        else
        {
            available = _roster.ToList();
        }

        for (var i = available.Count - 1; i > 0; i--)
        {
            var j = RandInt(i + 1);
            (available[i], available[j]) = (available[j], available[i]);
        }
        return available.Take(k).ToList();
    }

    private void Draw_Click(object sender, RoutedEventArgs e)
    {
        var k = WantCount;

        if (UseRoster)
        {
            var total = _roster.Count;
            if (total == 0)
            {
                HintText.Text = "尚未导入名单，请先单击「导入名单」";
                return;
            }
            if (k > total)
            {
                HintText.Text = $"名单共 {total} 人";
                return;
            }

            var picked = PickNamesOnce(k);
            if (picked is null)
            {
                HintText.Text = "未抽取人员数量不足，可单击「重置」重新抽取";
                return;
            }

            _pendingNames = picked;
            _ticks = 0;
            DrawButton.IsEnabled = false;
            DrawButton.Content = "抽取中";
            _roll.Start();
            return;
        }

        var size = PoolSize;

        if (size <= 1)
        {
            HintText.Text = "请设置有效的号码范围（例如 1 ~ 50）";
            return;
        }
        if (k > size)
        {
            HintText.Text = $"单次最多抽取 {size} 个";
            return;
        }
        if (NoRepeat && UsedInRange + k > size)
        {
            HintText.Text = "范围内可用号码不足，可单击「重置」重新抽取";
            return;
        }

        var final = PickOnce(k);
        if (final is null)
        {
            HintText.Text = "抽取失败，请检查号码范围";
            return;
        }

        _pending = final;
        _ticks = 0;
        DrawButton.IsEnabled = false;
        DrawButton.Content = "抽取中";
        _roll.Start();
    }

    private void RollTick()
    {
        _ticks++;
        var roster = UseRoster;

        if (_ticks >= RollTicks)
        {
            _roll.Stop();
            DrawButton.IsEnabled = true;
            DrawButton.Content = WantCount > 1 ? $"抽取 {WantCount} 个" : "抽取";

            if (roster)
            {
                ShowNames(_pendingNames, rolling: false);
                if (NoRepeat) _usedNames.AddRange(_pendingNames);
            }
            else
            {
                ShowNumbers(_pending, rolling: false);
                if (NoRepeat) _used.AddRange(_pending);
            }

            if (NoRepeat) Save();
            RefreshHints();
            return;
        }

        if (roster)
        {
            var total = _roster.Count;
            ShowNames(Enumerable.Range(0, _pendingNames.Count).Select(_ => _roster[RandInt(total)]).ToList(), rolling: true);
        }
        else
        {
            ShowNumbers(Enumerable.Range(0, _pending.Count).Select(_ => Lo + RandInt(PoolSize)).ToList(), rolling: true);
        }
    }

    private void ShowNumbers(IReadOnlyList<int> numbers, bool rolling)
    {
        ResultText.Text = string.Join("  ", numbers);
        ResultText.FontSize = numbers.Count switch
        {
            <= 1 => 64,
            2 => 46,
            <= 4 => 36,
            <= 8 => 28,
            _ => 22,
        };
        ResultText.Opacity = rolling ? 0.72 : 1;
    }

    /// <summary>名字比数字占地方，字号整体收一档。</summary>
    private void ShowNames(IReadOnlyList<string> names, bool rolling)
    {
        ResultText.Text = string.Join("  ", names);
        ResultText.FontSize = names.Count switch
        {
            <= 1 => 44,
            2 => 36,
            <= 4 => 30,
            <= 8 => 24,
            _ => 18,
        };
        ResultText.Opacity = rolling ? 0.72 : 1;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _used.Clear();
        _usedNames.Clear();
        Save();
        RefreshHints();
        HintText.Text = "已重置抽取记录";
    }

    private void Save()
    {
        new PickNumberConfig
        {
            From = From,
            To = To,
            Count = WantCount,
            NoRepeat = NoRepeat,
            Used = _used,
            Mode = UseRoster ? 1 : 0,
            Roster = _roster,
            UsedNames = _usedNames,
            RosterSource = _rosterSource,
        }.Save();
    }
}
