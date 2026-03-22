namespace Veyra.Application.DTOs;

public sealed record RepositoryScanResultDto(
    int TotalEntries,
    int FileEntries,
    int DirectoryEntries,
    string Trigger,
    bool SnapshotCreated = false,
    bool NoChangesDetected = false,
    IReadOnlyList<RepositoryBusyFileDto>? BusyFiles = null,
    bool SkippedBecauseScanInProgress = false)
{
    public IReadOnlyList<RepositoryBusyFileDto> BusyFilesSafe { get; } =
        BusyFiles ?? Array.Empty<RepositoryBusyFileDto>();

    public int BusyFilesCount => BusyFilesSafe.Count;
    public bool HasBusyFiles => BusyFilesCount > 0;
}
