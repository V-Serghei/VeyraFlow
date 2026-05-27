using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Veyra.Desktop.Localization;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class ArchiveDiffTreeNodeViewModel : ObservableObject
{
    public ArchiveDiffTreeNodeViewModel(
        string name,
        string entryPath,
        bool isDirectory,
        ArchiveDiffEntryItemViewModel? entry = null)
    {
        Name = name;
        EntryPath = entryPath;
        IsDirectory = isDirectory;
        Entry = entry;
    }

    public string Name { get; }
    public string EntryPath { get; }
    public bool IsDirectory { get; }
    public ArchiveDiffEntryItemViewModel? Entry { get; }
    public ObservableCollection<ArchiveDiffTreeNodeViewModel> Children { get; } = [];

    public string Icon => IsDirectory ? "\uE8B7" : "\uE8A5";
    public bool HasEntry => Entry is not null;
    public string StatusLabel => Entry?.StatusLabel ?? Loc.T("filter.option.folders");
    public string StatusBadgeBackground => Entry?.StatusBadgeBackground ?? "#1C3249";
    public string StatusBadgeForeground => Entry?.StatusBadgeForeground ?? "#CFE3F7";
    public string DetailLabel => Entry?.SizeLabel ?? Loc.F("compare.archive_tree_children", CountLeafEntries(this));

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(DetailLabel));
        foreach (var child in Children)
            child.RefreshLocalization();
    }

    private static int CountLeafEntries(ArchiveDiffTreeNodeViewModel node)
    {
        if (node.Entry is not null)
            return 1;

        var count = 0;
        foreach (var child in node.Children)
            count += CountLeafEntries(child);

        return count;
    }
}
