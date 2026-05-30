using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Scanning;

namespace Veyra.Application.Commands.Repository;

internal static class InitialSnapshotCreation
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(750);

    public static async Task<RepositoryScanResultDto> RunWithBoundedRetryAsync(
        IRepositoryScanner scanner,
        int repositoryId,
        IProgress<RepositoryScanProgressDto>? progress,
        ILogger log,
        CancellationToken ct)
    {
        RepositoryScanResultDto? lastResult = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            lastResult = await scanner.ScanRepositoryAsync(
                repositoryId,
                progress,
                new RepositoryScanOptionsDto(
                    SaveFileVersions: true,
                    TriggerOverride: "initial_snapshot"),
                ct);

            if (!ShouldRetry(lastResult))
                return lastResult;

            log.LogWarning(
                "Initial snapshot did not complete cleanly. RepositoryId {RepositoryId}. Attempt {Attempt}/{Attempts}. Files {Files}. SnapshotCreated {SnapshotCreated}. NoChanges {NoChanges}. ScanInProgress {ScanInProgress}. BusyFiles {BusyFiles}",
                repositoryId,
                attempt,
                MaxAttempts,
                lastResult.FileEntries,
                lastResult.SnapshotCreated,
                lastResult.NoChangesDetected,
                lastResult.SkippedBecauseScanInProgress,
                lastResult.BusyFilesCount);

            if (attempt < MaxAttempts)
                await Task.Delay(RetryDelay * attempt, ct);
        }

        return lastResult ?? new RepositoryScanResultDto(0, 0, 0, "initial_snapshot_failed");
    }

    public static string ResolveSnapshotStatus(RepositoryScanResultDto result)
    {
        if (result.SnapshotCreated)
            return "created";

        if (RequiresUserRetry(result))
            return "needs retry";

        return "not created";
    }

    public static bool RequiresUserRetry(RepositoryScanResultDto result)
        => result.SkippedBecauseScanInProgress
           || (result.FileEntries > 0 && !result.SnapshotCreated);

    private static bool ShouldRetry(RepositoryScanResultDto result)
        => result.SkippedBecauseScanInProgress
           || (result.FileEntries > 0 && !result.SnapshotCreated && !result.HasBusyFiles);
}
