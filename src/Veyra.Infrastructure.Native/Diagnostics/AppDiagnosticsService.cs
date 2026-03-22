using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Observability;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.DTOs;
using Veyra.Infrastructure.Native.Interop;
using Veyra.Infrastructure.Native.Scanning;

namespace Veyra.Infrastructure.Native.Diagnostics;

public sealed class AppDiagnosticsService(
    IRepositoryRepository repositories,
    IRepositorySnapshotRepository snapshots,
    ILogger<AppDiagnosticsService> log)
    : IAppDiagnosticsService
{
    private const long RecommendedMemoryLimitBytes = 500L * 1024 * 1024;
    private const long RecommendedHistoryTargetMs = 1000;
    private const double RecommendedScanTargetMbPerSecond = 50d;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<AppDiagnosticsReportDto> RunAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var targetRepository = (await repositories.GetAllRepositoriesAsync(ct))
            .Where(static repo => !repo.IsDeleted)
            .Where(static repo => !string.IsNullOrWhiteSpace(repo.DirectoryPath) && Directory.Exists(repo.DirectoryPath))
            .OrderByDescending(static repo => repo.TotalSizeBytes)
            .ThenByDescending(static repo => repo.FileCount)
            .FirstOrDefault();

        var process = CaptureProcessMetrics();
        var nativeRuntime = CaptureNativeRuntime();
        var history = targetRepository is null
            ? new AppDiagnosticsHistoryMetricsDto(false, 0, 0, 0, 0, false, RecommendedHistoryTargetMs, "No repository available for diagnostics.")
            : await CaptureHistoryMetricsAsync(targetRepository, ct);
        var scan = targetRepository is null
            ? new AppDiagnosticsScanBenchmarkDto(false, "unavailable", 0, 0, 0, 0, 0, false, RecommendedScanTargetMbPerSecond, "No repository available for diagnostics.")
            : await CaptureScanBenchmarkAsync(targetRepository, nativeRuntime, ct);

        return new AppDiagnosticsReportDto(
            DateTime.UtcNow,
            targetRepository?.Id,
            targetRepository?.Name,
            targetRepository?.DirectoryPath,
            process,
            history,
            scan,
            nativeRuntime);
    }

    private static AppDiagnosticsProcessMetricsDto CaptureProcessMetrics()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();

        var workingSet = Math.Max(0L, process.WorkingSet64);
        var privateBytes = Math.Max(0L, process.PrivateMemorySize64);
        var managedHeap = Math.Max(0L, GC.GetTotalMemory(forceFullCollection: false));

        return new AppDiagnosticsProcessMetricsDto(
            WorkingSetBytes: workingSet,
            PrivateMemoryBytes: privateBytes,
            ManagedHeapBytes: managedHeap,
            MeetsRecommendedLimit: workingSet <= RecommendedMemoryLimitBytes,
            RecommendedLimitBytes: RecommendedMemoryLimitBytes);
    }

    private async Task<AppDiagnosticsHistoryMetricsDto> CaptureHistoryMetricsAsync(RepositoryDto repository, CancellationToken ct)
    {
        try
        {
            var snapshotHistoryTimer = Stopwatch.StartNew();
            var snapshotHistory = await snapshots.GetSnapshotHistoryAsync(repository.Id, 200, ct);
            snapshotHistoryTimer.Stop();

            var latestEntriesTimer = Stopwatch.StartNew();
            var latestEntries = await snapshots.GetLatestEntriesAsync(repository.Id, ct);
            latestEntriesTimer.Stop();

            return new AppDiagnosticsHistoryMetricsDto(
                Available: true,
                SnapshotHistoryCount: snapshotHistory.Count,
                SnapshotHistoryLoadMs: snapshotHistoryTimer.ElapsedMilliseconds,
                LatestEntriesCount: Math.Min(500, latestEntries.Count),
                LatestEntriesLoadMs: latestEntriesTimer.ElapsedMilliseconds,
                MeetsLatestEntriesTarget: latestEntriesTimer.ElapsedMilliseconds <= RecommendedHistoryTargetMs,
                TargetMs: RecommendedHistoryTargetMs);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "App diagnostics failed to benchmark history loading. RepositoryId {RepositoryId}", repository.Id);
            return new AppDiagnosticsHistoryMetricsDto(
                Available: false,
                SnapshotHistoryCount: 0,
                SnapshotHistoryLoadMs: 0,
                LatestEntriesCount: 0,
                LatestEntriesLoadMs: 0,
                MeetsLatestEntriesTarget: false,
                TargetMs: RecommendedHistoryTargetMs,
                ErrorMessage: ex.Message);
        }
    }

    private async Task<AppDiagnosticsScanBenchmarkDto> CaptureScanBenchmarkAsync(
        RepositoryDto repository,
        AppDiagnosticsNativeRuntimeDto nativeRuntime,
        CancellationToken ct)
    {
        if (!nativeRuntime.IsLoaded || !nativeRuntime.IsHealthy || !nativeRuntime.SupportsScan)
        {
            return new AppDiagnosticsScanBenchmarkDto(
                Available: false,
                Engine: "unavailable",
                EntryCount: 0,
                FileCount: 0,
                TotalFileBytes: 0,
                DurationMs: 0,
                ThroughputMbPerSecond: 0,
                MeetsRecommendedTarget: false,
                TargetMbPerSecond: RecommendedScanTargetMbPerSecond,
                ErrorMessage: nativeRuntime.ErrorMessage ?? "Native scan runtime is unavailable.");
        }

        try
        {
            var scanTimer = Stopwatch.StartNew();
            var json = await Task.Run(
                () => VeyraCoreNative.ScanDirectoryJson(repository.DirectoryPath, repository.LinkedFormats),
                ct);
            scanTimer.Stop();

            var entries = JsonSerializer.Deserialize<List<NativeScanEntry>>(json, JsonOptions) ?? [];
            var filtered = entries
                .Where(static entry => !string.IsNullOrWhiteSpace(entry.RelativePath))
                .Where(entry => !RepositoryScanExclusionMatcher.IsExcluded(entry.RelativePath!, repository.ExcludedPatterns))
                .ToList();

            var fileCount = filtered.Count(static entry => !entry.IsDirectory);
            var totalFileBytes = filtered
                .Where(static entry => !entry.IsDirectory)
                .Sum(static entry => Math.Max(0L, entry.SizeBytes));

            var durationMs = Math.Max(1L, scanTimer.ElapsedMilliseconds);
            var throughput = totalFileBytes <= 0
                ? 0d
                : totalFileBytes / 1024d / 1024d / (durationMs / 1000d);

            return new AppDiagnosticsScanBenchmarkDto(
                Available: true,
                Engine: "rust",
                EntryCount: filtered.Count,
                FileCount: fileCount,
                TotalFileBytes: totalFileBytes,
                DurationMs: durationMs,
                ThroughputMbPerSecond: throughput,
                MeetsRecommendedTarget: throughput >= RecommendedScanTargetMbPerSecond,
                TargetMbPerSecond: RecommendedScanTargetMbPerSecond);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            log.LogWarning(ex, "App diagnostics could not run native scan benchmark. RepositoryId {RepositoryId}", repository.Id);
            return new AppDiagnosticsScanBenchmarkDto(
                Available: false,
                Engine: "native_unavailable",
                EntryCount: 0,
                FileCount: 0,
                TotalFileBytes: 0,
                DurationMs: 0,
                ThroughputMbPerSecond: 0,
                MeetsRecommendedTarget: false,
                TargetMbPerSecond: RecommendedScanTargetMbPerSecond,
                ErrorMessage: ex.Message);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "App diagnostics failed to benchmark native scan. RepositoryId {RepositoryId}", repository.Id);
            return new AppDiagnosticsScanBenchmarkDto(
                Available: false,
                Engine: "error",
                EntryCount: 0,
                FileCount: 0,
                TotalFileBytes: 0,
                DurationMs: 0,
                ThroughputMbPerSecond: 0,
                MeetsRecommendedTarget: false,
                TargetMbPerSecond: RecommendedScanTargetMbPerSecond,
                ErrorMessage: ex.Message);
        }
    }

    private static AppDiagnosticsNativeRuntimeDto CaptureNativeRuntime()
    {
        var report = VeyraCoreNative.ProbeRuntimeHealth();
        var featureUsage = NativeFeatureUsageTracker.Snapshot()
            .Select(entry => new AppDiagnosticsNativeFeatureUsageDto(
                entry.FeatureKey,
                IsFeatureSupported(report, entry.FeatureKey),
                entry.NativeHits,
                entry.ManagedFallbacks))
            .ToArray();

        return new AppDiagnosticsNativeRuntimeDto(
            report.IsLoaded,
            report.IsHealthy,
            report.SupportsScan,
            report.SupportsStoreFileBlocks,
            report.SupportsRestoreFileBlocks,
            report.SupportsTextDiff,
            report.SupportsSnapshotComparison,
            report.SupportsRepositoryPathComparison,
            report.SupportsVersionPlanning,
            report.SupportsImageDiff,
            featureUsage,
            report.LoadedPath,
            report.ErrorMessage);
    }

    private static bool IsFeatureSupported(NativeRuntimeHealthReport report, string featureKey)
        => featureKey switch
        {
            NativeFeatureUsageTracker.Scan => report.SupportsScan,
            NativeFeatureUsageTracker.StoreBlocks => report.SupportsStoreFileBlocks,
            NativeFeatureUsageTracker.RestoreBlocks => report.SupportsRestoreFileBlocks,
            NativeFeatureUsageTracker.TextDiff => report.SupportsTextDiff,
            NativeFeatureUsageTracker.SnapshotComparison => report.SupportsSnapshotComparison,
            NativeFeatureUsageTracker.RepositoryPathComparison => report.SupportsRepositoryPathComparison,
            NativeFeatureUsageTracker.VersionPlanning => report.SupportsVersionPlanning,
            NativeFeatureUsageTracker.ImageDiff => report.SupportsImageDiff,
            _ => false
        };

}
