using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Abstractions.Sync;
using Veyra.Application.Commands.Repository;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.FileVersions;
using Veyra.Application.DTOs.PendingChanges;
using Veyra.Application.DTOs.Repository.Cloud;
using Veyra.Application.DTOs.Repository.Core;
using Veyra.Application.DTOs.Repository.Retention;
using Veyra.Application.DTOs.Repository.Scanning;
using Veyra.Application.DTOs.Repository.Snapshots;
using Veyra.Application.DTOs.TextDiff;

namespace Veyra.Application.Tests;

public sealed class RestoreIntegrityTests
{
    // ────────────────────────────────────────────────────────────
    // RestoreFileVersionHandler — hash validation (new feature)
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RestoreFileVersion_Succeeds_WhenRestoredHashMatchesExpected()
    {
        var content = "hello veyra restore"u8.ToArray();
        var expectedHash = ComputeSha256Hex(content);
        var version = BuildVersion(expectedHash, content.Length, isDeletion: false);

        var store = new FakeContentStore(contentToWrite: content, tamper: false);
        var handler = BuildHandler(version, store);

        var result = await handler.Handle(
            new RestoreFileVersionCommand(1, version.RelativePath, version.FileVersionId, false, null),
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task RestoreFileVersion_Fails_WhenContentStoreWritesCorruptedBytes()
    {
        var original = "correct content"u8.ToArray();
        var corrupted = "wrong bytes here!!"u8.ToArray();
        var expectedHash = ComputeSha256Hex(original);

        // Store writes different bytes than expected
        var store = new FakeContentStore(contentToWrite: corrupted, tamper: true, expectedHashForValidation: expectedHash);
        var handler = BuildHandler(BuildVersion(expectedHash, original.Length, isDeletion: false), store);

        var result = await handler.Handle(
            new RestoreFileVersionCommand(1, "file.txt", 1, false, null),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("hash", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestoreFileVersion_RejectsDeletionMarker_BeforeRestoring()
    {
        var version = BuildVersion(string.Empty, 0, isDeletion: true);
        var store = new FakeContentStore([], false);
        var handler = BuildHandler(version, store);

        var result = await handler.Handle(
            new RestoreFileVersionCommand(1, version.RelativePath, version.FileVersionId, false, null),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("deletion marker", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, store.RestoreCalls);
    }

    // ────────────────────────────────────────────────────────────
    // ScanRepositoryHandler — cloud push gating
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Scan_DoesNotPushCloud_WhenNoChangesDetected()
    {
        var scanner = new FakeScanner(new RepositoryScanResultDto(3, 2, 1, "scheduled_snapshot_rust",
            SnapshotCreated: false, NoChangesDetected: true));
        var sync = new FakeCloudSync();
        var handler = new ScanRepositoryHandler(scanner, BuildScopeFactory(sync), NullLogger<ScanRepositoryHandler>.Instance);

        await handler.Handle(
            new ScanRepositoryCommand(5, null, new RepositoryScanOptionsDto(SaveFileVersions: true)),
            CancellationToken.None);

        Assert.Equal(0, sync.PushCount);
    }

    [Fact]
    public async Task Scan_PushesCloud_WhenSnapshotCreated()
    {
        var scanner = new FakeScanner(new RepositoryScanResultDto(5, 4, 1, "manual_snapshot_rust",
            SnapshotCreated: true, NoChangesDetected: false));
        var sync = new FakeCloudSync();
        var handler = new ScanRepositoryHandler(scanner, BuildScopeFactory(sync), NullLogger<ScanRepositoryHandler>.Instance);

        await handler.Handle(
            new ScanRepositoryCommand(7, null, new RepositoryScanOptionsDto(SaveFileVersions: true)),
            CancellationToken.None);

        await sync.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, sync.PushCount);
    }

    [Fact]
    public async Task Scan_DoesNotPushCloud_WhenSaveFileVersionsFalse()
    {
        var scanner = new FakeScanner(new RepositoryScanResultDto(2, 2, 0, "sync_index_rust",
            SnapshotCreated: true, NoChangesDetected: false));
        var sync = new FakeCloudSync();
        var handler = new ScanRepositoryHandler(scanner, BuildScopeFactory(sync), NullLogger<ScanRepositoryHandler>.Instance);

        await handler.Handle(
            new ScanRepositoryCommand(3, null, new RepositoryScanOptionsDto(SaveFileVersions: false)),
            CancellationToken.None);

        Assert.Equal(0, sync.PushCount);
    }

    // ────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────

    private static string ComputeSha256Hex(byte[] data)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

    private static FileVersionRestoreDto BuildVersion(string hash, long size, bool isDeletion)
        => new(
            FileVersionId: 1,
            RepositoryId: 1,
            RelativePath: "file.txt",
            Extension: ".txt",
            SizeBytes: size,
            IsDeletionMarker: isDeletion,
            ContentHashSha256: hash,
            LastWriteUtc: DateTime.UtcNow,
            Blocks: isDeletion ? [] : [new StoredFileBlockDto(0, "sha256-" + new string('a', 64), (int)size, (int)size)]);

    private static RestoreFileVersionHandler BuildHandler(FileVersionRestoreDto version, IFileContentStore store)
        => new(new FakeRepoRepository(), new FakeSnapshotRepository(version), store,
               NullLogger<RestoreFileVersionHandler>.Instance);

    private static IServiceScopeFactory BuildScopeFactory(IRepositoryCloudSyncOrchestrator sync)
    {
        var svc = new ServiceCollection();
        svc.AddSingleton(sync);
        return svc.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    // ────────────────────────────────────────────────────────────
    // Fakes
    // ────────────────────────────────────────────────────────────

    private sealed class FakeContentStore(
        byte[] contentToWrite,
        bool tamper,
        string? expectedHashForValidation = null) : IFileContentStore
    {
        public int RestoreCalls { get; private set; }

        public Task<StoredFileContentDto> StoreFileAsync(string filePath, CancellationToken ct = default)
            => throw new NotSupportedException();

        public async Task<long> RestoreFileAsync(
            IReadOnlyList<StoredFileBlockDto> blocks,
            string targetPath,
            bool overwriteExisting,
            string? expectedContentHash = null,
            CancellationToken ct = default)
        {
            RestoreCalls++;

            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllBytesAsync(targetPath, contentToWrite, ct);

            // Simulate hash validation when tamper=true with a known expected hash
            if (tamper && !string.IsNullOrWhiteSpace(expectedHashForValidation ?? expectedContentHash))
            {
                var check = expectedHashForValidation ?? expectedContentHash!;
                var actual = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(contentToWrite)).ToLowerInvariant();
                if (!string.Equals(actual, check, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Restored file hash does not match the expected content hash.");
            }

            return contentToWrite.Length;
        }

        public Task<IReadOnlyList<string>> FindMissingBlocksAsync(
            IReadOnlyCollection<string> blockStorageKeys,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeRepoRepository : IRepositoryRepository
    {
        public Task<int> CreateRepositoryAsync(string name, string? description, int directoryId, CancellationToken ct = default)
            => Task.FromResult(1);

        public Task UpdateRepositoryAsync(int id, string name, string? description, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UpdateRepositoryAsync(int id, string name, string? description, bool autoCaptureFileVersions,
            bool protectCloudMetadata, IReadOnlyCollection<string> excludedPatterns,
            RepositoryRetentionPolicyDto retentionPolicy, string syncConflictStrategy,
            int syncRetryMaxAttempts, int syncRetryBaseDelaySeconds, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteRepositoryAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreRepositoryAsync(int id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<RepositoryDto?> GetRepositoryByIdAsync(int id, CancellationToken ct = default)
            => Task.FromResult<RepositoryDto?>(new RepositoryDto(
                Id: id, Name: "test", Description: null,
                DirectoryId: 1, DirectoryPath: Path.GetTempPath(),
                LinkedFormats: [], ExcludedPatterns: [],
                IsDeleted: false, FileCount: 0, VersionCount: 0,
                TotalSizeBytes: 0L, LastScannedAt: null,
                RetentionPolicy: new RepositoryRetentionPolicyDto(
                    Enabled: false, MaxAgeDays: null, MaxSnapshots: null,
                    MaxTotalSizeBytes: null, TriggerFilters: [],
                    RunIntervalMinutes: 0, MaintenanceWindowStartHour: null,
                    MaintenanceWindowEndHour: null, LastRunAtUtc: null,
                    LastStatus: null)));

        public Task<IReadOnlyList<RepositoryDto>> GetAllRepositoriesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RepositoryDto>>([]);

        public Task EnsureRepositoriesForAllDirectoriesAsync(CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeSnapshotRepository(FileVersionRestoreDto version) : IRepositorySnapshotRepository
    {
        public Task<FileVersionRestoreDto?> GetFileVersionRestoreDataAsync(long fileVersionId, CancellationToken ct = default)
            => Task.FromResult<FileVersionRestoreDto?>(version);

        public Task<SnapshotSaveResultDto> SaveSnapshotAsync(int repositoryId, string trigger, DateTime scannedAtUtc,
            IReadOnlyCollection<RepositoryScanEntryDto> entries, bool saveFileVersions = true,
            string? snapshotTitle = null, IReadOnlyCollection<string>? snapshotTags = null,
            IProgress<RepositoryScanProgressDto>? progress = null, CancellationToken ct = default,
            bool forceSnapshotCreation = false)
            => throw new NotSupportedException();

        public Task<SnapshotSaveResultDto> ApplyWorkingSnapshotDeltaAsync(int repositoryId, string trigger,
            DateTime scannedAtUtc, IReadOnlyCollection<RepositoryScanEntryDto> upsertEntries,
            IReadOnlyCollection<string> removedPaths, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<SnapshotSaveResultDto> ApplyVersionedSnapshotDeltaAsync(int repositoryId, string trigger,
            DateTime scannedAtUtc, IReadOnlyCollection<RepositoryScanEntryDto> entries,
            IReadOnlyCollection<RepositoryScanEntryDto> upsertEntries,
            IReadOnlyCollection<string> removedPaths, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<RepositoryScanEntryDto>> GetLatestEntriesAsync(int repositoryId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RepositoryScanEntryDto>>([]);

        public Task<IReadOnlyDictionary<int, IReadOnlyList<RepositoryScanEntryDto>>> GetLatestEntriesBatchAsync(
            IReadOnlyCollection<int> repositoryIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<RepositoryScanEntryDto>>>(
               new Dictionary<int, IReadOnlyList<RepositoryScanEntryDto>>());

        public Task<IReadOnlyList<FileVersionInfoDto>> GetFileVersionsAsync(int repositoryId, string relativePath,
            int take = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FileVersionInfoDto>>([]);

        public Task<RepositoryPendingChangesDto> GetPendingChangesAsync(int repositoryId, int take = 200, CancellationToken ct = default)
            => Task.FromResult(RepositoryPendingChangesDto.Empty);

        public Task<IReadOnlyList<RepositorySnapshotHistoryItemDto>> GetSnapshotHistoryAsync(int repositoryId,
            int take = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RepositorySnapshotHistoryItemDto>>([]);

        public Task<IReadOnlyDictionary<int, IReadOnlyList<RepositorySnapshotHistoryItemDto>>> GetSnapshotHistoryBatchAsync(
            IReadOnlyCollection<int> repositoryIds, int take = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<RepositorySnapshotHistoryItemDto>>>(
               new Dictionary<int, IReadOnlyList<RepositorySnapshotHistoryItemDto>>());

        public Task<IReadOnlyList<RepositorySnapshotFileChangeDto>> GetSnapshotChangedFilesAsync(int repositoryId,
            long snapshotId, int take = 1000, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RepositorySnapshotFileChangeDto>>([]);

        public Task<PendingFileDiffPreviewDto> GetPendingFileDiffPreviewAsync(int repositoryId, string relativePath,
            int maxLines = 3000, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<PendingFileDiffPreviewDto> GetFileVersionDiffPreviewAsync(long leftFileVersionId,
            long rightFileVersionId, int maxLines = 3000, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<TextDiffResultDto?> GetStoredTextDiffAsync(long leftFileVersionId, long rightFileVersionId,
            int maxLines, CancellationToken ct = default)
            => Task.FromResult<TextDiffResultDto?>(null);

        public Task SaveStoredTextDiffAsync(TextDiffResultDto diff, int maxLines, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<RepositorySnapshotRestoreDataDto?> GetSnapshotRestoreDataAsync(
            int repositoryId,
            long snapshotId,
            CancellationToken ct = default)
            => Task.FromResult<RepositorySnapshotRestoreDataDto?>(null);
    }

    private sealed class FakeScanner(RepositoryScanResultDto result) : IRepositoryScanner
    {
        public Task<RepositoryScanResultDto> ScanRepositoryAsync(int repositoryId,
            IProgress<RepositoryScanProgressDto>? progress = null,
            RepositoryScanOptionsDto? options = null, CancellationToken ct = default)
            => Task.FromResult(result);

        public Task ScanAllRepositoriesAsync(CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeCloudSync : IRepositoryCloudSyncOrchestrator
    {
        private readonly TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PushCount { get; private set; }

        public Task TryPushLatestSnapshotAsync(int repositoryId, CancellationToken ct = default)
        {
            PushCount++;
            _tcs.TrySetResult(true);
            return Task.CompletedTask;
        }

        public async Task WaitAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            await _tcs.Task.WaitAsync(cts.Token);
        }

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

        public Task ProcessPendingQueueAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<RepositoryCloudRepairResultDto> RepairRepositoryCloudDataAsync(int repositoryId, CancellationToken ct = default)
            => Task.FromResult(new RepositoryCloudRepairResultDto(false, 0, 0, 0, 0, 0));

        public Task<bool> CancelRepositorySyncAsync(int repositoryId, CancellationToken ct = default)
            => Task.FromResult(false);
    }
}
