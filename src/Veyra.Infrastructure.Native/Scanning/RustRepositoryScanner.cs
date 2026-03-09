using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.DTOs;
using Veyra.Infrastructure.Native.Interop;

namespace Veyra.Infrastructure.Native.Scanning;

public sealed class RustRepositoryScanner(
    IRepositoryRepository repositories,
    IRepositorySnapshotRepository snapshots,
    ILogger<RustRepositoryScanner> log)
    : IRepositoryScanner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<RepositoryScanResultDto> ScanRepositoryAsync(
        int repositoryId,
        IProgress<RepositoryScanProgressDto>? progress = null,
        RepositoryScanOptionsDto? options = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var scanOptions = options ?? new RepositoryScanOptionsDto();

        var repo = await repositories.GetRepositoryByIdAsync(repositoryId, ct);
        if (repo is null || repo.IsDeleted)
            return new RepositoryScanResultDto(0, 0, 0, "skipped_missing");

        if (!Directory.Exists(repo.DirectoryPath))
        {
            log.LogWarning("Skipping scan for repository {RepositoryId}. Directory not found {Path}", repositoryId, repo.DirectoryPath);
            return new RepositoryScanResultDto(0, 0, 0, "skipped_directory_not_found");
        }

        log.LogInformation(
            "Starting scan for repository {RepositoryId}. Root {Root}. Formats {FormatCount}. Scheduled {Scheduled}. MaxReadBps {MaxReadBps}. MaxIops {MaxIops}. SaveVersions {SaveVersions}",
            repositoryId,
            repo.DirectoryPath,
            repo.LinkedFormats.Count,
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

        try
        {
            progress?.Report(new RepositoryScanProgressDto(
                "scan",
                10,
                0,
                0,
                "Scanning directory"));

            var json = await Task.Run(() =>
                    VeyraCoreNative.ScanDirectoryJson(
                        repo.DirectoryPath,
                        repo.LinkedFormats,
                        scanOptions.MaxReadBytesPerSecond,
                        scanOptions.MaxIoOperationsPerSecond),
                ct);

            var nativeEntries = JsonSerializer.Deserialize<List<NativeScanEntry>>(json, JsonOptions) ?? [];

            entries = nativeEntries
                .Select(ToEntry)
                .Where(e => !string.IsNullOrWhiteSpace(e.RelativePath))
                .ToList();

            var scannedFiles = entries.Count(e => !e.IsDirectory);
            progress?.Report(new RepositoryScanProgressDto(
                "scan",
                70,
                scannedFiles,
                scannedFiles,
                $"Scanned files: {scannedFiles}"));

            trigger = ResolveTrigger(scanOptions, "rust");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            log.LogWarning(ex,
                "Rust scanner unavailable for repository {RepositoryId}. Falling back to managed scan.",
                repositoryId);

            entries = await BuildManagedEntriesAsync(
                repo.DirectoryPath,
                repo.LinkedFormats,
                scanOptions.MaxReadBytesPerSecond,
                scanOptions.MaxIoOperationsPerSecond,
                progress,
                ct);

            trigger = ResolveTrigger(scanOptions, "managed_fallback");
        }

        var fileCount = entries.Count(e => !e.IsDirectory);

        progress?.Report(new RepositoryScanProgressDto(
            "save",
            92,
            fileCount,
            fileCount,
            "Saving scan results"));

        var saveResult = await snapshots.SaveSnapshotAsync(
            repositoryId,
            trigger,
            DateTime.UtcNow,
            entries,
            scanOptions.SaveFileVersions,
            scanOptions.SnapshotTitle,
            ct);

        if (scanOptions.SaveFileVersions
            && IsManualSnapshotTrigger(trigger)
            && !saveResult.SnapshotCreated
            && saveResult.NoChangesDetected)
        {
            throw new InvalidOperationException("Cannot create snapshot: no file changes detected.");
        }

        progress?.Report(new RepositoryScanProgressDto(
            "done",
            100,
            fileCount,
            fileCount,
            "Scan completed"));

        log.LogInformation(
            "Repository {RepositoryId} scanned. Entries {EntryCount}. Trigger {Trigger}",
            repositoryId,
            entries.Count,
            trigger);

        return new RepositoryScanResultDto(entries.Count, fileCount, entries.Count - fileCount, trigger);
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

        await TraverseDirectoryAsync(root, rootPath, normalizedExt, maxReadBytesPerSecond, maxIoOperationsPerSecond, iopsState, entries, () =>
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

            await TraverseDirectoryAsync(dir, rootPath, extFilter, maxReadBytesPerSecond, maxIoOperationsPerSecond, iopsState, entries, onFileProcessed, ct);
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
            FileShare.Read,
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

    private sealed class IopsThrottleState
    {
        public DateTime StartUtc { get; } = DateTime.UtcNow;
        public long TotalOperations { get; set; }
    }

    private sealed record NativeScanEntry
    {
        [JsonPropertyName("relative_path")]
        public string RelativePath { get; init; } = string.Empty;

        [JsonPropertyName("parent_relative_path")]
        public string? ParentRelativePath { get; init; }

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("is_directory")]
        public bool IsDirectory { get; init; }

        [JsonPropertyName("extension")]
        public string? Extension { get; init; }

        [JsonPropertyName("size_bytes")]
        public long SizeBytes { get; init; }

        [JsonPropertyName("last_write_unix_seconds")]
        public long LastWriteUnixSeconds { get; init; }

        [JsonPropertyName("content_hash_sha256")]
        public string? ContentHashSha256 { get; init; }
    }
}
