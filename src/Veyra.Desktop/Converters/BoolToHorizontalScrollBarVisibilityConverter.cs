using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Veyra.Desktop.Converters;

public sealed class BoolToHorizontalScrollBarVisibilityConverter : IValueConverter
{
    public static readonly BoolToHorizontalScrollBarVisibilityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool wrap
            ? (wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto)
            : AvaloniaProperty.UnsetValue;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
