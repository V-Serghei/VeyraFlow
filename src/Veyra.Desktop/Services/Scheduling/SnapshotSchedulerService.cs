using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Abstractions.Sync;
using Veyra.Application.DTOs;

namespace Veyra.Desktop.Services.Scheduling;

public sealed class SnapshotSchedulerService(
    IServiceScopeFactory scopeFactory,
    SnapshotSchedulerOptions options,
    ILogger<SnapshotSchedulerService> log)
    : ISnapshotScheduler
{
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public void Start()
    {
        if (!options.Enabled)
        {
            log.LogInformation("Snapshot scheduler is disabled by configuration");
            return;
        }

        if (_cts is not null)
            return;

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));

        log.LogInformation(
            "Snapshot scheduler started. Poll {PollSeconds}s. Interval {IntervalMinutes}m. QuietHours {Start}-{End}. MaxReadBps {MaxReadBps}. MaxIops {MaxIops}. Retries {Retries}",
            options.PollSeconds,
            options.IntervalMinutes,
            options.QuietHoursStartHour,
            options.QuietHoursEndHour,
            options.MaxReadBytesPerSecond,
            options.MaxIoOperationsPerSecond,
            options.RetryCount);
    }

    public async Task StopAsync()
    {
        if (_cts is null)
            return;

        _cts.Cancel();

        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _loop = null;
        _cts.Dispose();
        _cts = null;

        log.LogInformation("Snapshot scheduler stopped");
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var poll = TimeSpan.FromSeconds(Math.Clamp(options.PollSeconds, 5, 600));

        using var timer = new PeriodicTimer(poll);

        while (await timer.WaitForNextTickAsync(ct))
        {
            await ExecuteTickSafeAsync(ct);
        }
    }

    private async Task ExecuteTickSafeAsync(CancellationToken ct)
    {
        if (!await _tickGate.WaitAsync(0, ct))
            return;

        try
        {
            await ExecuteTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Snapshot scheduler tick failed");
        }
        finally
        {
            _tickGate.Release();
        }
    }

    private async Task ExecuteTickAsync(CancellationToken ct)
    {
        if (IsInsideQuietHours(DateTime.Now, options.QuietHoursStartHour, options.QuietHoursEndHour))
            return;

        await using var scope = scopeFactory.CreateAsyncScope();

        var repositories = scope.ServiceProvider.GetRequiredService<IRepositoryRepository>();
        var scanner = scope.ServiceProvider.GetRequiredService<IRepositoryScanner>();
        var retention = scope.ServiceProvider.GetRequiredService<IRepositoryRetentionService>();
        var cloudSync = scope.ServiceProvider.GetRequiredService<IRepositoryCloudSyncOrchestrator>();

        var all = await repositories.GetAllRepositoriesAsync(ct);
        if (all.Count == 0)
        {
            await cloudSync.ProcessPendingQueueAsync(ct);
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Clamp(options.IntervalMinutes, 1, 24 * 60));
        var nowUtc = DateTime.UtcNow;

        foreach (var repo in all)
        {
            ct.ThrowIfCancellationRequested();

            if (repo.LastScannedAt is not null && nowUtc - repo.LastScannedAt.Value < interval)
                continue;

            await RunScheduledScanWithRetryAsync(scanner, repo.Id, ct);
        }

        var retentionRuns = await retention.RunDueRetentionAsync(ct: ct);
        if (retentionRuns.Count > 0)
        {
            log.LogInformation(
                "Scheduled retention completed for {Count} repositories",
                retentionRuns.Count);
        }

        await cloudSync.ProcessPendingQueueAsync(ct);
    }

    private async Task RunScheduledScanWithRetryAsync(
        IRepositoryScanner scanner,
        int repositoryId,
        CancellationToken ct)
    {
        var totalAttempts = Math.Clamp(options.RetryCount, 0, 10) + 1;
        var delay = TimeSpan.FromSeconds(Math.Clamp(options.RetryDelaySeconds, 1, 300));

        for (var attempt = 1; attempt <= totalAttempts; attempt++)
        {
            try
            {
                await scanner.ScanRepositoryAsync(
                    repositoryId,
                    null,
                    new RepositoryScanOptionsDto(
                        IsScheduled: true,
                        MaxReadBytesPerSecond: Math.Max(0, options.MaxReadBytesPerSecond),
                        MaxIoOperationsPerSecond: Math.Max(0, options.MaxIoOperationsPerSecond),
                        SaveFileVersions: false),
                    ct);

                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt >= totalAttempts)
                {
                    log.LogError(ex,
                        "Scheduled scan failed. RepositoryId {RepositoryId}. Attempts {Attempts}",
                        repositoryId,
                        totalAttempts);
                    return;
                }

                log.LogWarning(ex,
                    "Scheduled scan attempt failed. RepositoryId {RepositoryId}. Attempt {Attempt}/{Attempts}",
                    repositoryId,
                    attempt,
                    totalAttempts);

                await Task.Delay(delay, ct);
            }
        }
    }

    private static bool IsInsideQuietHours(DateTime localNow, int startHour, int endHour)
    {
        var start = Math.Clamp(startHour, 0, 23);
        var end = Math.Clamp(endHour, 0, 23);

        if (start == end)
            return false;

        var hour = localNow.Hour;

        if (start < end)
            return hour >= start && hour < end;

        return hour >= start || hour < end;
    }
}
