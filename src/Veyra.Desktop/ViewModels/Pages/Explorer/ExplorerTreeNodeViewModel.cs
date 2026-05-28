using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Veyra.Desktop.ViewModels.Pages.Explorer;

public sealed partial class ExplorerTreeNodeViewModel : ObservableObject
{
    public string RelativePath { get; init; } = string.Empty;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    public ObservableCollection<ExplorerTreeNodeViewModel> Children { get; } = [];
}
