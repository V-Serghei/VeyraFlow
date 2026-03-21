namespace Veyra.Desktop.Services.Monitoring.Models;

public sealed record OperationMonitorRepositorySnapshotDto(
    int RepositoryId,
    string Name,
    string StatusCode,
    string? LastStatus,
    string? LastError,
    int PendingQueueCount,
    int RunningQueueCount,
    int RetryQueueCount,
    int ConflictQueueCount,
    int FailedQueueCount,
    int DeadLetterQueueCount,
    bool HasProgress,
    int ProgressCurrent,
    int ProgressTotal,
    global::System.DateTime? ProgressStartedAtUtc,
    global::System.DateTime? ProgressUpdatedAtUtc);
