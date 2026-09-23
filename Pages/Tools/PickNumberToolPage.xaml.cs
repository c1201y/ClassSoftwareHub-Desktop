using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>
/// 随机抽号（对齐网页版 tools/PickNumberTool.vue）：
/// 加密随机抽号（Fisher–Yates）+ 滚动动画 + 不重复记录（本地存档）+ 公平性自检（2 万次直方图 + 卡方）+ 随机分组。
/// </summary>
public sealed partial class PickNumberToolPage : Page
{
    private const int RollTicks = 16;
    private const int FairTotal = 20000;

    private static readonly string StorePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassSoftwareHub", "pick-number.json");

    private static readonly string[] GroupModeItems = { "按组数（分成 N 组）", "按人数（每组 N 人）" };

    private readonly DispatcherQueueTimer _rollTimer;
    private readonly List<int> _used = new();
    private List<int> _pending = new();
    private int _ticks;
    private bool _ready;

    public PickNumberToolPage()
    {
        InitializeComponent();

        _rollTimer = DispatcherQueue.CreateTimer();
        _rollTimer.Interval = TimeSpan.FromMilliseconds(65);
        _rollTimer.IsRepeating = true;
        _rollTimer.Tick += (_, _) => RollTick();

        foreach (var item in GroupModeItems) GroupModeBox.Items.Add(item);
        GroupModeBox.SelectedIndex = 0;

        LoadConfig();

        FromBox.ValueChanged += (_, _) => OnSettingChanged();
        ToBox.ValueChanged += (_, _) => OnSettingChanged();
        CountBox.ValueChanged += (_, _) => OnSettingChanged();
        NoRepeatBox.Checked += (_, _) => OnSettingChanged();
        NoRepeatBox.Unchecked += (_, _) => OnSettingChanged();

        _ready = true;
        RefreshHints();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(ToolsPage));
    }

    // ══════════ 设置 ══════════
    private int From => double.IsNaN(FromBox.Value) ? 0 : (int)Math.Floor(FromBox.Value);
    private int To => double.IsNaN(ToBox.Value) ? 0 : (int)Math.Floor(ToBox.Value);
    private int Lo => Math.Min(From, To);
    private int Hi => Math.Max(From, To);
    private int PoolSize => Hi - Lo + 1;
    private int WantCount => double.IsNaN(CountBox.Value) ? 1 : Math.Max(1, (int)Math.Floor(CountBox.Value));
    private bool NoRepeat => NoRepeatBox.IsChecked == true;

    private void OnSettingChanged()
    {
        if (!_ready) return;
        SaveConfig();
        RefreshHints();
    }

    private int UsedInRange => _used.Where(n => n >= Lo && n <= Hi).Distinct().Count();

    private void RefreshHints()
    {
        SummaryText.Text = $"本次设置：在 {Lo} ~ {Hi} 里抽 {WantCount} 个号" + (NoRepeat ? "，抽过的不再出现" : "");

        if (NoRepeat)
        {
            var remain = Math.Max(0, PoolSize - UsedInRange);
            RemainText.Text = $"范围内还剩 {remain} 个号没抽过"
                              + (UsedInRange > 0 ? $"（已抽 {UsedInRange} 个）" : "")
                              + (remain == 0 ? " —— 想再来一轮请点「重置记录」" : "");
            RemainText.Visibility = Visibility.Visible;
        }
        else
        {
            RemainText.Visibility = Visibility.Collapsed;
        }

        UsedRow.Visibility = _used.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UsedText.Text = _used.Count > 0 ? $"已抽 {_used.Count} 个：{string.Join("、", _used)}" : "";
        GroupRangeHint.Text = $"范围沿用上面的 {Lo} ~ {Hi}";
    }

    // ══════════ 抽号 ══════════
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
        var k = WantCount;
        var size = PoolSize;
        if (size <= 1) { ShowError("请填写有效的号码范围（如 1 ~ 50）"); return; }
        if (k > size) { ShowError($"一次最多抽 {size} 个号"); return; }
        if (NoRepeat && UsedInRange + k > size)
        {
            ShowError("范围内号码不够了，点「重置记录」再来一轮");
            return;
        }

        var final = PickOnce(k);
        if (final is null) { ShowError("抽号失败，请检查范围与数量"); return; }

        _pending = final;
        _ticks = 0;
        DrawButton.IsEnabled = false;
        DrawButton.Content = "抽号中…";
        _rollTimer.Start();
    }

    private void RollTick()
    {
        _ticks++;
        if (_ticks >= RollTicks)
        {
            _rollTimer.Stop();
            DrawButton.IsEnabled = true;
            DrawButton.Content = "开始抽号";
            ShowResult(_pending, rolling: false);
            if (NoRepeat)
            {
                _used.AddRange(_pending);
                SaveConfig();
                RefreshHints();
            }
            return;
        }
        ShowResult(Enumerable.Range(0, _pending.Count).Select(_ => Lo + RandInt(PoolSize)).ToList(), rolling: true);
    }

    private void ShowResult(IReadOnlyList<int> numbers, bool rolling)
    {
        ResultHost.Children.Clear();
        foreach (var n in numbers)
        {
            ResultHost.Children.Add(new TextBlock
            {
                Text = n.ToString(),
                FontSize = 42,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = rolling ? 0.72 : 1,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        ResultHint.Visibility = numbers.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ResetUsed_Click(object sender, RoutedEventArgs e)
    {
        _used.Clear();
        SaveConfig();
        RefreshHints();
        Toast.Text = "已重置抽号记录";
    }

    // ══════════ 公平性自检 ══════════
    private void FairCheck_Click(object sender, RoutedEventArgs e)
    {
        var size = PoolSize;
        if (size <= 1) { Toast.Text = "请先填有效的号码范围"; return; }

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

        FairChart.Children.Clear();
        FairChart.ColumnDefinitions.Clear();
        for (var i = 0; i < buckets; i++)
            FairChart.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

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
            var labelText = new TextBlock
            {
                Text = label,
                FontSize = 10,
                Opacity = 0.6,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            col.Children.Add(labelText);
            ToolTipService.SetToolTip(col, $"{label}：{counts[i]} 次");
            Grid.SetColumn(col, i);
            FairChart.Children.Add(col);
        }

        var normal = chi < 27.9;
        FairNote.Text = $"实抽 {FairTotal:N0} 次，分成 {buckets} 格，每格理论约 {Math.Round(expect)} 次；" +
                        $"实际 {counts.Min()} ~ {counts.Max()} 次（卡方 {chi:0.0}，" +
                        (normal ? "分布正常" : "这次偏了一点，再点一次看看") + "）";
        FairPanel.Visibility = Visibility.Visible;
    }

    // ══════════ 分组 ══════════
    private void Group_Click(object sender, RoutedEventArgs e)
    {
        var size = PoolSize;
        if (size <= 1) { Toast.Text = "请填写有效的号码范围"; return; }

        var value = double.IsNaN(GroupValueBox.Value) ? 1 : Math.Max(1, (int)Math.Floor(GroupValueBox.Value));
        var nums = Enumerable.Range(Lo, size).ToList();
        for (var i = nums.Count - 1; i > 0; i--)
        {
            var j = RandInt(i + 1);
            (nums[i], nums[j]) = (nums[j], nums[i]);
        }

        var byGroups = GroupModeBox.SelectedIndex <= 0;
        var groupCount = byGroups ? Math.Min(value, size) : (int)Math.Ceiling(size / (double)value);
        var groups = new List<List<int>>();
        for (var i = 0; i < groupCount; i++) groups.Add(new List<int>());
        for (var i = 0; i < nums.Count; i++) groups[i % groupCount].Add(nums[i]);
        foreach (var g in groups) g.Sort();

        GroupHint.Visibility = Visibility.Collapsed;
        GroupsHost.Children.Clear();
        for (var i = 0; i < groups.Count; i++)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            row.Children.Add(new TextBlock
            {
                Text = $"第 {i + 1} 组 · {groups[i].Count} 人",
                FontSize = 12.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var chips = new VariableSizedWrapGrid
            {
                Orientation = Orientation.Horizontal,
                ItemWidth = 46,
                ItemHeight = 30,
            };
            foreach (var n in groups[i])
            {
                chips.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 2, 8, 2),
                    Background = Res("ControlFillColorSecondaryBrush"),
                    Child = new TextBlock { Text = n.ToString(), FontSize = 13 },
                });
            }
            Grid.SetColumn(chips, 1);
            row.Children.Add(chips);
            GroupsHost.Children.Add(row);
        }
    }

    // ══════════ 本地存档 + 小工具 ══════════
    private sealed class Config
    {
        public int From { get; set; } = 1;
        public int To { get; set; } = 50;
        public int Count { get; set; } = 1;
        public bool NoRepeat { get; set; } = true;
        public List<int> Used { get; set; } = new();
    }

    private void LoadConfig()
    {
        var cfg = new Config();
        try
        {
            if (File.Exists(StorePath))
                cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(StorePath)) ?? new Config();
        }
        catch { /* 存档坏了就用默认值 */ }

        FromBox.Value = cfg.From;
        ToBox.Value = cfg.To;
        CountBox.Value = cfg.Count;
        NoRepeatBox.IsChecked = cfg.NoRepeat;
        _used.Clear();
        _used.AddRange(cfg.Used);
    }

    private void SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(StorePath)!);
            var cfg = new Config
            {
                From = From,
                To = To,
                Count = WantCount,
                NoRepeat = NoRepeat,
                Used = _used,
            };
            File.WriteAllText(StorePath, JsonSerializer.Serialize(cfg));
        }
        catch { /* 存不上不影响使用 */ }
    }

    private static Brush Res(string key, Color? fallback = null)
    {
        try { return (Brush)Application.Current.Resources[key]; }
        catch { return new SolidColorBrush(fallback ?? Color.FromArgb(255, 0, 103, 192)); }
    }

    private static Brush AccentBrush() => Res("AccentFillColorDefaultBrush");

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
}
