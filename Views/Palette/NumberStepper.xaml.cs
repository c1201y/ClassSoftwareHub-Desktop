using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Views.Palette;

/// <summary>
/// 触屏友好的数字步进器：[-] 数值 [+]
/// 数值用 TextBlock 显示（不是 TextBox），触屏点它不会全选、不会弹复制粘贴菜单；
/// 加减按钮是 RepeatButton，按住不放会连续加减（1 → 50 不用点 49 下）。
/// </summary>
public sealed partial class NumberStepper : UserControl
{
    /// <summary>值变化（含程序里改 Value；改 Minimum/Maximum 触发夹取时也会发）。</summary>
    public event EventHandler? ValueChanged;

    private int _value;
    private int _minimum;
    private int _maximum = 100;
    private int _step = 1;

    public NumberStepper()
    {
        InitializeComponent();
        UpdateState();
    }

    /// <summary>左边那个小标签，比如"范围""个数""分""秒"。</summary>
    public string Label
    {
        get => LabelText.Text;
        set => LabelText.Text = value ?? "";
    }

    public int Minimum
    {
        get => _minimum;
        set { _minimum = value; Value = _value; }
    }

    public int Maximum
    {
        get => _maximum;
        set { _maximum = value; Value = _value; }
    }

    /// <summary>按一下加减多少（至少 1）。</summary>
    public int Step
    {
        get => _step;
        set => _step = Math.Max(1, value);
    }

    public int Value
    {
        get => _value;
        set
        {
            var v = Clamp(value);
            if (v == _value)
            {
                UpdateState();
                return;
            }

            _value = v;
            UpdateState();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private int Clamp(int v)
    {
        if (v < _minimum) v = _minimum;
        if (v > _maximum) v = _maximum;
        return v;
    }

    private void UpdateState()
    {
        ValueText.Text = _value.ToString();
        MinusButton.IsEnabled = _value > _minimum;
        PlusButton.IsEnabled = _value < _maximum;
    }

    private void Minus_Click(object sender, RoutedEventArgs e) => Value = _value - _step;

    private void Plus_Click(object sender, RoutedEventArgs e) => Value = _value + _step;
}
