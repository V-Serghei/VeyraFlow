using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Sync;
using Veyra.Application.Commands.Repository;
using Veyra.Application.DTOs;

namespace Veyra.Application.Tests;

public sealed class ScanRepositoryHandlerTests
{
    [Fact]
    public async Task Handle_DoesNotPushCloudSync_WhenVersionScanFindsNoNewSnapshot()
    {
        var scanner = new FakeRepositoryScanner
        {
            Result = new RepositoryScanResultDto(
                TotalEntries: 4,
                FileEntries: 3,
                DirectoryEntries: 1,
                Trigger: "auto_snapshot_live_watcher",
                SnapshotCreated: false,
                NoChangesDetected: true)
        };
        var cloudSync = new FakeRepositoryCloudSyncOrchestrator();
        var handler = new ScanRepositoryHandler(scanner, CreateScopeFactory(cloudSync), NullLogger<ScanRepositoryHandler>.Instance);

        var result = await handler.Handle(
            new ScanRepositoryCommand(
                7,
                Progress: null,
                new RepositoryScanOptionsDto(SaveFileVersions: true, TriggerOverride: "auto_snapshot_live_watcher")),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, cloudSync.TryPushLatestSnapshotCalls);
    }

    [Fact]
    public async Task Handle_PushesCloudSync_WhenVersionScanCreatesSnapshot()
    {
        var scanner = new FakeRepositoryScanner
        {
            Result = new RepositoryScanResultDto(
                TotalEntries: 6,
                FileEntries: 5,
                DirectoryEntries: 1,
                Trigger: "manual_snapshot",
                SnapshotCreated: true,
                NoChangesDetected: false)
        };
        var cloudSync = new FakeRepositoryCloudSyncOrchestrator();
        var handler = new ScanRepositoryHandler(scanner, CreateScopeFactory(cloudSync), NullLogger<ScanRepositoryHandler>.Instance);

        var result = await handler.Handle(
            new ScanRepositoryCommand(
                9,
                Progress: null,
                new RepositoryScanOptionsDto(SaveFileVersions: true, TriggerOverride: "manual_snapshot")),
            CancellationToken.None);

        await cloudSync.WaitForPushAsync(TimeSpan.FromSeconds(2));

        Assert.True(result.Success);
        Assert.Equal(1, cloudSync.TryPushLatestSnapshotCalls);
    }

    private static IServiceScopeFactory CreateScopeFactory(IRepositoryCloudSyncOrchestrator cloudSync)
    {
        var services = new ServiceCollection();
        services.AddSingleton(cloudSync);

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private sealed class FakeRepositoryScanner : IRepositoryScanner
    {
        public RepositoryScanResultDto Result { get; init; } = new(0, 0, 0, "test");

        public Task<RepositoryScanResultDto> ScanRepositoryAsync(
            int repositoryId,
            IProgress<RepositoryScanProgressDto>? progress = null,
            RepositoryScanOptionsDto? options = null,
            CancellationToken ct = default)
            => Task.FromResult(Result);

        public Task ScanAllRepositoriesAsync(CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeRepositoryCloudSyncOrchestrator : IRepositoryCloudSyncOrchestrator
    {
        private readonly TaskCompletionSource<bool> _pushTaskSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int TryPushLatestSnapshotCalls { get; private set; }

        public Task TryPushLatestSnapshotAsync(int repositoryId, CancellationToken ct = default)
        {
            TryPushLatestSnapshotCalls++;
            _pushTaskSource.TrySetResult(true);
            return Task.CompletedTask;
        }

        public async Task WaitForPushAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            await _pushTaskSource.Task.WaitAsync(cts.Token);
        }

        public Task<int> RestoreRepositoriesFromCloudAsync(string? targetRootDirectory = null, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<bool> RestoreRepositoryFromCloudAsync(
            int cloudRepositoryId,
            string? targetRootDirectory = null,
            bool restoreFullHistory = false,
            bool restoreToAnotherFolder = false,
            CancellationToken ct = default)
            => Task.FromResult(false);

        public Task ProcessPendingQueueAsync(CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<RepositoryCloudRepairResultDto> RepairRepositoryCloudDataAsync(int repositoryId, CancellationToken ct = default)
            => Task.FromResult(new RepositoryCloudRepairResultDto(false, 0, 0, 0, 0, 0));

        public Task<bool> CancelRepositorySyncAsync(int repositoryId, CancellationToken ct = default)
            => Task.FromResult(false);
    }
}
