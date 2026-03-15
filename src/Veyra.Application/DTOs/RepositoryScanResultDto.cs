namespace Veyra.Application.DTOs;

public sealed record RepositoryScanResultDto(
    int TotalEntries,
    int FileEntries,
    int DirectoryEntries,
    string Trigger,
    bool SnapshotCreated = false,
    bool NoChangesDetected = false);
