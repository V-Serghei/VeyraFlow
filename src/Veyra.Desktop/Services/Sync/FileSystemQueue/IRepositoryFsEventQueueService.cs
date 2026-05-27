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
