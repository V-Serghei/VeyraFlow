using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Veyra.Desktop.Converters;

internal sealed class IsNotNullOrEmptyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s)
            return !string.IsNullOrWhiteSpace(s);

        return value is not null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => AvaloniaProperty.UnsetValue;
}
