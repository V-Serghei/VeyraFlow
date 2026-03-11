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
    int ConflictQueueCount,
    int RunningQueueCount = 0,
    int RetryQueueCount = 0,
    int DeadLetterQueueCount = 0,
    int FailedQueueCount = 0,
    int CompletedQueueCount = 0);
