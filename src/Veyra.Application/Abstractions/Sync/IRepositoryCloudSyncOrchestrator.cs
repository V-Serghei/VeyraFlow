using Veyra.Application.DTOs;

namespace Veyra.Application.Abstractions.Sync;

public interface IRepositoryCloudSyncOrchestrator
{
    Task TryPushLatestSnapshotAsync(int repositoryId, CancellationToken ct = default);
    Task<int> RestoreRepositoriesFromCloudAsync(string? targetRootDirectory = null, CancellationToken ct = default);
    Task<bool> RestoreRepositoryFromCloudAsync(
        int cloudRepositoryId,
        string? targetRootDirectory = null,
        bool restoreFullHistory = false,
        bool restoreToAnotherFolder = false,
        CancellationToken ct = default);
    Task ProcessPendingQueueAsync(CancellationToken ct = default);
    Task<RepositoryCloudRepairResultDto> RepairRepositoryCloudDataAsync(int repositoryId, CancellationToken ct = default);
    Task<bool> CancelRepositorySyncAsync(int repositoryId, CancellationToken ct = default);
}
