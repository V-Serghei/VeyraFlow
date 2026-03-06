namespace Veyra.Desktop.ViewModels.Pages.Explorer;

public sealed class RepositorySnapshotFileChangeViewModel
{
    public long SnapshotId { get; init; }
    public long FileIdentityId { get; init; }
    public long FileVersionId { get; init; }
    public string RelativePath { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ChangeKind { get; init; } = string.Empty;
    public long CurrentSizeBytes { get; init; }
    public long PreviousSizeBytes { get; init; }

    public string ChangeKindLabel => ChangeKind switch
    {
        "added" => "+ Added",
        "modified" => "~ Modified",
        "deleted" => "- Deleted",
        _ => ChangeKind
    };

    public string SizeDeltaLabel => ChangeKind switch
    {
        "added" => $"{FormatBytes(CurrentSizeBytes)}",
        "modified" => $"{FormatBytes(PreviousSizeBytes)} -> {FormatBytes(CurrentSizeBytes)}",
        "deleted" => $"{FormatBytes(PreviousSizeBytes)}",
        _ => FormatBytes(CurrentSizeBytes)
    };

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}

