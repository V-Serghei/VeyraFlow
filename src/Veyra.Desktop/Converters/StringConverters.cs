using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Veyra.Desktop.Converters;

public static class StringConverters
{
    public static IValueConverter IsNotNullOrEmpty { get; } = new IsNotNullOrEmptyConverter();
}
