using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace ClassSoftwareHub.Desktop.Core;


public sealed class NullToCollapsed : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, string language)
        => value is null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object? parameter, string language)
        => throw new NotImplementedException();
}


public sealed class NotNullToCollapsed : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, string language)
        => value is null ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object? parameter, string language)
        => throw new NotImplementedException();
}
