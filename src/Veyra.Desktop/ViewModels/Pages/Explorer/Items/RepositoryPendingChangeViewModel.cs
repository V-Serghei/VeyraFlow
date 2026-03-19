using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Veyra.Desktop.Localization;

namespace Veyra.Desktop.ViewModels.Pages.Explorer;

public sealed class RepositoryPendingChangeViewModel : ObservableObject
{
    public string RelativePath { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ChangeKind { get; init; } = string.Empty;
    public long CurrentSizeBytes { get; init; }
    public long BaselineSizeBytes { get; init; }

    public string ChangeKindLabel => ChangeKind switch
    {
        "added" => Loc.T("change_kind.added"),
        "modified" => Loc.T("change_kind.modified"),
        "deleted" => Loc.T("change_kind.deleted"),
        _ => Loc.T("change_kind.changed")
    };

    public string ChangeKindColor => ChangeKind switch
    {
        "added" => "#34D399",
        "modified" => "#60A5FA",
        "deleted" => "#F87171",
        _ => "#A3A3A3"
    };

    public string SizeDeltaLabel
    {
        get
        {
            if (ChangeKind == "added")
                return FormatSize(CurrentSizeBytes);

            if (ChangeKind == "deleted")
                return "-";

            var delta = CurrentSizeBytes - BaselineSizeBytes;
            var sign = delta >= 0 ? "+" : "-";
            return $"{sign}{FormatSize(Math.Abs(delta))}";
        }
    }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(ChangeKindLabel));
        OnPropertyChanged(nameof(SizeDeltaLabel));
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}
