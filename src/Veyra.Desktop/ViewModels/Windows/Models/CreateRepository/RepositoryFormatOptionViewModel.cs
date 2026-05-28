using CommunityToolkit.Mvvm.ComponentModel;
using Veyra.Application.Common.Files;

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

        return KnownFileExtensions.NormalizeExtension(value) ?? string.Empty;
    }
}
