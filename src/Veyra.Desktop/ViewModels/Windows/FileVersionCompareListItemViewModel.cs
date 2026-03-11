using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Veyra.Desktop.Localization;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class FileVersionCompareListItemViewModel : ObservableObject
{
    public long FileVersionId { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public long SizeBytes { get; init; }
    public bool IsDeletionMarker { get; init; }
    public bool HasContentBlocks { get; init; }

    [ObservableProperty]
    private bool _isSelectedLeft;

    [ObservableProperty]
    private bool _isSelectedRight;

    public string VersionName => $"v{FileVersionId}";
    public string CreatedAtDisplay => CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string SizeDisplay => IsDeletionMarker ? Loc.T("common.deleted") : FormatSize(SizeBytes);

    public bool IsSelectable => HasContentBlocks && !IsDeletionMarker;

    public string StateLabel
    {
        get
        {
            if (IsDeletionMarker)
                return Loc.T("compare.version.deleted_marker");

            if (!HasContentBlocks)
                return Loc.T("compare.version.no_content_blocks");

            return Loc.T("compare.version.ready");
        }
    }

    public bool HasSelectionBadge => IsSelectedLeft || IsSelectedRight;

    public string SelectionBadge => (IsSelectedLeft, IsSelectedRight) switch
    {
        (true, true) => "L/R",
        (true, false) => Loc.T("compare.side.left_short"),
        (false, true) => Loc.T("compare.side.right_short"),
        _ => string.Empty
    };

    public string SelectionBadgeBackground => (IsSelectedLeft, IsSelectedRight) switch
    {
        (true, true) => "#5E3AA8",
        (true, false) => "#1E4F95",
        (false, true) => "#7A3150",
        _ => "#2E4967"
    };

    public string SelectionBadgeForeground => "#F6FAFF";

    public string CardBackground => (IsSelectedLeft, IsSelectedRight) switch
    {
        (true, true) => "#2A3456",
        (true, false) => "#1A3558",
        (false, true) => "#4A243A",
        _ => "#13253A"
    };

    public string CardBorderBrush => (IsSelectedLeft, IsSelectedRight) switch
    {
        (true, true) => "#A39BFF",
        (true, false) => "#78B2FF",
        (false, true) => "#FF9FBE",
        _ => "#365779"
    };

    public string HintLabel => IsSelectable
        ? Loc.T("compare.version.select_hint")
        : Loc.T("compare.version.not_comparable");

    partial void OnIsSelectedLeftChanged(bool value)
        => RaiseSelectionChanged();

    partial void OnIsSelectedRightChanged(bool value)
        => RaiseSelectionChanged();

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelectionBadge));
        OnPropertyChanged(nameof(SelectionBadge));
        OnPropertyChanged(nameof(SelectionBadgeBackground));
        OnPropertyChanged(nameof(SelectionBadgeForeground));
        OnPropertyChanged(nameof(CardBackground));
        OnPropertyChanged(nameof(CardBorderBrush));
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}
