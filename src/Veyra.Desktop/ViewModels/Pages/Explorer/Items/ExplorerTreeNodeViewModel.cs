using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Veyra.Desktop.ViewModels.Pages.Explorer;

public sealed partial class ExplorerTreeNodeViewModel : ObservableObject
{
    public string RelativePath { get; init; } = string.Empty;
    public bool IsPlaceholder { get; init; }
    public Action<ExplorerTreeNodeViewModel, bool>? ExpansionChanged { get; init; }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _hasUnrealizedChildren;

    public ObservableCollection<ExplorerTreeNodeViewModel> Children { get; } = [];

    partial void OnIsExpandedChanged(bool value)
    {
        if (!IsPlaceholder)
            ExpansionChanged?.Invoke(this, value);
    }
}
