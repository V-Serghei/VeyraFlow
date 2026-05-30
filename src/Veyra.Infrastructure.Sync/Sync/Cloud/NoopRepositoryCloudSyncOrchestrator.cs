using Veyra.Application.Abstractions.Sync;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Cloud;

namespace Veyra.Infrastructure.Sync.Sync;

public sealed class NoopRepositoryCloudSyncOrchestrator : IRepositoryCloudSyncOrchestrator
{
    public Task TryPushLatestSnapshotAsync(int repositoryId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<int> RestoreRepositoriesFromCloudAsync(string? targetRootDirectory = null, CancellationToken ct = default)
        => Task.FromResult(0);

    public Task<bool> RestoreRepositoryFromCloudAsync(
        int cloudRepositoryId,
        string? targetRootDirectory = null,
        bool restoreFullHistory = false,
        bool restoreToAnotherFolder = false,
        bool restoreMetadataOnly = false,
        CancellationToken ct = default)
        => Task.FromResult(false);

    public Task ProcessPendingQueueAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<RepositoryCloudRepairResultDto> RepairRepositoryCloudDataAsync(int repositoryId, CancellationToken ct = default)
        => Task.FromResult(new RepositoryCloudRepairResultDto(
            Success: false,
            ReferencedBlocks: 0,
            AlreadyPresentBlocks: 0,
            UploadedBlocks: 0,
            MissingLocalBlocks: 0,
            FailedUploads: 0,
            ErrorMessage: "Cloud sync is not enabled in this environment."));

    public Task<bool> CancelRepositorySyncAsync(int repositoryId, CancellationToken ct = default)
        => Task.FromResult(false);
}
