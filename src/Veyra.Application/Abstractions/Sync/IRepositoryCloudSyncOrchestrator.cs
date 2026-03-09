namespace Veyra.Application.Abstractions.Sync;

public interface IRepositoryCloudSyncOrchestrator
{
    Task TryPushLatestSnapshotAsync(int repositoryId, CancellationToken ct = default);
    Task<int> RestoreRepositoriesFromCloudAsync(CancellationToken ct = default);
    Task ProcessPendingQueueAsync(CancellationToken ct = default);
}
