using Veyra.Application.DTOs.FileVersions;
using Veyra.Application.DTOs.Repository.Scanning;

namespace Veyra.Application.DTOs.Repository.Snapshots;

public sealed record RepositorySnapshotRestoreDataDto(
    RepositorySnapshotHistoryItemDto Snapshot,
    IReadOnlyList<RepositoryScanEntryDto> Entries,
    IReadOnlyList<FileVersionRestoreDto> FileVersions);
