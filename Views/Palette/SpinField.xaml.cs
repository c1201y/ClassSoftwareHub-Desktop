using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Views.Palette;

/// <summary>
/// 紧凑数字输入的代码侧（配合 <see cref="SpinField"/> 的 XAML）。
/// 对外只暴露 <see cref="Value"/> 和 <see cref="ValueChanged"/>，跟旧的 NumberStepper 一样，
/// 调用方（MiniPickNumber / MiniTimer / MiniStopwatch）不用关心内部长什么样。
/// </summary>
public sealed partial class SpinField : UserControl
{
    private int _value;
    private int _min;
    private int _max = 100;
    private bool _updating;

    public SpinField()
    {
        InitializeComponent();
        Refresh();
    }

    /// <summary>字段名称（显示在标签上，如"范围起""个数"）。</summary>
    public string Label
    {
        get => LabelText.Text;
        set => LabelText.Text = value;
    }

    /// <summary>字段单位后缀（可选，如"分""秒"）；为空就不显示。</summary>
    public string? Unit { get; set; }

    public int Minimum
    {
        get => _min;
        set { _min = value; Refresh(); }
    }

    public int Maximum
    {
        get => _max;
        set { _max = Math.Max(_min, value); Refresh(); }
    }

    /// <summary>每次点箭头 +1。</summary>
    public int Step { get; set; } = 1;

    /// <summary>当前值。越界会被夹住（外面代码可以放心直接写）。</summary>
    public int Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, _min, _max);
            if (clamped == _value) { Refresh(); return; }
            _value = clamped;
            Refresh();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ValueChanged;

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        Value = _value + Step;
    }

    private void Down_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        Value = _value - Step;
    }

    /// <summary>
    /// 刷新显示。⚠️ _updating 这一下很关键：外面设置 Maximum 时会夹到当前 Value，
    /// 那是"被动修正"，不该再触发一次 ValueChanged 回头存一遍存档。
    /// </summary>
    private void Refresh()
    {
        _updating = true;
        try
        {
            _value = Math.Clamp(_value, _min, _max);
            ValueText.Text = _value.ToString();
            LabelText.Text = string.IsNullOrWhiteSpace(Unit) ? Label : $"{Label}（{Unit}）";
            UpButton.IsEnabled = _value < _max;
            DownButton.IsEnabled = _value > _min;
        }
        finally
        {
            _updating = false;
        }
    }
}
