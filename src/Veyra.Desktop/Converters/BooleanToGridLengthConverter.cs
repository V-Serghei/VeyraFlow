using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Veyra.Desktop.Converters;

public sealed class BooleanToGridLengthConverter : IValueConverter
{
    public static readonly BooleanToGridLengthConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isVisible = value is true;
        if (!isVisible)
            return new GridLength(0, GridUnitType.Pixel);

        if (parameter is string text)
        {
            var normalized = text.Trim();
            if (string.Equals(normalized, "Auto", StringComparison.OrdinalIgnoreCase))
                return GridLength.Auto;

            if (normalized.EndsWith('*'))
            {
                var weightText = normalized[..^1];
                if (string.IsNullOrWhiteSpace(weightText))
                    return new GridLength(1, GridUnitType.Star);

                if (double.TryParse(weightText, NumberStyles.Float, CultureInfo.InvariantCulture, out var weight))
                    return new GridLength(Math.Max(0, weight), GridUnitType.Star);
            }

            if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var pixels))
                return new GridLength(Math.Max(0, pixels), GridUnitType.Pixel);
        }

        return GridLength.Auto;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => AvaloniaProperty.UnsetValue;
}
