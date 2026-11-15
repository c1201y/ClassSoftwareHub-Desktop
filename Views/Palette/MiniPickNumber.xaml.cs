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

        // 座号 / 名单：事件在 XAML 上挂；选中在 Reload 里按存档拨
        // ⚠️ SelectorBar 在 Loaded 时会**自动选中第一项**，构造函数里拨的选中会被它盖乱
        //    （高亮一个、下划线在另一个上，Nick 截图骂的就是这个脱节）。
        //    所以加载后先等它挑完，再排一次队把存档的模式拨回去。
        PoolSelector.Loaded += (_, _) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _ready = false;
            PoolSelector.SelectedItem = _pendingRoster ? ModeRosterItem : ModeRangeItem;
            _ready = true;
        });

        ToNumberBox.ValueChanged += (_, _) => OnSettingChanged();
        CountNumberBox.ValueChanged += (_, _) => OnSettingChanged();
        NoRepeatBox.Toggled += (_, _) => OnSettingChanged();

        _ready = true;
        Reload();
    }

    private void PoolSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        OnPoolModeChanged();
    }

    // 数字输入用 NumberBox（Inline 自增钮，触屏点得到；Nick 2026-10-03 换掉原来的小号自增钮）。
    // 起始座号已砍：座号固定从 1 号开始，只留「终止范围」。
    private const int Lo = 1;
    private int To => double.IsNaN(ToNumberBox.Value) ? 0 : (int)Math.Floor(ToNumberBox.Value);
    private int Hi => Math.Max(Lo, To);
    private int PoolSize => Hi - Lo + 1;

    private int WantCount => double.IsNaN(CountNumberBox.Value) ? 1 : Math.Max(1, (int)Math.Floor(CountNumberBox.Value));
    private bool NoRepeat => NoRepeatBox.IsOn;

    private bool UseRoster => ReferenceEquals(PoolSelector.SelectedItem, ModeRosterItem);

    private bool? _lastRoster;   // 上一次的模式（null=刚加载）：真换了模式才清上一把的结果
    private bool _pendingRoster; // 想要的模式：SelectorBar 加载完成后的排队回调按它拨

    private int UsedInRange => _used.Where(n => n >= Lo && n <= Hi).Distinct().Count();

    /// <summary>每次浮窗显示 / 切到这个工具时调一次：重新读存档 + 刷新提示。</summary>
    public void Reload()
    {
        var cfg = PickNumberConfig.Load();

        _ready = false;
        ToNumberBox.Value = cfg.To;                                   // 终止范围
        CountNumberBox.Value = Math.Clamp(cfg.Count, 1, 9999);        // 先给个宽上限，精确上限交给 RefreshHints 按模式夹
        NoRepeatBox.IsOn = cfg.NoRepeat;
        // ⚠️ 拨选中一律走 SelectedItem 这个官方入口。之前直接摆弄 SelectorBarItem.IsSelected
        //    （"先清两个再选一个"），结果高亮和下划线会分家 —— 看着像座号、实际选的是名单。
        _pendingRoster = cfg.Mode == 1;
        PoolSelector.SelectedItem = _pendingRoster ? ModeRosterItem : ModeRangeItem;
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
        RefreshHints();   // 先按模式把「抽取个数」的上限夹好（超了会顺手把 Value 夹进去），再存
        Save();
    }

    /// <summary>按当前模式收起 / 放出对应的输入项。</summary>
    private void SyncPoolMode()
    {
        var roster = UseRoster;
        ToNumberBox.Visibility = roster ? Visibility.Collapsed : Visibility.Visible;
        RosterInfoText.Visibility = roster ? Visibility.Visible : Visibility.Collapsed;
        // 抽取个数两种模式共用同一个框，只是上限不同（RefreshHints 里按模式夹）

        if (roster)
            RosterInfoText.Text = _roster.Count > 0 ? $"名单：{_roster.Count} 人" : "未导入名单";

        // 真换了模式才清上一把的结果 —— 座号页面挂着个"李伟"会让人以为语义串了。
        // 刚加载（_lastRoster 为 null）不清：浮窗收起再打开，上次抽的结果还在。
        if (_lastRoster is bool prev && prev != roster)
        {
            ResultText.Text = "—";
            ResultText.FontSize = 64;
            ResultText.Opacity = 1;
        }
        _lastRoster = roster;
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
            CountNumberBox.Maximum = Math.Max(1, total);

            if (total == 0)
            {
                HintText.Text = "尚未导入名单。在「内置工具 → 随机抽号」里导入 Excel 或 txt 文件。";
                return;
            }

            HintText.Text = NoRepeat
                ? (total - _usedNames.Count > 0
                    ? $"名单共 {total} 人，剩余 {total - _usedNames.Count} 人未抽取"
                    : "名单人员已全部抽取，可点「重置」重新抽取")
                : $"名单共 {total} 人（允许重复）";
            return;
        }

        CountNumberBox.Maximum = Math.Max(1, PoolSize);

        if (PoolSize <= 1)
        {
            HintText.Text = "请设置座号范围，例如 1 ~ 50";
            return;
        }

        HintText.Text = NoRepeat
            ? (PoolSize - UsedInRange > 0
                ? $"座号 {Lo} ~ {Hi}，剩余 {PoolSize - UsedInRange} 个未抽取"
                : $"座号 {Lo} ~ {Hi} 已全部抽取，可点「重置」重新抽取")
            : $"座号 {Lo} ~ {Hi}（允许重复）";
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

    private void NoRepeat_Toggled(object sender, RoutedEventArgs e) => OnSettingChanged();

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

    /// <summary>
    /// 结果写成一行。字号先按数量收一档，再由外层 Viewbox（DownOnly）兜底：
    /// 浮窗窄、内容长的时候整体等比缩小，保证**单行、不折行、不被裁**（2026-10-05 群反馈）。
    /// </summary>
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
            From = Lo,        // 起始固定 1 号（座号从 1 开始）
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
