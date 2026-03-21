using Veyra.Application.DTOs;

namespace Veyra.Application.Abstractions.Sync;

public interface IRepositoryCloudSyncOrchestrator
{
    Task TryPushLatestSnapshotAsync(int repositoryId, CancellationToken ct = default);
    Task<int> RestoreRepositoriesFromCloudAsync(CancellationToken ct = default);
    Task ProcessPendingQueueAsync(CancellationToken ct = default);
    Task<RepositoryCloudRepairResultDto> RepairRepositoryCloudDataAsync(int repositoryId, CancellationToken ct = default);
    Task<bool> CancelRepositorySyncAsync(int repositoryId, CancellationToken ct = default);
}
