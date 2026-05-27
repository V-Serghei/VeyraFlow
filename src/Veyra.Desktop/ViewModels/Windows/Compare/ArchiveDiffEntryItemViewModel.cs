using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Veyra.Application.DTOs;
using Veyra.Desktop.Localization;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class ArchiveDiffEntryItemViewModel : ObservableObject
{
    public ArchiveDiffEntryItemViewModel(PendingArchiveEntryDiffDto dto)
    {
        EntryPath = dto.EntryPath;
        ChangeKind = dto.ChangeKind;
        IsDirectory = dto.IsDirectory;
        BaselineSizeBytes = dto.BaselineSizeBytes;
        CurrentSizeBytes = dto.CurrentSizeBytes;
        BaselineCompressedSizeBytes = dto.BaselineCompressedSizeBytes;
        CurrentCompressedSizeBytes = dto.CurrentCompressedSizeBytes;
        BaselineCrc32 = dto.BaselineCrc32;
        CurrentCrc32 = dto.CurrentCrc32;
        BaselineModifiedUtc = dto.BaselineModifiedUtc;
        CurrentModifiedUtc = dto.CurrentModifiedUtc;
    }

    public string EntryPath { get; }
    public PendingArchiveEntryChangeKind ChangeKind { get; }
    public bool IsDirectory { get; }
    public long? BaselineSizeBytes { get; }
    public long? CurrentSizeBytes { get; }
    public long? BaselineCompressedSizeBytes { get; }
    public long? CurrentCompressedSizeBytes { get; }
    public uint? BaselineCrc32 { get; }
    public uint? CurrentCrc32 { get; }
    public DateTimeOffset? BaselineModifiedUtc { get; }
    public DateTimeOffset? CurrentModifiedUtc { get; }

    public string StatusLabel => ChangeKind switch
    {
        PendingArchiveEntryChangeKind.Added => Loc.T("filter.option.added"),
        PendingArchiveEntryChangeKind.Removed => Loc.T("filter.option.removed"),
        PendingArchiveEntryChangeKind.Changed => Loc.T("filter.option.modified"),
        _ => Loc.T("compare.archive.status.unchanged")
    };

    public string TypeLabel => IsDirectory
        ? Loc.T("filter.option.folders")
        : Loc.T("filter.option.files");

    public string SizeLabel => $"{FormatBytes(BaselineSizeBytes)} -> {FormatBytes(CurrentSizeBytes)}";

    public string CompressedSizeLabel => $"{FormatBytes(BaselineCompressedSizeBytes)} -> {FormatBytes(CurrentCompressedSizeBytes)}";

    public string CrcLabel => $"{FormatCrc(BaselineCrc32)} -> {FormatCrc(CurrentCrc32)}";

    public string ModifiedLabel => $"{FormatModified(BaselineModifiedUtc)} -> {FormatModified(CurrentModifiedUtc)}";

    public string StatusBadgeBackground => ChangeKind switch
    {
        PendingArchiveEntryChangeKind.Added => "#173E31",
        PendingArchiveEntryChangeKind.Removed => "#3F1F28",
        PendingArchiveEntryChangeKind.Changed => "#4A3420",
        _ => "#1C3249"
    };

    public string StatusBadgeForeground => ChangeKind switch
    {
        PendingArchiveEntryChangeKind.Added => "#8DFFD0",
        PendingArchiveEntryChangeKind.Removed => "#FF9FB0",
        PendingArchiveEntryChangeKind.Changed => "#FFD89F",
        _ => "#CFE3F7"
    };

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(SizeLabel));
        OnPropertyChanged(nameof(CompressedSizeLabel));
        OnPropertyChanged(nameof(CrcLabel));
        OnPropertyChanged(nameof(ModifiedLabel));
    }

    private static string FormatBytes(long? bytes)
    {
        if (!bytes.HasValue)
            return Loc.T("common.not_available_short");

        var value = bytes.Value;
        if (value < 1024) return $"{value} B";
        if (value < 1024 * 1024) return $"{value / 1024.0:F1} KB";
        if (value < 1024L * 1024 * 1024) return $"{value / (1024.0 * 1024):F1} MB";
        return $"{value / (1024.0 * 1024 * 1024):F1} GB";
    }

    private static string FormatModified(DateTimeOffset? value)
        => value.HasValue
            ? value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : Loc.T("common.not_available_short");

    private static string FormatCrc(uint? value)
        => value.HasValue ? value.Value.ToString("X8") : Loc.T("common.not_available_short");
}
