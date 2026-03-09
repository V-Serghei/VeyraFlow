namespace Veyra.Application.DTOs;

public sealed record CloudRepositoryHeaderDto(
    int RepositoryId,
    string Name,
    string? Description,
    long? LatestSnapshotId,
    DateTime? LatestSnapshotCreatedAtUtc,
    string? LatestSnapshotTitle,
    string? LatestSnapshotTrigger,
    int LatestSnapshotFileCount,
    int LatestSnapshotEntryCount);

public sealed record CloudRepositoryMetadataDto(
    int Id,
    string Name,
    string? Description);

public sealed record CloudSnapshotMetadataDto(
    long Id,
    string? Title,
    string Trigger,
    DateTime CreatedAtUtc,
    int TotalEntries,
    int FileEntries,
    int DirectoryEntries,
    long TotalFileBytes,
    string? PayloadSha256);

public sealed record CloudSnapshotEntryDto(
    string RelativePath,
    string? ParentRelativePath,
    string Name,
    bool IsDirectory,
    string? Extension,
    long SizeBytes,
    DateTime LastWriteUtc,
    string? ContentHashSha256);

public sealed record CloudBlockRefDto(
    int Sequence,
    string BlockHash,
    int LengthBytes,
    long StoredSizeBytes);

public sealed record CloudFileVersionDto(
    string RelativePath,
    long FileVersionId,
    string ContentHashSha256,
    long SizeBytes,
    bool IsDeletionMarker,
    DateTime CreatedAtUtc,
    IReadOnlyList<CloudBlockRefDto> Blocks);

public sealed record CloudSnapshotPackageDto(
    CloudRepositoryMetadataDto Repository,
    CloudSnapshotMetadataDto Snapshot,
    IReadOnlyList<CloudSnapshotEntryDto> Entries,
    IReadOnlyList<CloudFileVersionDto> FileVersions);

public sealed record CloudPushResultDto(
    bool Ok,
    IReadOnlyList<string> MissingBlockHashes);
