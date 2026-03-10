using System;
using CommunityToolkit.Mvvm.ComponentModel;

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
    public string SizeDisplay => IsDeletionMarker ? "deleted" : FormatSize(SizeBytes);

    public bool IsSelectable => HasContentBlocks && !IsDeletionMarker;

    public string StateLabel
    {
        get
        {
            if (IsDeletionMarker)
                return "deleted marker";

            if (!HasContentBlocks)
                return "no content blocks";

            return "ready";
        }
    }

    public bool HasSelectionBadge => IsSelectedLeft || IsSelectedRight;

    public string SelectionBadge => (IsSelectedLeft, IsSelectedRight) switch
    {
        (true, true) => "L/R",
        (true, false) => "LEFT",
        (false, true) => "RIGHT",
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
        ? "Left-click: pick LEFT side, Right-click: pick RIGHT side"
        : "This version cannot be compared.";

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
