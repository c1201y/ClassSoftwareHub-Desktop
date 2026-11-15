using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Views.Palette;






public sealed partial class NumberStepper : UserControl
{
    
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
