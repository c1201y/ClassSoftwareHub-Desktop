using System;
using ClassSoftwareHub.Desktop.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Controls;

















public sealed partial class ExperimentalBadge : UserControl
{
    public static readonly DependencyProperty FeatureIdProperty = DependencyProperty.Register(
        nameof(FeatureId), typeof(string), typeof(ExperimentalBadge),
        new PropertyMetadata("", OnFeatureIdChanged));

    
    public string FeatureId
    {
        get => (string)GetValue(FeatureIdProperty);
        set => SetValue(FeatureIdProperty, value);
    }

    public ExperimentalBadge()
    {
        InitializeComponent();
        Loaded += (_, _) => Update();
    }

    private static void OnFeatureIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        
        if (d is ExperimentalBadge badge) badge.Update();
    }

    private void Update()
    {
        Visibility = ExperimentalFeatureCatalog.Find(FeatureId) is null
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
