using CommunityToolkit.Mvvm.ComponentModel;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class RepositoryFormatOptionViewModel : ObservableObject
{
    public RepositoryFormatOptionViewModel(string format, bool isSelected = true)
    {
        Format = NormalizeFormat(format);
        _isSelected = isSelected;
    }

    public string Format { get; }

    [ObservableProperty] private bool _isSelected;

    public static string NormalizeFormat(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var v = value.Trim();
        if (!v.StartsWith('.'))
            v = "." + v;

        return v.ToLowerInvariant();
    }
}
