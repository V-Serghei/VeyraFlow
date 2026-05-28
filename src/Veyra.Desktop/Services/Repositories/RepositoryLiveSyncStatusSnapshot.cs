using System;

namespace Veyra.Desktop.Services.Repositories;

public enum RepositoryLiveSyncOperationKind
{
    None,
    IncrementalApplied,
    IncrementalNoChanges,
    IncrementalFailed,
    FallbackRequested,
    FallbackCompleted,
    FallbackFailed
}

public sealed record RepositoryLiveSyncOperationSnapshot(
    RepositoryLiveSyncOperationKind Kind,
    bool SaveFileVersions,
    int PathCount,
    int UpdatedCount,
    int RemovedCount,
    string? Reason)
{
    public static RepositoryLiveSyncOperationSnapshot None { get; } =
        new(RepositoryLiveSyncOperationKind.None, false, 0, 0, 0, null);
}

public sealed record RepositoryLiveSyncStatusSnapshot(
    int RepositoryId,
    string RepositoryName,
    string RootPath,
    bool IsActive,
    bool IsPaused,
    bool IsScanRunning,
    bool SaveFileVersions,
    bool IsPathAvailable,
    int QueuePendingCount,
    int QueueRunningCount,
    int QueueTotalCount,
    int QueueMaxRetryCount,
    DateTime LastProcessedUtc,
    string? LastIssue,
    RepositoryLiveSyncOperationSnapshot LastOperation);

public sealed class RepositoryLiveSyncStatusChangedEventArgs(int repositoryId, RepositoryLiveSyncStatusSnapshot? snapshot)
    : EventArgs
{
    public int RepositoryId { get; } = repositoryId;
    public RepositoryLiveSyncStatusSnapshot? Snapshot { get; } = snapshot;
}
