using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using ClassSoftwareHub.Desktop.Data;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Views.Palette;







public sealed partial class MiniPickNumber : UserControl
{
    private const int RollTicks = 10;

    private readonly DispatcherQueueTimer _roll;
    private readonly List<int> _used = new();

    
    private readonly List<string> _roster = new();
    private readonly List<string> _usedNames = new();
    private string _rosterSource = "";   

    private List<int> _pending = new();
    private List<string> _pendingNames = new();
    private int _ticks;
    private bool _ready;
    private bool _updating;      

    public MiniPickNumber()
    {
        InitializeComponent();

        _roll = DispatcherQueue.CreateTimer();
        _roll.Interval = TimeSpan.FromMilliseconds(60);
        _roll.IsRepeating = true;
        _roll.Tick += (_, _) => RollTick();

        
        
        
        
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

    
    
    private const int Lo = 1;
    private int To => double.IsNaN(ToNumberBox.Value) ? 0 : (int)Math.Floor(ToNumberBox.Value);
    private int Hi => Math.Max(Lo, To);
    private int PoolSize => Hi - Lo + 1;

    private int WantCount => double.IsNaN(CountNumberBox.Value) ? 1 : Math.Max(1, (int)Math.Floor(CountNumberBox.Value));
    private bool NoRepeat => NoRepeatBox.IsOn;

    private bool UseRoster => ReferenceEquals(PoolSelector.SelectedItem, ModeRosterItem);

    private bool? _lastRoster;   
    private bool _pendingRoster; 

    private int UsedInRange => _used.Where(n => n >= Lo && n <= Hi).Distinct().Count();

    
    public void Reload()
    {
        var cfg = PickNumberConfig.Load();

        _ready = false;
        ToNumberBox.Value = cfg.To;                                   
        CountNumberBox.Value = Math.Clamp(cfg.Count, 1, 9999);        
        NoRepeatBox.IsOn = cfg.NoRepeat;
        
        
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
        RefreshHints();   
        Save();
    }

    
    private void SyncPoolMode()
    {
        var roster = UseRoster;
        ToNumberBox.Visibility = roster ? Visibility.Collapsed : Visibility.Visible;
        RosterInfoText.Visibility = roster ? Visibility.Visible : Visibility.Collapsed;
        

        if (roster)
            RosterInfoText.Text = _roster.Count > 0 ? $"名单：{_roster.Count} 人" : "未导入名单";

        
        
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
        if (!_ready || _updating) return;      
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
            From = Lo,        
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
