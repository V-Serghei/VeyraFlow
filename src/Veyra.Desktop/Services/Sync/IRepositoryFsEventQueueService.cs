using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Veyra.Desktop.Services.Sync;

public interface IRepositoryFsEventQueueService
{
    Task EnqueueAsync(int repositoryId, string fullPath, string eventKind, CancellationToken ct = default);
    Task<RepositoryFsEventLease> LeaseAsync(int repositoryId, int maxItems, TimeSpan staleRunningAfter, CancellationToken ct = default);
    Task CompleteAsync(int repositoryId, IReadOnlyCollection<long> itemIds, CancellationToken ct = default);
    Task RequeueAsync(int repositoryId, IReadOnlyCollection<long> itemIds, string? error, CancellationToken ct = default);
    Task<bool> HasPendingAsync(int repositoryId, CancellationToken ct = default);
    Task<RepositoryFsEventQueueStatus> GetStatusAsync(int repositoryId, CancellationToken ct = default);
}

public sealed record RepositoryFsEventLease(
    IReadOnlyList<long> ItemIds,
    int Count)
{
    public static RepositoryFsEventLease Empty { get; } = new(Array.Empty<long>(), 0);
}

public sealed record RepositoryFsEventQueueStatus(
    int PendingCount,
    int RunningCount,
    int TotalCount,
    int MaxRetryCount,
    DateTime? OldestPendingAtUtc,
    DateTime? LastUpdatedAtUtc,
    string? LastError)
{
    public static RepositoryFsEventQueueStatus Empty { get; } = new(0, 0, 0, 0, null, null, null);
}
