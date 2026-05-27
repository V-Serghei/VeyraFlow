namespace Veyra.Application.DTOs;

public sealed record GlobalSearchIndexLoadDto(
    IReadOnlyList<RepositoryDto> Repositories,
    IReadOnlyList<GlobalSearchRepositoryEntriesDto> RepositoryEntries,
    IReadOnlyList<GlobalSearchRepositorySnapshotsDto> RepositorySnapshots,
    IReadOnlyList<GlobalSearchTaggedFileDto>? TaggedFiles = null);

public sealed record GlobalSearchRepositoryEntriesDto(
    int RepositoryId,
    IReadOnlyList<RepositoryScanEntryDto> Entries);

public sealed record GlobalSearchRepositorySnapshotsDto(
    int RepositoryId,
    IReadOnlyList<RepositorySnapshotHistoryItemDto> Snapshots);

public sealed record GlobalSearchTaggedFileDto(
    int RepositoryId,
    string RelativePath,
    IReadOnlyList<string> Tags);
