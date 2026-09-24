using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Views.Palette;

/// <summary>
/// 浮窗版随机抽号：范围 + 个数 + 是否重复 + 大号结果。
/// ⚠️ 存档和「内置工具 → 随机抽号」是**同一个文件**（%LOCALAPPDATA%\ClassSoftwareHub\pick-number.json），
/// 所以两边的范围/个数/不重复设置和"抽过的号"是通的；每次显示时重新读一遍，避免两边各改各的。
/// </summary>
public sealed partial class MiniPickNumber : UserControl
{
    private const int RollTicks = 10;

    private static readonly string StorePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassSoftwareHub", "pick-number.json");

    private readonly DispatcherQueueTimer _roll;
    private readonly List<int> _used = new();

    private List<int> _pending = new();
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

    private int UsedInRange => _used.Where(n => n >= Lo && n <= Hi).Distinct().Count();

    /// <summary>每次浮窗显示 / 切到这个工具时调一次：重新读存档 + 刷新提示。</summary>
    public void Reload()
    {
        var cfg = new Config();
        try
        {
            if (File.Exists(StorePath))
                cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(StorePath)) ?? new Config();
        }
        catch { /* 存档坏了就用默认值 */ }

        _ready = false;
        FromStepper.Value = cfg.From;
        ToStepper.Value = cfg.To;
        CountStepper.Value = Math.Clamp(cfg.Count, 1, 50);
        NoRepeatBox.IsChecked = cfg.NoRepeat;
        _ready = true;

        _used.Clear();
        _used.AddRange(cfg.Used);
        RefreshHints();
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
        DrawButton.Content = WantCount > 1 ? $"抽 {WantCount} 个" : "抽一个";
        CountStepper.Maximum = Math.Max(1, PoolSize);

        if (PoolSize <= 1)
        {
            HintText.Text = "填一下号码范围，比如 1 ~ 50";
            UsedText.Text = "";
            return;
        }

        if (NoRepeat)
        {
            var remain = Math.Max(0, PoolSize - UsedInRange);
            HintText.Text = remain > 0
                ? $"范围 {Lo} ~ {Hi}，还剩 {remain} 个没抽过"
                : $"范围 {Lo} ~ {Hi} 都抽完了，点「重置」再来一轮";
            UsedText.Text = UsedInRange > 0 ? $"已抽 {UsedInRange}" : "";
        }
        else
        {
            HintText.Text = $"范围 {Lo} ~ {Hi}（允许重复）";
            UsedText.Text = _used.Count > 0 ? $"已抽 {_used.Count}" : "";
        }
    }

    private static int RandInt(int n) => n <= 1 ? 0 : RandomNumberGenerator.GetInt32(n);

    /// <summary>抽 k 个（Fisher–Yates，跟完整版页面同一套算法）。</summary>
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
        var k = WantCount;
        var size = PoolSize;

        if (size <= 1)
        {
            HintText.Text = "请先填有效的号码范围（如 1 ~ 50）";
            return;
        }
        if (k > size)
        {
            HintText.Text = $"一次最多抽 {size} 个";
            return;
        }
        if (NoRepeat && UsedInRange + k > size)
        {
            HintText.Text = "范围内的号码不够了，点「重置」再来一轮";
            return;
        }

        var final = PickOnce(k);
        if (final is null)
        {
            HintText.Text = "抽号失败，检查一下范围";
            return;
        }

        _pending = final;
        _ticks = 0;
        DrawButton.IsEnabled = false;
        DrawButton.Content = "抽号中…";
        _roll.Start();
    }

    private void RollTick()
    {
        _ticks++;
        if (_ticks >= RollTicks)
        {
            _roll.Stop();
            DrawButton.IsEnabled = true;
            DrawButton.Content = WantCount > 1 ? $"抽 {WantCount} 个" : "抽一个";

            ShowNumbers(_pending, rolling: false);
            if (NoRepeat)
            {
                _used.AddRange(_pending);
                Save();
            }
            RefreshHints();
            return;
        }

        var rolling = Enumerable.Range(0, _pending.Count).Select(_ => Lo + RandInt(PoolSize)).ToList();
        ShowNumbers(rolling, rolling: true);
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

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _used.Clear();
        Save();
        RefreshHints();
        HintText.Text = "已重置抽号记录";
    }

    private sealed class Config
    {
        public int From { get; set; } = 1;
        public int To { get; set; } = 50;
        public int Count { get; set; } = 1;
        public bool NoRepeat { get; set; } = true;
        public List<int> Used { get; set; } = new();
    }

    private void Save()
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
}
