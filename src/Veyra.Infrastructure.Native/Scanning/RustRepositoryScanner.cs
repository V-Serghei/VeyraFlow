using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.DTOs;
using Veyra.Infrastructure.Native.Diagnostics;
using Veyra.Infrastructure.Native.Execution;
using Veyra.Infrastructure.Native.Interop;

namespace Veyra.Infrastructure.Native.Scanning;

public sealed class RustRepositoryScanner(
    IRepositoryRepository repositories,
    IRepositorySnapshotRepository snapshots,
    INativeExecutionScheduler scheduler,
    ILogger<RustRepositoryScanner> log)
    : IRepositoryScanner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly ConcurrentDictionary<int, string> ActiveRepositoryScans = new();

    public async Task<RepositoryScanResultDto> ScanRepositoryAsync(
        int repositoryId,
        IProgress<RepositoryScanProgressDto>? progress = null,
        RepositoryScanOptionsDto? options = null,
        CancellationToken ct = default)
    {
        var overallTimer = Stopwatch.StartNew();
        var operationId = Guid.NewGuid().ToString("N");
        ct.ThrowIfCancellationRequested();

        var scanOptions = options ?? new RepositoryScanOptionsDto();

        var repo = await repositories.GetRepositoryByIdAsync(repositoryId, ct);
        if (repo is null || repo.IsDeleted)
            return new RepositoryScanResultDto(0, 0, 0, "skipped_missing", false, false);

        if (!Directory.Exists(repo.DirectoryPath))
        {
            log.LogWarning("Skipping scan for repository {RepositoryId}. Directory not found {Path}", repositoryId, repo.DirectoryPath);
            return new RepositoryScanResultDto(0, 0, 0, "skipped_directory_not_found", false, false);
        }

        var requestedTrigger = ResolveTrigger(scanOptions, "requested");
        if (!ActiveRepositoryScans.TryAdd(repositoryId, requestedTrigger))
        {
            log.LogInformation(
                "Skipping scan because another scan is already running. RepositoryId {RepositoryId}. ActiveTrigger {ActiveTrigger}. RequestedTrigger {RequestedTrigger}. Scheduled {Scheduled}",
                repositoryId,
                ActiveRepositoryScans.GetValueOrDefault(repositoryId, "unknown"),
                requestedTrigger,
                scanOptions.IsScheduled);
            return new RepositoryScanResultDto(
                0,
                0,
                0,
                "skipped_scan_in_progress",
                SnapshotCreated: false,
                NoChangesDetected: false,
                BusyFiles: null,
                SkippedBecauseScanInProgress: true);
        }

        try
        {
            log.LogInformation(
                "Starting scan operation {OperationId} for repository {RepositoryId}. Root {Root}. Formats {FormatCount}. Scheduled {Scheduled}. MaxReadBps {MaxReadBps}. MaxIops {MaxIops}. SaveVersions {SaveVersions}",
                operationId,
                repositoryId,
                repo.DirectoryPath,
                repo.LinkedFormats,
                scanOptions.IsScheduled,
                scanOptions.MaxReadBytesPerSecond,
                scanOptions.MaxIoOperationsPerSecond,
                scanOptions.SaveFileVersions);

            progress?.Report(new RepositoryScanProgressDto(
                "prepare",
                2,
                0,
                0,
                "Preparing scan"));

            List<RepositoryScanEntryDto> entries;
            string trigger;
            string engineName;
            long scanStageMs;
            long entryProjectionMs = 0;

            try
            {
                engineName = "rust";
                progress?.Report(new RepositoryScanProgressDto(
                    "scan",
                    10,
                    0,
                    0,
                    "Scanning directory"));

                var nativeScanTimer = Stopwatch.StartNew();
                var nativeResult = await scheduler.RunAsync(() =>
                {
                    var json = VeyraCoreNative.ScanDirectoryJson(
                        repo.DirectoryPath,
                        repo.LinkedFormats,
                        scanOptions.MaxReadBytesPerSecond,
                        scanOptions.MaxIoOperationsPerSecond);
                    nativeScanTimer.Stop();

                    var projectionTimer = Stopwatch.StartNew();
                    var nativeEntries = JsonSerializer.Deserialize<List<NativeScanEntry>>(json, JsonOptions) ?? [];
                    var projectedEntries = nativeEntries
                        .Select(ToEntry)
                        .Where(e => !string.IsNullOrWhiteSpace(e.RelativePath))
                        .Where(e => !RepositoryScanExclusionMatcher.IsExcluded(e.RelativePath, repo.ExcludedPatterns))
                        .ToList();
                    projectionTimer.Stop();

                    return new NativeScanExecutionResult(
                        projectedEntries,
                        nativeScanTimer.ElapsedMilliseconds,
                        projectionTimer.ElapsedMilliseconds);
                }, ct);

                entries = nativeResult.Entries;
                scanStageMs = nativeResult.NativeScanMs;
                entryProjectionMs = nativeResult.EntryProjectionMs;

                var scannedFiles = entries.Count(e => !e.IsDirectory);
                progress?.Report(new RepositoryScanProgressDto(
                    "scan",
                    70,
                    scannedFiles,
                    scannedFiles,
                    $"Scanned files: {scannedFiles}"));

                trigger = ResolveTrigger(scanOptions, "rust");
                log.LogInformation(
                    "Repository scan native stage completed. OperationId {OperationId}. RepositoryId {RepositoryId}. Root {Root}. Engine {Engine}. NativeScanMs {NativeScanMs}. EntryProjectionMs {EntryProjectionMs}. Entries {Entries}. Files {Files}",
                    operationId,
                    repositoryId,
                    repo.DirectoryPath,
                    engineName,
                    scanStageMs,
                    entryProjectionMs,
                    entries.Count,
                    scannedFiles);
                NativeFeatureUsageTracker.MarkNativeHit(NativeFeatureUsageTracker.Scan);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                engineName = "managed_fallback";
                NativeFeatureUsageTracker.MarkManagedFallback(NativeFeatureUsageTracker.Scan);
                log.LogWarning(ex,
                    "Rust scanner unavailable for repository {RepositoryId}. Falling back to managed scan.",
                    repositoryId);

                var managedScanTimer = Stopwatch.StartNew();
                entries = await BuildManagedEntriesAsync(
                    repo.DirectoryPath,
                    repo.LinkedFormats,
                    repo.ExcludedPatterns,
                    scanOptions.MaxReadBytesPerSecond,
                    scanOptions.MaxIoOperationsPerSecond,
                    progress,
                    ct);
                managedScanTimer.Stop();
                scanStageMs = managedScanTimer.ElapsedMilliseconds;

                trigger = ResolveTrigger(scanOptions, "managed_fallback");
                log.LogInformation(
                    "Repository scan managed fallback completed. OperationId {OperationId}. RepositoryId {RepositoryId}. Root {Root}. Engine {Engine}. ScanMs {ScanMs}. Entries {Entries}. Files {Files}",
                    operationId,
                    repositoryId,
                    repo.DirectoryPath,
                    engineName,
                    scanStageMs,
                    entries.Count,
                    entries.Count(e => !e.IsDirectory));
            }

            var fileCount = entries.Count(e => !e.IsDirectory);
            var totalFileBytes = entries
                .Where(static e => !e.IsDirectory)
                .Sum(static e => Math.Max(0L, e.SizeBytes));

            progress?.Report(new RepositoryScanProgressDto(
                "save",
                72,
                0,
                fileCount,
                "Saving scan results"));

            var saveProgress = new Progress<RepositoryScanProgressDto>(p =>
            {
                var savePercent = Math.Clamp(72 + (int)Math.Round(p.Percent * 0.27), 72, 99);
                var processed = Math.Max(0, p.FilesProcessed);
                var total = p.FilesTotal > 0 ? p.FilesTotal : fileCount;

                progress?.Report(new RepositoryScanProgressDto(
                    p.Stage,
                    savePercent,
                    processed,
                    total,
                    p.Message));
            });

            var saveTimer = Stopwatch.StartNew();
            var saveResult = await snapshots.SaveSnapshotAsync(
                repositoryId,
                trigger,
                DateTime.UtcNow,
                entries,
                scanOptions.SaveFileVersions,
                scanOptions.SnapshotTitle,
                scanOptions.SnapshotTags,
                saveProgress,
                ct);
            saveTimer.Stop();

            if (scanOptions.SaveFileVersions
                && IsManualSnapshotTrigger(trigger)
                && !saveResult.SnapshotCreated
                && saveResult.NoChangesDetected)
            {
                throw new InvalidOperationException("Cannot create snapshot: no file changes detected.");
            }

            var completedMessage = saveResult.HasBusyFiles
                ? $"Scan completed with warnings: {saveResult.BusyFilesCount} file(s) still in use"
                : "Scan completed";

            progress?.Report(new RepositoryScanProgressDto(
                "done",
                100,
                fileCount,
                fileCount,
                completedMessage));

            log.LogInformation(
                "Repository scan operation completed. OperationId {OperationId}. RepositoryId {RepositoryId}. Entries {EntryCount}. Files {FileCount}. Trigger {Trigger}. Engine {Engine}. ScanStageMs {ScanStageMs}. EntryProjectionMs {EntryProjectionMs}. SnapshotSaveMs {SnapshotSaveMs}. TotalMs {TotalMs}. SaveVersions {SaveVersions}. SnapshotCreated {SnapshotCreated}. NoChanges {NoChanges}. BusyFiles {BusyFiles}",
                operationId,
                repositoryId,
                entries.Count,
                fileCount,
                trigger,
                engineName,
                scanStageMs,
                entryProjectionMs,
                saveTimer.ElapsedMilliseconds,
                overallTimer.ElapsedMilliseconds,
                scanOptions.SaveFileVersions,
                saveResult.SnapshotCreated,
                saveResult.NoChangesDetected,
                saveResult.BusyFilesCount);

            RepositoryScanTelemetryTracker.Record(
                repositoryId,
                repo.DirectoryPath,
                engineName,
                entries.Count,
                fileCount,
                totalFileBytes,
                scanStageMs,
                overallTimer.ElapsedMilliseconds,
                trigger,
                DateTime.UtcNow);

            return new RepositoryScanResultDto(
                entries.Count,
                fileCount,
                entries.Count - fileCount,
                trigger,
                saveResult.SnapshotCreated,
                saveResult.NoChangesDetected,
                saveResult.BusyFilesSafe);
        }
        finally
        {
            ActiveRepositoryScans.TryRemove(repositoryId, out _);
        }
    }

    public async Task ScanAllRepositoriesAsync(CancellationToken ct = default)
    {
        var repos = await repositories.GetAllRepositoriesAsync(ct);
        foreach (var repo in repos)
        {
            ct.ThrowIfCancellationRequested();
            await ScanRepositoryAsync(repo.Id, null, null, ct);
        }
    }

    private static string ResolveTrigger(RepositoryScanOptionsDto options, string engine)
    {
        if (!string.IsNullOrWhiteSpace(options.TriggerOverride))
            return options.TriggerOverride.Trim();

        if (options.IsScheduled)
            return options.SaveFileVersions ? $"scheduled_snapshot_{engine}" : $"scheduled_sync_{engine}";

        return options.SaveFileVersions ? $"manual_snapshot_{engine}" : $"sync_index_{engine}";
    }

    private static bool IsManualSnapshotTrigger(string trigger)
        => trigger.StartsWith("manual_snapshot", StringComparison.OrdinalIgnoreCase)
           || string.Equals(trigger, "manual", StringComparison.OrdinalIgnoreCase);

    private static RepositoryScanEntryDto ToEntry(NativeScanEntry src)
    {
        var unix = src.LastWriteUnixSeconds < 0 ? 0 : src.LastWriteUnixSeconds;
        var lastWrite = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

        return new RepositoryScanEntryDto(
            src.RelativePath,
            src.ParentRelativePath,
            src.Name,
            src.IsDirectory,
            src.Extension,
            src.SizeBytes,
            lastWrite,
            src.ContentHashSha256);
    }

    private static async Task<List<RepositoryScanEntryDto>> BuildManagedEntriesAsync(
        string rootPath,
        IReadOnlyCollection<string> linkedFormats,
        IReadOnlyCollection<string> excludedPatterns,
        int maxReadBytesPerSecond,
        int maxIoOperationsPerSecond,
        IProgress<RepositoryScanProgressDto>? progress,
        CancellationToken ct)
    {
        var normalizedExt = linkedFormats
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim().ToLowerInvariant())
            .Select(v => v.StartsWith('.') ? v : "." + v)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var root = new DirectoryInfo(rootPath);
        var entries = new List<RepositoryScanEntryDto>();
        var processedFiles = 0;
        var iopsState = new IopsThrottleState();

        await TraverseDirectoryAsync(root, rootPath, normalizedExt, excludedPatterns, maxReadBytesPerSecond, maxIoOperationsPerSecond, iopsState, entries, () =>
        {
            processedFiles++;
            if (processedFiles % 25 == 0)
            {
                var percent = Math.Min(80, 20 + (processedFiles / 25) * 4);
                progress?.Report(new RepositoryScanProgressDto(
                    "scan",
                    percent,
                    processedFiles,
                    0,
                    $"Scanned files: {processedFiles}"));
            }
        }, ct);

        return entries
            .OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task TraverseDirectoryAsync(
        DirectoryInfo current,
        string rootPath,
        HashSet<string> extFilter,
        IReadOnlyCollection<string> excludedPatterns,
        int maxReadBytesPerSecond,
        int maxIoOperationsPerSecond,
        IopsThrottleState iopsState,
        List<RepositoryScanEntryDto> entries,
        Action onFileProcessed,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        IEnumerable<DirectoryInfo> dirs;
        try
        {
            dirs = current.EnumerateDirectories();
        }
        catch
        {
            return;
        }

        foreach (var dir in dirs)
        {
            ct.ThrowIfCancellationRequested();

            var relative = ToRelativePath(rootPath, dir.FullName);
            if (RepositoryScanExclusionMatcher.IsExcluded(relative, excludedPatterns))
                continue;

            var parentRelative = GetParentRelativePath(relative);

            entries.Add(new RepositoryScanEntryDto(
                relative,
                parentRelative,
                dir.Name,
                true,
                null,
                0,
                dir.LastWriteTimeUtc,
                null));

            await TraverseDirectoryAsync(dir, rootPath, extFilter, excludedPatterns, maxReadBytesPerSecond, maxIoOperationsPerSecond, iopsState, entries, onFileProcessed, ct);
        }

        IEnumerable<FileInfo> files;
        try
        {
            files = current.EnumerateFiles();
        }
        catch
        {
            return;
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var ext = NormalizeExtension(file.Extension);
            if (extFilter.Count > 0 && (ext is null || !extFilter.Contains(ext)))
                continue;

            string? hash;
            try
            {
                await ApplyIoThrottleAsync(maxIoOperationsPerSecond, iopsState, ct);
                hash = await ComputeSha256Async(file.FullName, maxReadBytesPerSecond, ct);
            }
            catch
            {
                continue;
            }

            var relative = ToRelativePath(rootPath, file.FullName);
            if (RepositoryScanExclusionMatcher.IsExcluded(relative, excludedPatterns))
                continue;

            var parentRelative = GetParentRelativePath(relative);

            entries.Add(new RepositoryScanEntryDto(
                relative,
                parentRelative,
                file.Name,
                false,
                ext,
                file.Length,
                file.LastWriteTimeUtc,
                hash));

            onFileProcessed();
        }
    }

    private static async Task ApplyIoThrottleAsync(int maxIoOperationsPerSecond, IopsThrottleState state, CancellationToken ct)
    {
        if (maxIoOperationsPerSecond <= 0)
            return;

        state.TotalOperations++;

        var expectedSeconds = state.TotalOperations / (double)maxIoOperationsPerSecond;
        var elapsedSeconds = (DateTime.UtcNow - state.StartUtc).TotalSeconds;

        if (expectedSeconds > elapsedSeconds)
        {
            var sleepMs = (int)Math.Ceiling((expectedSeconds - elapsedSeconds) * 1000.0);
            if (sleepMs > 0)
                await Task.Delay(sleepMs, ct);
        }
    }

    private static string ToRelativePath(string rootPath, string fullPath)
    {
        var rel = Path.GetRelativePath(rootPath, fullPath)
            .Replace('\\', '/')
            .Trim();

        return rel;
    }

    private static string? GetParentRelativePath(string relativePath)
    {
        var idx = relativePath.LastIndexOf('/');
        if (idx <= 0)
            return null;

        return relativePath[..idx];
    }

    private static string? NormalizeExtension(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var v = value.Trim().ToLowerInvariant();
        return v.StartsWith('.') ? v : "." + v;
    }

    private static async Task<string> ComputeSha256Async(string filePath, int maxReadBytesPerSecond, CancellationToken ct)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 1024,
            useAsync: true);

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];

        var throttled = maxReadBytesPerSecond > 0;
        var start = DateTime.UtcNow;
        long totalRead = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
                break;

            hasher.AppendData(buffer, 0, read);

            if (throttled)
            {
                totalRead += read;
                var expectedSeconds = totalRead / (double)maxReadBytesPerSecond;
                var elapsed = (DateTime.UtcNow - start).TotalSeconds;

                if (expectedSeconds > elapsed)
                {
                    var sleepMs = (int)Math.Ceiling((expectedSeconds - elapsed) * 1000.0);
                    if (sleepMs > 0)
                        await Task.Delay(sleepMs, ct);
                }
            }
        }

        var hash = hasher.GetHashAndReset();
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

}
