using Microsoft.Extensions.Logging.Abstractions;
using Veyra.Desktop.Services.Sync;

namespace Veyra.Desktop.Tests;

public sealed class RepositoryFsEventQueueServiceTests
{
    [Fact]
    public async Task LeaseAsync_ReplaysStaleRunningItemsAcrossServiceRestart()
    {
        using var overrideScope = new QueuePathOverrideScope();

        var first = new RepositoryFsEventQueueService(NullLogger<RepositoryFsEventQueueService>.Instance);
        await first.EnqueueAsync(42, @"C:\\repo\\a.txt", "changed");
        await first.EnqueueAsync(42, @"C:\\repo\\b.txt", "created");

        var initialLease = await first.LeaseAsync(42, maxItems: 1, staleRunningAfter: TimeSpan.FromHours(1));
        Assert.Equal(1, initialLease.Count);

        var second = new RepositoryFsEventQueueService(NullLogger<RepositoryFsEventQueueService>.Instance);
        var replayLease = await second.LeaseAsync(42, maxItems: 10, staleRunningAfter: TimeSpan.Zero);

        Assert.Equal(2, replayLease.Count);

        await second.CompleteAsync(42, replayLease.ItemIds);
        var hasPending = await second.HasPendingAsync(42);

        Assert.False(hasPending);
    }

    [Fact]
    public async Task EnqueueAsync_DeduplicatesPendingItemsByPath()
    {
        using var overrideScope = new QueuePathOverrideScope();

        var queue = new RepositoryFsEventQueueService(NullLogger<RepositoryFsEventQueueService>.Instance);
        await queue.EnqueueAsync(7, @"C:\\repo\\same.txt", "changed");
        await queue.EnqueueAsync(7, @"C:\\repo\\same.txt", "deleted");
        await queue.EnqueueAsync(7, @"C:\\repo\\same.txt", "renamed");

        var lease = await queue.LeaseAsync(7, maxItems: 10, staleRunningAfter: TimeSpan.FromHours(1));
        Assert.Equal(1, lease.Count);

        await queue.CompleteAsync(7, lease.ItemIds);
        var hasPending = await queue.HasPendingAsync(7);

        Assert.False(hasPending);
    }

    private sealed class QueuePathOverrideScope : IDisposable
    {
        private const string EnvironmentVariableName = "VEYRA_FS_EVENT_QUEUE_PATH";

        private readonly string? _previous;
        private readonly string _tempRoot;

        public QueuePathOverrideScope()
        {
            _previous = Environment.GetEnvironmentVariable(EnvironmentVariableName);
            _tempRoot = Path.Combine(Path.GetTempPath(), "veyra-fs-event-queue-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);

            var queuePath = Path.Combine(_tempRoot, "repository-fs-event-queue.json");
            Environment.SetEnvironmentVariable(EnvironmentVariableName, queuePath);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(EnvironmentVariableName, _previous);

            if (!Directory.Exists(_tempRoot))
                return;

            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch
            {
            }
        }
    }
}
