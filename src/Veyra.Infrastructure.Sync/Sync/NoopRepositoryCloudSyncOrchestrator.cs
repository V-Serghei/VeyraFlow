using Veyra.Application.Abstractions.Sync;

namespace Veyra.Infrastructure.Sync.Sync;

public sealed class NoopRepositoryCloudSyncOrchestrator : IRepositoryCloudSyncOrchestrator
{
    public Task TryPushLatestSnapshotAsync(int repositoryId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<int> RestoreRepositoriesFromCloudAsync(CancellationToken ct = default)
        => Task.FromResult(0);
}
