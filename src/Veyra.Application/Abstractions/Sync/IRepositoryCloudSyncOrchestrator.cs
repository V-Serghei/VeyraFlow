using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Cloud;

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
        bool restoreMetadataOnly = false,
        CancellationToken ct = default);
    Task ProcessPendingQueueAsync(CancellationToken ct = default);
    Task<RepositoryCloudRepairResultDto> RepairRepositoryCloudDataAsync(int repositoryId, CancellationToken ct = default);
    Task<bool> CancelRepositorySyncAsync(int repositoryId, CancellationToken ct = default);
}
