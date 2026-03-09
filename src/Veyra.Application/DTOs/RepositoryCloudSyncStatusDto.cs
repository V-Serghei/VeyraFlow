namespace Veyra.Application.DTOs;

public sealed record RepositoryCloudSyncStatusDto(
    string ConflictStrategy,
    int RetryMaxAttempts,
    int RetryBaseDelaySeconds,
    DateTime? LastSyncedAtUtc,
    long? LastLocalSnapshotId,
    long? LastRemoteSnapshotId,
    string? LastStatus,
    string? LastError,
    int PendingQueueCount,
    int ConflictQueueCount);
