using System;
using CommunityToolkit.Mvvm.ComponentModel;

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
        if (string.IsNullOrWhiteSpace(e)) return string.Empty;
        e = e.Trim();
        if (!e.StartsWith(".")) e = "." + e;
        return e.ToLowerInvariant();
    }

    public override bool Equals(object? obj)
        => obj is FileExtensionOption other &&
           StringComparer.OrdinalIgnoreCase.Equals(Name, other.Name);

    public override int GetHashCode()
        => StringComparer.OrdinalIgnoreCase.GetHashCode(Name);
}
