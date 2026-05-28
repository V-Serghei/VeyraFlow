namespace Veyra.Application.DTOs;

public sealed record RepositorySnapshotRestoreDataDto(
    RepositorySnapshotHistoryItemDto Snapshot,
    IReadOnlyList<RepositoryScanEntryDto> Entries,
    IReadOnlyList<FileVersionRestoreDto> FileVersions);
