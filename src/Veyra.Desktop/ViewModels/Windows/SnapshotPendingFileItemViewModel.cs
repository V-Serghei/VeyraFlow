using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Veyra.Desktop.Localization;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed class SnapshotPendingFileItemViewModel : ObservableObject
{
    public string RelativePath { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ChangeKind { get; init; } = string.Empty;
    public long CurrentSizeBytes { get; init; }
    public long BaselineSizeBytes { get; init; }

    public string ChangeKindLabel => ChangeKind switch
    {
        "added" => Loc.T("snapshot.change_kind.added"),
        "modified" => Loc.T("snapshot.change_kind.modified"),
        "deleted" => Loc.T("snapshot.change_kind.deleted"),
        _ => Loc.T("change_kind.changed")
    };

    public string SizeDeltaLabel => ChangeKind switch
    {
        "added" => $"{FormatBytes(CurrentSizeBytes)}",
        "modified" => $"{FormatBytes(BaselineSizeBytes)} -> {FormatBytes(CurrentSizeBytes)}",
        "deleted" => $"{FormatBytes(BaselineSizeBytes)}",
        _ => FormatBytes(CurrentSizeBytes)
    };

    public string ComparisonHint
    {
        get
        {
            var delta = CurrentSizeBytes - BaselineSizeBytes;
            var sign = delta switch
            {
                > 0 => "+",
                < 0 => "-",
                _ => string.Empty
            };

            return ChangeKind switch
            {
                "added" => Loc.T("snapshot.hint.added"),
                "deleted" => Loc.T("snapshot.hint.deleted"),
                "modified" => Loc.F("snapshot.hint.modified", $"{sign}{FormatBytes(Math.Abs(delta))}"),
                _ => Loc.T("snapshot.hint.changed")
            };
        }
    }

    public string DiffPreviewPlaceholder => ChangeKind switch
    {
        "added" => Loc.T("snapshot.placeholder.added"),
        "deleted" => Loc.T("snapshot.placeholder.deleted"),
        "modified" => Loc.T("snapshot.placeholder.modified"),
        _ => Loc.T("snapshot.placeholder.unavailable")
    };

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(ChangeKindLabel));
        OnPropertyChanged(nameof(SizeDeltaLabel));
        OnPropertyChanged(nameof(ComparisonHint));
        OnPropertyChanged(nameof(DiffPreviewPlaceholder));
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}
