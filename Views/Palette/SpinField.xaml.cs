using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Views.Palette;






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

    
    public string Label
    {
        get => LabelText.Text;
        set => LabelText.Text = value;
    }

    
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

    
    public int Step { get; set; } = 1;

    
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
