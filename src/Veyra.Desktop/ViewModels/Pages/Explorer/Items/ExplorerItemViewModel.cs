using CommunityToolkit.Mvvm.ComponentModel;

namespace Veyra.Desktop.ViewModels.Pages.Explorer;

public sealed partial class ExplorerItemViewModel : ObservableObject
{
    public string RelativePath { get; init; } = string.Empty;
    public string? ParentRelativePath { get; init; }
    public bool IsDirectory { get; init; }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _type = string.Empty;
    [ObservableProperty] private string _sizeDisplay = "—";
    [ObservableProperty] private string _modifiedDisplay = string.Empty;
    [ObservableProperty] private string? _hashSha256;

    public string Icon => IsDirectory ? "📁" : "📄";
    public bool CanOpenDirectory => IsDirectory;
}
