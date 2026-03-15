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
    IReadOnlyList<CloudFileVersionDto> FileVersions,
    bool MetadataProtected = false);

public sealed record CloudPushResultDto(
    bool Ok,
    IReadOnlyList<string> MissingBlockHashes);

public sealed record CloudUploadBlockItemDto(
    string BlockHash,
    string SourcePath,
    long ContentLength);

public sealed record CloudBatchUploadResultDto(
    bool Ok,
    int StoredBlocks,
    int SkippedBlocks);

public sealed record CloudStorageSummaryDto(
    long LogicalBlockCount,
    long LogicalBytes,
    long PhysicalObjectCount,
    long PhysicalPayloadBytes,
    long MissingBlockCount,
    long ReducedObjectCount,
    long ReducedObjectPercentFloor);

public sealed record CloudStorageBlockMetricsDto(
    long TotalBlocks,
    long PackedBlocks,
    long LooseBlocks,
    long MissingBlocks,
    long LogicalBytes,
    long PackedBytes,
    long LooseBytes,
    long MissingBytes);

public sealed record CloudStoragePackMetricsDto(
    long TotalPacks,
    long ActivePacks,
    long SealedPacks,
    long BytesWritten,
    long PackedBlockRefs);

public sealed record CloudStorageFilesystemStatsDto(
    long PackFileCount,
    long LooseFileCount,
    long OtherFileCount,
    long PackFileBytes,
    long LooseFileBytes,
    long OtherFileBytes,
    long TotalPhysicalBytes);

public sealed record CloudStorageMetricsDto(
    bool Ok,
    CloudStorageSummaryDto Summary,
    CloudStorageBlockMetricsDto Blocks,
    CloudStoragePackMetricsDto Packs,
    CloudStorageFilesystemStatsDto Filesystem);

public sealed record CloudStorageRepairStatsDto(
    int Scanned,
    int MissingMarked,
    int BrokenLooseRefs,
    int BrokenPackRefs,
    int Compacted);

public sealed record CloudStorageRepairResultDto(
    bool Ok,
    CloudStorageRepairStatsDto Repair,
    CloudStorageMetricsDto Metrics);
