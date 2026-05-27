using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Veyra.Desktop.Converters;

public sealed class BoolToTextWrappingConverter : IValueConverter
{
    public static readonly BoolToTextWrappingConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool wrap
            ? (wrap ? TextWrapping.Wrap : TextWrapping.NoWrap)
            : AvaloniaProperty.UnsetValue;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
