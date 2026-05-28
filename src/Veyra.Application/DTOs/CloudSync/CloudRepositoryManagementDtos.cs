namespace Veyra.Application.DTOs;

public sealed record CloudRepositoryManagerOverviewDto(
    string? AccountDisplayName,
    string? AccountEmail,
    string ConnectionState,
    DateTime? LastSuccessfulSyncUtc,
    long CloudStorageUsedBytes,
    int CloudRepositoryCount,
    int CloudSnapshotCount,
    int CloudBlockCount,
    int PendingOperationCount,
    int FailedOperationCount,
    IReadOnlyList<CloudRepositoryManagerRepositoryDto> Repositories,
    IReadOnlyList<CloudRepositoryOperationStatusDto> Operations);

public sealed record CloudRepositoryManagerRepositoryDto(
    int CloudRepositoryId,
    string Name,
    string? OriginalPath,
    string? CurrentLocalPath,
    bool HasLocalLink,
    bool LocalPathExists,
    int SnapshotCount,
    DateTime? LatestSnapshotUtc,
    long CloudSizeBytes,
    string SyncStatus,
    string RestoreStatus,
    string ConflictStatus);

public sealed record CloudRepositoryOperationStatusDto(
    string OperationId,
    int? RepositoryId,
    int? CloudRepositoryId,
    string OperationKind,
    string Status,
    string Phase,
    int Percent,
    long UploadedBytes,
    long DownloadedBytes,
    int UploadedBlocks,
    int DownloadedBlocks,
    int ProcessedSnapshots,
    string? Error);

public sealed record CloudRepositoryRestoreOptionsDto(
    int CloudRepositoryId,
    string? TargetPath,
    string RestoreMode,
    bool RestoreFullHistory,
    bool RestoreLatestSnapshotOnly,
    bool RestoreMetadataOnly,
    bool RelinkExistingLocalFolder,
    string ConflictStrategy,
    bool RestoreToAnotherFolder = false);

public sealed record CloudRepositoryRestorePlanDto(
    int CloudRepositoryId,
    string RepositoryName,
    string? TargetPath,
    int SnapshotCount,
    int BlocksRequired,
    int BlocksAlreadyLocal,
    int BlocksToDownload,
    long BytesToDownload,
    bool TargetPathExists,
    bool HasLocalConflicts,
    IReadOnlyList<CloudRepositoryConflictItemDto> Conflicts,
    string? OriginalCloudPath = null,
    bool WillCreateTargetFolder = false,
    bool WillRelinkExistingFolder = false,
    bool WillRestoreFiles = true,
    bool WillCreateRecoverySnapshot = false,
    string RestorePlanMode = "original_path");

public sealed record CloudRepositoryConflictItemDto(
    string RelativePath,
    string ConflictKind,
    string LocalState,
    string CloudState,
    string RecommendedAction);

public sealed record CloudRepositoryQueuedOperationDto(
    string OperationId,
    string OperationKind,
    int? RepositoryId,
    int? CloudRepositoryId,
    string Status);
