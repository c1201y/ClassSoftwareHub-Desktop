using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>值为 null → Collapsed，否则 Visible（用于「有图标源才显示 Image」）。</summary>
public sealed class NullToCollapsed : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, string language)
        => value is null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object? parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>值非 null → Collapsed，否则 Visible（用于「SVG 链接要藏起 FontIcon」）。</summary>
public sealed class NotNullToCollapsed : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, string language)
        => value is null ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object? parameter, string language)
        => throw new NotImplementedException();
}
