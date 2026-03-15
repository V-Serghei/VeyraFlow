using System;
using System.Collections.Generic;

namespace Veyra.Desktop.Services.Monitoring.Models;

public sealed record OperationMonitorProcessSnapshotDto(
    double? CpuPercent,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    long ManagedHeapBytes,
    int ThreadCount,
    int HandleCount,
    DateTime? StartedAtUtc);

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
    DateTime? ProgressStartedAtUtc,
    DateTime? ProgressUpdatedAtUtc);

public sealed record OperationMonitorJournalSnapshotDto(
    long Id,
    DateTime OccurredAtUtc,
    string Level,
    string Category,
    string Action,
    int? RepositoryId,
    string Message,
    string? Details,
    long? DurationMs);

public sealed record OperationMonitorSnapshotDto(
    DateTime GeneratedAtUtc,
    OperationMonitorProcessSnapshotDto Process,
    IReadOnlyList<OperationMonitorRepositorySnapshotDto> ActiveRepositories,
    IReadOnlyList<OperationMonitorJournalSnapshotDto> RecentOperations);
