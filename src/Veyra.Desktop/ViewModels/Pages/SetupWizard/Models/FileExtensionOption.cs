using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Veyra.Application.Common.Files;

namespace Veyra.Desktop.ViewModels.Pages.SetupWizard;

public sealed partial class FileExtensionOption : ObservableObject
{
    public FileExtensionOption(string name, bool isSelected = false)
    {
        Name = Normalize(name);
        _isSelected = isSelected;
    }

    public string Name { get; }

    [ObservableProperty]
    private bool isSelected;

    private readonly bool _isSelected;

    public override string ToString() => Name;

    private static string Normalize(string e)
    {
        return KnownFileExtensions.NormalizeExtension(e) ?? string.Empty;
    }

    public override bool Equals(object? obj)
        => obj is FileExtensionOption other &&
           StringComparer.OrdinalIgnoreCase.Equals(Name, other.Name);

    public override int GetHashCode()
        => StringComparer.OrdinalIgnoreCase.GetHashCode(Name);
}
