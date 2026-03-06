namespace Veyra.Desktop.ViewModels.Windows;

public sealed class SnapshotPendingFileItemViewModel
{
    public string RelativePath { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ChangeKind { get; init; } = string.Empty;
    public long CurrentSizeBytes { get; init; }
    public long BaselineSizeBytes { get; init; }

    public string ChangeKindLabel => ChangeKind switch
    {
        "added" => "+ Added",
        "modified" => "~ Modified",
        "deleted" => "- Deleted",
        _ => "Changed"
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
                _ => ""
            };

            return ChangeKind switch
            {
                "added" => "New file in this snapshot.",
                "deleted" => "File will be marked as deleted in this snapshot.",
                "modified" => $"Size delta vs previous version: {sign}{FormatBytes(Math.Abs(delta))}",
                _ => "Change detected."
            };
        }
    }

    public string DiffPreviewPlaceholder => ChangeKind switch
    {
        "added" => "Diff preview placeholder: file has no previous version in repository history.",
        "deleted" => "Diff preview placeholder: file content is absent in current snapshot.",
        "modified" => "Diff preview placeholder: text diff storage is not enabled yet for this file. "
                      + "Planned source: previous file version content vs current on-disk content.",
        _ => "Diff preview placeholder is unavailable for this change type."
    };

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}
