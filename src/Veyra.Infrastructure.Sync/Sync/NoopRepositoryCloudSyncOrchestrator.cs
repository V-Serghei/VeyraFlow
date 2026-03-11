using Veyra.Application.Abstractions.Sync;
using Veyra.Application.DTOs;

namespace Veyra.Infrastructure.Sync.Sync;

public sealed class NoopRepositoryCloudSyncOrchestrator : IRepositoryCloudSyncOrchestrator
{
    public Task TryPushLatestSnapshotAsync(int repositoryId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<int> RestoreRepositoriesFromCloudAsync(CancellationToken ct = default)
        => Task.FromResult(0);

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
}
