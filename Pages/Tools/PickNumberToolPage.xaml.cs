using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using ClassSoftwareHub.Desktop.Data;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;   
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.UI;

namespace ClassSoftwareHub.Desktop.Pages.Tools;







public sealed partial class PickNumberToolPage : Page
{
    private const int RollTicks = 16;
    private const int FairTotal = 20000;

    private static readonly string[] GroupModeItems = { "按组数（分成 N 组）", "按人数（每组 N 人）" };

    private readonly DispatcherQueueTimer _rollTimer;
    private readonly List<int> _used = new();
    private List<int> _pending = new();
    private int _ticks;
    private bool _ready;
    private bool _updating;   

    
    private readonly List<string> _roster = new();
    private readonly List<string> _usedNames = new();
    private List<string> _pendingNames = new();
    private string _rosterSource = "";   
    private int _savedMode;              
    private string _groupText = "";      

    public PickNumberToolPage()
    {
        InitializeComponent();

        
        
        Core.WheelRelay.Attach(this, DrawView, GroupView);

        _rollTimer = DispatcherQueue.CreateTimer();
        _rollTimer.Interval = TimeSpan.FromMilliseconds(65);
        _rollTimer.IsRepeating = true;
        _rollTimer.Tick += (_, _) => RollTick();

        
        Unloaded += (_, _) => _rollTimer.Stop();

        foreach (var item in GroupModeItems) GroupModeBox.Items.Add(item);
        GroupModeBox.SelectedIndex = 0;

        LoadConfig();

        
        
        if (_savedMode == 1) ModeRosterRadio.IsChecked = true;
        else ModeRangeRadio.IsChecked = true;

        FromBox.ValueChanged += (_, _) => OnSettingChanged();
        ToBox.ValueChanged += (_, _) => OnSettingChanged();
        CountBox.ValueChanged += (_, _) => OnSettingChanged();
        NoRepeatBox.Checked += (_, _) => OnSettingChanged();
        NoRepeatBox.Unchecked += (_, _) => OnSettingChanged();

        _ready = true;
        SyncPoolMode();
        RefreshHints();

        
        TaskDrawRadio.IsChecked = true;
        SyncTask();

        
        GroupModeBox.SelectionChanged += (_, _) => SyncGroupModeHeader();
        SyncGroupModeHeader();
    }

    
    private void SyncGroupModeHeader()
    {
        GroupValueBox.Header = GroupModeBox.SelectedIndex <= 0 ? "组数" : "每组人数";
        RefreshGroupRangeHint();
    }

    
    private void RefreshGroupRangeHint()
    {
        if (UseRoster)
        {
            var total = _roster.Count;
            GroupRangeHint.Text = total > 0
                ? $"抽取池：导入的名单，共 {total} 人。"
                : "抽取池：尚未导入名单，请在上方导入。";
            return;
        }

        GroupRangeHint.Text = PoolSize > 0
            ? $"抽取池：{Lo} ~ {Hi}，共 {PoolSize} 个号码。"
            : "抽取池：请先在上方填写有效的号码范围。";
    }

    
    private void OpenPalette_Click(object sender, RoutedEventArgs e)
        => Views.ToolPaletteWindow.ShowTool("pick-number");

    
    private void TaskMode_Changed(object sender, RoutedEventArgs e) => SyncTask();

    
    private void SyncTask()
    {
        var draw = TaskDrawRadio.IsChecked == true;
        DrawView.Visibility = draw ? Visibility.Visible : Visibility.Collapsed;
        GroupView.Visibility = draw ? Visibility.Collapsed : Visibility.Visible;

        
        
        var view = draw ? DrawView : GroupView;
        if (view.IsLoaded) view.ChangeView(0, 0, null);
    }

    
    private bool UseRoster => ModeRosterRadio.IsChecked == true;

    private void PoolMode_Changed(object sender, RoutedEventArgs e) => OnPoolModeChanged();

    private void OnPoolModeChanged()
    {
        if (!_ready) return;
        SyncPoolMode();
        SaveConfig();
        RefreshHints();
    }

    
    private void SyncPoolMode()
    {
        var roster = UseRoster;
        RangePanel.Visibility = roster ? Visibility.Collapsed : Visibility.Visible;
        RosterPanel.Visibility = roster ? Visibility.Visible : Visibility.Collapsed;

        
        
        
        
        
        Grid.SetRow(BatchPanel, roster ? 1 : 0);
        Grid.SetColumn(BatchPanel, roster ? 0 : 1);
        BatchPanel.Margin = roster ? new Thickness(0, 8, 0, 0) : new Thickness(0);

        
        ToolTipService.SetToolTip(NoRepeatBox, roster ? "抽过的名字不再出现" : "抽过的不再出现");
        RefreshRosterText();
    }

    
    private void RefreshRosterText()
    {
        var has = _roster.Count > 0;
        ClearRosterButton.IsEnabled = has;
        OpenRosterButton.IsEnabled = has;
    }

    
    
    
    
    
    
    
    private async void OpenRoster_Click(object sender, RoutedEventArgs e)
    {
        HideError();
        var dialog = new Views.RosterEditorDialog(_roster, _rosterSource) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();

        var updated = dialog.Result;
        var same = updated.Count == _roster.Count && !updated.Where((n, i) => n != _roster[i]).Any();
        if (same) return;

        _roster.Clear();
        _roster.AddRange(updated);
        _usedNames.Clear();

        SaveConfig();
        RefreshRosterText();
        RefreshHints();
        Toast.Text = $"名单已更新，共 {_roster.Count} 人（抽号记录已重置）。";
    }

    
    private int From => double.IsNaN(FromBox.Value) ? 0 : (int)Math.Floor(FromBox.Value);
    private int To => double.IsNaN(ToBox.Value) ? 0 : (int)Math.Floor(ToBox.Value);
    private int Lo => Math.Min(From, To);
    private int Hi => Math.Max(From, To);
    
    private const int MaxRange = 9999;

    
    
    
    
    
    
    
    
    
    
    private int PoolSize
    {
        get
        {
            var raw = (long)Hi - Lo + 1;
            if (raw <= 0) return 0;
            return (int)Math.Min(raw, MaxRange);
        }
    }

    
    private bool TryUseRange(out int size, out string error)
    {
        var raw = (long)Hi - Lo + 1;
        if (raw <= 1)
        {
            size = 0;
            error = "请填写有效的号码范围（如 1 ~ 50）";
            return false;
        }

        if (raw > MaxRange)
        {
            size = 0;
            error = $"号码范围过大（{raw:N0} 个），本工具上限为 {MaxRange}。请检查起止数字是否输入有误。";
            return false;
        }

        size = (int)raw;
        error = "";
        return true;
    }

    private int WantCount => double.IsNaN(CountBox.Value) ? 1 : Math.Max(1, (int)Math.Floor(CountBox.Value));
    private bool NoRepeat => NoRepeatBox.IsChecked == true;

    private void OnSettingChanged()
    {
        if (!_ready || _updating) return;   
        SaveConfig();
        RefreshHints();
    }

    private int UsedInRange => _used.Where(n => n >= Lo && n <= Hi).Distinct().Count();

    private void RefreshHints()
    {
        _updating = true;
        try { RefreshHintsCore(); }
        finally { _updating = false; }
    }

    
    
    
    
    
    
    
    
    
    private void RefreshHintsCore()
    {
        CountBox.Maximum = Math.Max(1, UseRoster ? _roster.Count : PoolSize);

        if (UseRoster) { RefreshRosterHints(); return; }

        SummaryText.Text = $"本次设置：从 {Lo} ~ {Hi} 中抽取 {WantCount} 个号" + (NoRepeat ? "，抽过的不再出现" : "");

        
        PoolStatusText.Text = PoolSize > 0 ? PoolStatus(PoolSize, UsedInRange, "个号") : "请填写有效的号码范围（如 1 ~ 50）";

        UsedRow.Visibility = _used.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UsedText.Text = _used.Count > 0 ? $"已抽取 {_used.Count} 个：{Head(_used.Select(n => n.ToString()))}" : "";
        RefreshGroupRangeHint();
    }

    private void RefreshRosterHints()
    {
        var total = _roster.Count;
        var used = _usedNames.Count;

        SummaryText.Text = $"本次设置：从名单中抽取 {WantCount} 人" + (NoRepeat ? "，抽过的不再出现" : "");
        PoolStatusText.Text = total == 0
            ? "名单是空的 · 可先单击「示例名单」看一份现成的"
            : PoolStatus(total, used, "人");

        UsedRow.Visibility = used > 0 ? Visibility.Visible : Visibility.Collapsed;
        UsedText.Text = used > 0 ? $"已抽取 {used} 人：{Head(_usedNames)}" : "";
        RefreshGroupRangeHint();
    }

    
    
    
    
    private string PoolStatus(int size, int used, string unit)
    {
        if (!NoRepeat) return $"共 {size} {unit}";
        var remain = Math.Max(0, size - used);
        return remain == 0
            ? $"共 {size} {unit} · 已抽完，如需重新抽取请单击「重置记录」"
            : $"共 {size} {unit} · 已抽 {used} · 剩 {remain}";
    }

    
    private static string Head(IEnumerable<string> items)
    {
        var list = items.ToList();
        var shown = string.Join("、", list.Take(12));
        return list.Count > 12 ? shown + $" 等 {list.Count} 个" : shown;
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

    private void Draw_Click(object sender, RoutedEventArgs e)
    {
        HideError();

        if (UseRoster) { DrawFromRoster(); return; }

        var k = WantCount;
        if (!TryUseRange(out var size, out var rangeError)) { ShowError(rangeError); return; }
        if (k > size) { ShowError($"单次最多抽取 {size} 个号"); return; }
        if (NoRepeat && UsedInRange + k > size)
        {
            ShowError("范围内号码不足，请单击「重置记录」后重新抽取");
            return;
        }

        var final = PickOnce(k);
        if (final is null) { ShowError("抽号失败，请检查范围与数量"); return; }

        _pending = final;
        _ticks = 0;
        DrawButton.IsEnabled = false;
        DrawButton.Content = "抽号中";
        _rollTimer.Start();
    }

    private void DrawFromRoster()
    {
        var k = WantCount;
        var total = _roster.Count;
        if (total == 0) { ShowError("名单是空的，请单击上方的「导入名单」；不清楚格式可先单击「示例名单」。"); return; }
        if (k > total) { ShowError($"名单共 {total} 人，单次最多抽取 {total} 个"); return; }

        List<string> available;
        if (NoRepeat)
        {
            var usedSet = new HashSet<string>(_usedNames, StringComparer.OrdinalIgnoreCase);
            available = _roster.Where(n => !usedSet.Contains(n)).ToList();
            if (available.Count < k)
            {
                ShowError("名单中未抽取的人数不足，请单击「重置记录」后重新抽取");
                return;
            }
        }
        else
        {
            available = _roster.ToList();
        }

        Shuffle(available);
        _pendingNames = available.Take(k).ToList();
        _ticks = 0;
        DrawButton.IsEnabled = false;
        DrawButton.Content = "抽号中";
        _rollTimer.Start();
    }

    
    private static void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = RandInt(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private void RollTick()
    {
        _ticks++;
        var roster = UseRoster;

        if (_ticks >= RollTicks)
        {
            _rollTimer.Stop();
            DrawButton.IsEnabled = true;
            DrawButton.Content = "开始抽号";

            if (roster) ShowNames(_pendingNames, rolling: false);
            else ShowResult(_pending, rolling: false);

            if (NoRepeat)
            {
                if (roster) _usedNames.AddRange(_pendingNames);
                else _used.AddRange(_pending);
                SaveConfig();
                RefreshHints();
            }
            return;
        }

        
        if (roster)
        {
            var total = _roster.Count;
            ShowNames(Enumerable.Range(0, _pendingNames.Count).Select(_ => _roster[RandInt(total)]).ToList(), rolling: true);
        }
        else
        {
            ShowResult(Enumerable.Range(0, _pending.Count).Select(_ => Lo + RandInt(PoolSize)).ToList(), rolling: true);
        }
    }

    
    private const double NumberFontSize = 72;

    
    private const double NameFontSize = 44;

    private void ShowResult(IReadOnlyList<int> numbers, bool rolling)
    {
        var texts = numbers.Select(n => n.ToString()).ToList();
        ShowTexts(texts, NumberFontSize, rolling);
    }

    private void ShowNames(IReadOnlyList<string> names, bool rolling)
    {
        ShowTexts(names.ToList(), NameFontSize, rolling);
    }

    
    
    
    private void ShowTexts(IReadOnlyList<string> texts, double fontSize, bool rolling)
    {
        ResultHost.Children.Clear();
        LayoutStage(texts, fontSize);
        foreach (var t in texts) ResultHost.Children.Add(FitBox(t, fontSize, rolling));
        ResultHint.Visibility = texts.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    
    
    
    
    
    
    
    
    
    
    
    
    private void LayoutStage(IReadOnlyList<string> texts, double fontSize)
    {
        if (texts.Count == 0) return;

        
        var stage = ResultHost.ActualWidth > 0 ? ResultHost.ActualWidth : 872;

        
        var perRow = texts.Count switch
        {
            <= 1 => 1,
            <= 4 => 2,
            <= 6 => 3,
            _ => 4,
        };

        var want = texts.Max(t => EstimateWidth(t, fontSize)) + 20;   
        var room = stage / perRow - 8;                                
        ResultHost.ItemWidth = Math.Max(180, Math.Min(want, room));

        
        
        ResultHost.ItemHeight = Math.Max(112, fontSize * 1.45 + 12);
    }

    
    
    
    
    
    
    private static Viewbox FitBox(string text, double fontSize, bool rolling)
    {
        
        
        
        var tb = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity = rolling ? 0.72 : 1,
            TextWrapping = TextWrapping.NoWrap,
            TextAlignment = TextAlignment.Center,
        };
        Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);

        return new Viewbox
        {
            Margin = new Thickness(10, 6, 10, 6),
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = tb,
        };
    }

    
    private static double EstimateWidth(string text, double fontSize)
    {
        var em = 0.0;
        foreach (var ch in text) em += ch <= 0x7F ? 0.56 : 1.0;
        return em * fontSize;
    }

    private void ResetUsed_Click(object sender, RoutedEventArgs e)
    {
        _used.Clear();
        _usedNames.Clear();
        SaveConfig();
        RefreshHints();
        Toast.Text = "已重置抽号记录";
    }

    
    
    
    
    
    
    
    
    
    private void FairCheck_Click(object sender, RoutedEventArgs e)
    {
        var size = PoolSize;
        if (size <= 1) { Toast.Text = "请先填写有效的号码范围"; return; }

        var buckets = Math.Min(10, size);
        var counts = new int[buckets];
        for (var i = 0; i < FairTotal; i++)
        {
            var v = Lo + RandInt(size);
            var bi = Math.Min(buckets - 1, (int)Math.Floor((v - Lo) / (double)size * buckets));
            counts[bi]++;
        }

        var expect = FairTotal / (double)buckets;
        double chi = 0;
        foreach (var c in counts) chi += Math.Pow(c - expect, 2) / expect;
        var max = counts.Max();

        
        var chart = new Grid { VerticalAlignment = VerticalAlignment.Bottom };
        for (var i = 0; i < buckets; i++)
            chart.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var i = 0; i < buckets; i++)
        {
            var start = Lo + (int)Math.Round((double)i * size / buckets);
            var end = Lo + (int)Math.Round((double)(i + 1) * size / buckets) - 1;
            var label = start == end ? start.ToString() : $"{start}-{end}";
            var pct = max > 0 ? counts[i] / (double)max : 0;

            var col = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Bottom };
            col.Children.Add(new Rectangle
            {
                Height = Math.Max(2, pct * 78),
                RadiusX = 3,
                RadiusY = 3,
                Fill = AccentBrush(),
            });
            col.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 10,
                Opacity = 0.6,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            ToolTipService.SetToolTip(col, $"{label}：{counts[i]} 次");
            Grid.SetColumn(col, i);
            chart.Children.Add(col);
        }

        var normal = chi < 27.9;
        var note = new TextBlock
        {
            Text = $"实抽 {FairTotal:N0} 次，分成 {buckets} 格，每格理论约 {Math.Round(expect)} 次；" +
                   $"实际 {counts.Min()} ~ {counts.Max()} 次（卡方 {chi:0.0}，" +
                   (normal ? "分布正常" : "本次分布略有偏差，可再次抽取") + "）",
            FontSize = 12,
            Opacity = 0.75,
            MaxWidth = 420,
            TextWrapping = TextWrapping.Wrap,
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "在本机实抽 2 万次，检验号码分布是否均匀。",
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 4),
            Height = 124,
            Background = Res("ControlFillColorDefaultBrush"),
            Child = chart,
        });
        panel.Children.Add(note);

        new Flyout { Content = panel, Placement = FlyoutPlacementMode.Top }.ShowAt(FairLink);
    }

    
    
    
    
    
    
    
    private void Group_Click(object sender, RoutedEventArgs e)
    {
        Toast.Text = "";
        if (UseRoster) { GroupRoster(); return; }

        if (!TryUseRange(out var size, out var rangeError)) { Toast.Text = rangeError; return; }
        if (size < 2) { Toast.Text = "至少要 2 个号码才能分组。"; return; }

        var byGroups = GroupModeBox.SelectedIndex <= 0;
        var value = GroupValue;
        var nums = Enumerable.Range(Lo, size).ToList();
        Shuffle(nums);

        var groupCount = Math.Max(1, byGroups ? Math.Min(value, size) : (int)Math.Ceiling(size / (double)value));
        var groups = new List<List<string>>();
        for (var i = 0; i < groupCount; i++) groups.Add(new List<string>());
        for (var i = 0; i < nums.Count; i++) groups[i % groupCount].Add(nums[i].ToString());
        
        foreach (var g in groups) g.Sort((a, b) => int.Parse(a).CompareTo(int.Parse(b)));

        RenderGroups(groups, "个号");
    }

    
    private void GroupRoster()
    {
        var total = _roster.Count;
        if (total == 0) { Toast.Text = "尚未导入名单，请单击「导入名单」。"; return; }
        if (total < 2) { Toast.Text = "名单里至少要有 2 个人才能分组。"; return; }

        var byGroups = GroupModeBox.SelectedIndex <= 0;
        var value = GroupValue;
        var names = _roster.ToList();
        Shuffle(names);

        var groupCount = Math.Max(1, byGroups ? Math.Min(value, total) : (int)Math.Ceiling(total / (double)value));

        var groups = new List<List<string>>();
        for (var g = 0; g < groupCount; g++)
        {
            
            var members = new List<string>();
            for (var i = g; i < names.Count; i += groupCount) members.Add(names[i]);
            members.Sort(StringComparer.CurrentCulture);
            groups.Add(members);
        }

        RenderGroups(groups, "人");
    }

    
    private int GroupValue
        => double.IsNaN(GroupValueBox.Value) ? 1 : Math.Max(1, Math.Min(9999, (int)Math.Floor(GroupValueBox.Value)));

    
    
    
    
    private void RenderGroups(List<List<string>> groups, string unit)
    {
        GroupHint.Visibility = Visibility.Collapsed;
        GroupsHost.Children.Clear();

        var text = new System.Text.StringBuilder();
        var chipWidth = unit == "人" ? 92 : 46;

        for (var i = 0; i < groups.Count; i++)
        {
            var members = groups[i];
            text.Append($"第 {i + 1} 组（{members.Count} {unit}）：")
                .Append(string.Join("、", members))
                .Append('\n');

            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            row.Children.Add(new TextBlock
            {
                Text = $"第 {i + 1} 组 · {members.Count} {unit}",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var chips = new VariableSizedWrapGrid
            {
                Orientation = Orientation.Horizontal,
                ItemWidth = chipWidth,
                ItemHeight = 30,
            };
            foreach (var m in members)
            {
                chips.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 2, 8, 2),
                    Background = Res("ControlFillColorSecondaryBrush"),
                    Child = new TextBlock
                    {
                        Text = m,
                        FontSize = 12,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                });
            }
            Grid.SetColumn(chips, 1);
            row.Children.Add(chips);
            GroupsHost.Children.Add(row);
        }

        _groupText = text.ToString().TrimEnd();
        CopyGroupsButton.IsEnabled = groups.Count > 0;
        Toast.Text = $"已分成 {groups.Count} 组，可反复分组。";
    }

    
    private void CopyGroups_Click(object sender, RoutedEventArgs e)
    {
        if (_groupText.Length == 0) return;
        try
        {
            var dp = new DataPackage();
            dp.SetText(_groupText);
            Clipboard.SetContent(dp);
            Toast.Text = "已复制分组结果";
        }
        catch { Toast.Text = "复制失败"; }
    }

    
    

    private void LoadConfig()
    {
        var cfg = PickNumberConfig.Load();

        FromBox.Value = cfg.From;
        ToBox.Value = cfg.To;
        CountBox.Value = cfg.Count;
        NoRepeatBox.IsChecked = cfg.NoRepeat;
        _used.Clear();
        _used.AddRange(cfg.Used);

        _roster.Clear();
        _roster.AddRange(cfg.Roster ?? new List<string>());
        _usedNames.Clear();
        _usedNames.AddRange(cfg.UsedNames ?? new List<string>());
        _rosterSource = cfg.RosterSource ?? "";
        _savedMode = cfg.Mode;
    }

    private void SaveConfig()
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

    
    
    
    
    private static readonly string[] SampleNames =
    {
        "张伟", "王伟", "王芳", "李伟", "王秀英", "李秀英", "李娜", "张秀英", "刘伟", "张敏",
    };

    private async void SampleRoster_Click(object sender, RoutedEventArgs e)
    {
        HideError();
        var dialog = new Views.RosterSampleDialog(_roster.Count > 0) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();

        if (dialog.UseSampleRequested) UseSampleRoster();
    }

    
    private void UseSampleRoster()
    {
        _roster.Clear();
        _roster.AddRange(SampleNames);
        _usedNames.Clear();          
        _rosterSource = "示例名单";

        if (!UseRoster) ModeRosterRadio.IsChecked = true;

        SaveConfig();
        SyncPoolMode();
        RefreshHints();
        Toast.Text = $"已载入示例名单（{SampleNames.Length} 人）。可单击「开始抽号」，或切到「随机分组」试一次。";
    }

    

    private async void ImportRoster_Click(object sender, RoutedEventArgs e)
    {
        HideError();
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            foreach (var ext in NameRoster.TextExts) picker.FileTypeFilter.Add(ext);
            foreach (var ext in NameRoster.ExcelExts) picker.FileTypeFilter.Add(ext);

            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var names = NameRoster.Read(file.Path);
            if (names.Count == 0)
            {
                ShowError("该文件中未读取到姓名。txt / csv 请每行一个（或用逗号分隔）；"
                          + "Excel 请将姓名放在同一列。需要对照可单击「示例名单」。");
                return;
            }

            _roster.Clear();
            _roster.AddRange(names);
            _usedNames.Clear();          
            _rosterSource = file.Name;

            
            if (!UseRoster) ModeRosterRadio.IsChecked = true;

            SaveConfig();
                SyncPoolMode();
            RefreshHints();
            Toast.Text = $"已导入 {names.Count} 个姓名。";
        }
        catch (Exception ex)
        {
            ShowError("读取名单失败：" + ex.Message);
        }
    }

    private void ClearRoster_Click(object sender, RoutedEventArgs e)
    {
        _roster.Clear();
        _usedNames.Clear();
        _rosterSource = "";
        SaveConfig();
        SyncPoolMode();
        RefreshHints();
        Toast.Text = "已清空名单";
    }

    private Brush Res(string key, Color? fallback = null) => Services.ThemeBrush.Get(this, key);

    private Brush AccentBrush() => Services.ThemeBrush.Get(this, "AccentFillColorDefaultBrush");

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
}
