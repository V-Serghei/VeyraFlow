using Microsoft.Extensions.Logging.Abstractions;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Abstractions.Sync;
using Veyra.Application.Commands.Repository;
using Veyra.Application.DTOs;

namespace Veyra.Application.Tests;

public sealed class RepositorySnapshotRestoreHandlerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "veyra-repository-rollback-tests",
        Guid.NewGuid().ToString("N"));

    public RepositorySnapshotRestoreHandlerTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task RollbackToSnapshot_CreatesNewSnapshot()
    {
        var payloads = CreatePayloads(("file.txt", "selected snapshot"));
        var restoreData = CreateRestoreData(15, payloads);
        var snapshots = new FakeSnapshotRepository(restoreData, [CreateEntry("file.txt", "current")], initialSnapshotCount: 3);
        var cloud = new FakeCloudSync();
        var handler = CreateHandler(snapshots, new FakeContentStore(payloads), cloud);

        var result = await handler.Handle(
            new RestoreRepositorySnapshotCommand(1, 15, RepositorySnapshotRestoreMode.Rollback),
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Value);
        Assert.True(result.Value!.CreatedSnapshot);
        Assert.Equal(4, snapshots.SnapshotCount);
        Assert.Equal("repository_rollback_snapshot", snapshots.LastTrigger);
        Assert.Equal("Rollback to snapshot 15", snapshots.LastTitle);
        Assert.True(snapshots.LastForceSnapshotCreation);
        Assert.Equal(1, cloud.PushCount);
    }

    [Fact]
    public async Task RollbackToSnapshot_PreservesOldSnapshots()
    {
        var payloads = CreatePayloads(("file.txt", "selected snapshot"));
        var snapshots = new FakeSnapshotRepository(
            CreateRestoreData(15, payloads),
            [CreateEntry("file.txt", "current")],
            initialSnapshotCount: 3);
        var handler = CreateHandler(snapshots, new FakeContentStore(payloads), new FakeCloudSync());

        var result = await handler.Handle(
            new RestoreRepositorySnapshotCommand(1, 15, RepositorySnapshotRestoreMode.Rollback),
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(3, snapshots.InitialSnapshotCount);
        Assert.Equal(1, snapshots.SaveCalls);
        Assert.Equal(4, snapshots.SnapshotCount);
    }

    [Fact]
    public async Task RollbackToSnapshot_RestoresFilesFromSelectedSnapshot()
    {
        File.WriteAllText(Path.Combine(_root, "file.txt"), "current");
        File.WriteAllText(Path.Combine(_root, "extra.txt"), "extra");

        var payloads = CreatePayloads(("file.txt", "selected snapshot"));
        var restoreData = CreateRestoreData(15, payloads);
        var snapshots = new FakeSnapshotRepository(
            restoreData,
            [CreateEntry("file.txt", "current"), CreateEntry("extra.txt", "extra")],
            initialSnapshotCount: 3);
        var handler = CreateHandler(snapshots, new FakeContentStore(payloads), new FakeCloudSync());

        var result = await handler.Handle(
            new RestoreRepositorySnapshotCommand(1, 15, RepositorySnapshotRestoreMode.Rollback),
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal("selected snapshot", File.ReadAllText(Path.Combine(_root, "file.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "extra.txt")));
        Assert.NotNull(result.Value?.BackupDirectoryPath);
        Assert.False(result.Value!.BackupDirectoryPath!.StartsWith(_root, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(result.Value!.BackupDirectoryPath!, "extra.txt")));
    }

    [Fact]
    public async Task RollbackToSnapshot_IgnoresOfficeLockFiles()
    {
        var payloads = CreatePayloads(
            ("file.txt", "selected snapshot"),
            ("~$locked.docx", "word lock"));
        var snapshots = new FakeSnapshotRepository(
            CreateRestoreData(15, payloads),
            [CreateEntry("file.txt", "current")],
            initialSnapshotCount: 3);
        var handler = CreateHandler(snapshots, new FakeContentStore(payloads), new FakeCloudSync());

        var result = await handler.Handle(
            new RestoreRepositorySnapshotCommand(1, 15, RepositorySnapshotRestoreMode.Rollback),
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.False(File.Exists(Path.Combine(_root, "~$locked.docx")));
        Assert.DoesNotContain(snapshots.LastEntries, entry => entry.RelativePath.StartsWith("~$", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RestoreAsCopies_DoesNotOverwriteCurrentFiles()
    {
        File.WriteAllText(Path.Combine(_root, "file.txt"), "current");

        var payloads = CreatePayloads(("file.txt", "selected snapshot"));
        var snapshots = new FakeSnapshotRepository(
            CreateRestoreData(15, payloads),
            [CreateEntry("file.txt", "current")],
            initialSnapshotCount: 3);
        var handler = CreateHandler(snapshots, new FakeContentStore(payloads), new FakeCloudSync());

        var result = await handler.Handle(
            new RestoreRepositorySnapshotCommand(1, 15, RepositorySnapshotRestoreMode.Copies),
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal("current", File.ReadAllText(Path.Combine(_root, "file.txt")));
        Assert.NotNull(result.Value?.RestoreDirectoryPath);
        Assert.Equal(
            "selected snapshot",
            File.ReadAllText(Path.Combine(result.Value!.RestoreDirectoryPath!, "file.txt")));
    }

    [Fact]
    public async Task RestoreAsCopies_DoesNotCreateSnapshotAutomatically()
    {
        var payloads = CreatePayloads(("file.txt", "selected snapshot"));
        var snapshots = new FakeSnapshotRepository(
            CreateRestoreData(15, payloads),
            [CreateEntry("file.txt", "current")],
            initialSnapshotCount: 3);
        var cloud = new FakeCloudSync();
        var handler = CreateHandler(snapshots, new FakeContentStore(payloads), cloud);

        var result = await handler.Handle(
            new RestoreRepositorySnapshotCommand(1, 15, RepositorySnapshotRestoreMode.Copies),
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.False(result.Value!.CreatedSnapshot);
        Assert.Equal(0, snapshots.SaveCalls);
        Assert.Equal(3, snapshots.SnapshotCount);
        Assert.Equal(0, cloud.PushCount);
    }

    [Fact]
    public async Task MissingBlocks_PreventRollbackBeforeFilesAreChanged()
    {
        File.WriteAllText(Path.Combine(_root, "file.txt"), "current");

        var payloads = CreatePayloads(("file.txt", "selected snapshot"));
        var blockKey = CreateBlockKey("file.txt");
        var store = new FakeContentStore(payloads, missingBlocks: [blockKey]);
        var snapshots = new FakeSnapshotRepository(
            CreateRestoreData(15, payloads),
            [CreateEntry("file.txt", "current")],
            initialSnapshotCount: 3);
        var handler = CreateHandler(snapshots, store, new FakeCloudSync());

        var result = await handler.Handle(
            new RestoreRepositorySnapshotCommand(1, 15, RepositorySnapshotRestoreMode.Rollback),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("missing", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("current", File.ReadAllText(Path.Combine(_root, "file.txt")));
        Assert.Equal(0, store.RestoreCalls);
        Assert.Equal(0, snapshots.SaveCalls);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    private RestoreRepositorySnapshotHandler CreateHandler(
        FakeSnapshotRepository snapshots,
        FakeContentStore contentStore,
        IRepositoryCloudSyncOrchestrator cloudSync)
        => new(
            new FakeRepositoryRepository(_root),
            snapshots,
            contentStore,
            cloudSync,
            NullLogger<RestoreRepositorySnapshotHandler>.Instance);

    private static RepositorySnapshotRestoreDataDto CreateRestoreData(
        long snapshotId,
        IReadOnlyDictionary<string, byte[]> payloads)
    {
        var entries = payloads
            .Select(pair => CreateEntry(pair.Key, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pair.Value)).ToLowerInvariant()))
            .ToList();
        var versions = payloads
            .Select((pair, index) => CreateVersion(index + 1, pair.Key, pair.Value))
            .ToList();

        return new RepositorySnapshotRestoreDataDto(
            new RepositorySnapshotHistoryItemDto(
                SnapshotId: snapshotId,
                Title: "Selected snapshot",
                CreatedAtUtc: DateTime.UtcNow.AddMinutes(-5),
                Kind: "manual",
                IsArchived: false,
                Trigger: "manual_snapshot",
                ChangedFilesCount: versions.Count),
            entries,
            versions);
    }

    private static RepositoryScanEntryDto CreateEntry(string relativePath, string hash)
        => new(
            RelativePath: relativePath,
            ParentRelativePath: null,
            Name: Path.GetFileName(relativePath),
            IsDirectory: false,
            Extension: Path.GetExtension(relativePath),
            SizeBytes: hash.Length,
            LastWriteUtc: DateTime.UtcNow,
            ContentHashSha256: hash);

    private static FileVersionRestoreDto CreateVersion(int id, string relativePath, byte[] payload)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();
        var blockKey = CreateBlockKey(relativePath);

        return new FileVersionRestoreDto(
            FileVersionId: id,
            RepositoryId: 1,
            RelativePath: relativePath,
            Extension: Path.GetExtension(relativePath),
            SizeBytes: payload.Length,
            IsDeletionMarker: false,
            ContentHashSha256: hash,
            Blocks: [new StoredFileBlockDto(
                Sequence: 0,
                BlockStorageKey: blockKey,
                LengthBytes: payload.Length,
                StoredSizeBytes: payload.Length)]);
    }

    private static Dictionary<string, byte[]> CreatePayloads(params (string RelativePath, string Content)[] files)
        => files.ToDictionary(
            file => file.RelativePath,
            file => System.Text.Encoding.UTF8.GetBytes(file.Content),
            StringComparer.OrdinalIgnoreCase);

    private static string CreateBlockKey(string relativePath)
    {
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(relativePath)))
            .ToLowerInvariant();

        return "sha256-" + hash;
    }

    private sealed class FakeContentStore(
        IReadOnlyDictionary<string, byte[]> payloads,
        IReadOnlyCollection<string>? missingBlocks = null) : IFileContentStore
    {
        private readonly Dictionary<string, byte[]> _payloadsByBlockKey = payloads.ToDictionary(
            pair => CreateBlockKey(pair.Key),
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _missingBlocks = new(missingBlocks ?? [], StringComparer.OrdinalIgnoreCase);

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
            if (!overwriteExisting && File.Exists(targetPath))
                throw new IOException("Target file already exists.");

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using var output = File.Create(targetPath);
            foreach (var block in blocks.OrderBy(static x => x.Sequence))
            {
                var payload = _payloadsByBlockKey[block.BlockStorageKey];
                await output.WriteAsync(payload, ct);
            }

            return output.Length;
        }

        public Task<IReadOnlyList<string>> FindMissingBlocksAsync(
            IReadOnlyCollection<string> blockStorageKeys,
            CancellationToken ct = default)
        {
            var missing = blockStorageKeys
                .Where(key => _missingBlocks.Contains(key) || !_payloadsByBlockKey.ContainsKey(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Task.FromResult<IReadOnlyList<string>>(missing);
        }
    }

    private sealed class FakeRepositoryRepository(string rootPath) : IRepositoryRepository
    {
        public Task<int> CreateRepositoryAsync(string name, string? description, int directoryId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task UpdateRepositoryAsync(int id, string name, string? description, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task UpdateRepositoryAsync(int id, string name, string? description, bool autoCaptureFileVersions,
            bool protectCloudMetadata, IReadOnlyCollection<string> excludedPatterns,
            RepositoryRetentionPolicyDto retentionPolicy, string syncConflictStrategy,
            int syncRetryMaxAttempts, int syncRetryBaseDelaySeconds, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task DeleteRepositoryAsync(int id, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task RestoreRepositoryAsync(int id, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<RepositoryDto?> GetRepositoryByIdAsync(int id, CancellationToken ct = default)
            => Task.FromResult<RepositoryDto?>(new RepositoryDto(
                Id: id,
                Name: "rollback-test",
                Description: null,
                DirectoryId: 1,
                DirectoryPath: rootPath,
                LinkedFormats: [],
                ExcludedPatterns: [],
                IsDeleted: false,
                FileCount: 0,
                VersionCount: 0,
                TotalSizeBytes: 0,
                LastScannedAt: null,
                RetentionPolicy: new RepositoryRetentionPolicyDto(
                    Enabled: false,
                    MaxAgeDays: null,
                    MaxSnapshots: null,
                    MaxTotalSizeBytes: null,
                    TriggerFilters: [],
                    RunIntervalMinutes: 0,
                    MaintenanceWindowStartHour: null,
                    MaintenanceWindowEndHour: null,
                    LastRunAtUtc: null,
                    LastStatus: null)));

        public Task<IReadOnlyList<RepositoryDto>> GetAllRepositoriesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RepositoryDto>>([]);

        public Task EnsureRepositoriesForAllDirectoriesAsync(CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeSnapshotRepository(
        RepositorySnapshotRestoreDataDto restoreData,
        IReadOnlyList<RepositoryScanEntryDto> latestEntries,
        int initialSnapshotCount) : IRepositorySnapshotRepository
    {
        public int InitialSnapshotCount { get; } = initialSnapshotCount;
        public int SnapshotCount { get; private set; } = initialSnapshotCount;
        public int SaveCalls { get; private set; }
        public string? LastTrigger { get; private set; }
        public string? LastTitle { get; private set; }
        public bool LastForceSnapshotCreation { get; private set; }
        public IReadOnlyList<RepositoryScanEntryDto> LastEntries { get; private set; } = [];

        public Task<SnapshotSaveResultDto> SaveSnapshotAsync(
            int repositoryId,
            string trigger,
            DateTime scannedAtUtc,
            IReadOnlyCollection<RepositoryScanEntryDto> entries,
            bool saveFileVersions = true,
            string? snapshotTitle = null,
            IReadOnlyCollection<string>? snapshotTags = null,
            IProgress<RepositoryScanProgressDto>? progress = null,
            CancellationToken ct = default,
            bool forceSnapshotCreation = false)
        {
            SaveCalls++;
            SnapshotCount++;
            LastTrigger = trigger;
            LastTitle = snapshotTitle;
            LastForceSnapshotCreation = forceSnapshotCreation;
            LastEntries = entries.ToList();
            return Task.FromResult(SnapshotSaveResultDto.Created());
        }

        public Task<IReadOnlyList<RepositoryScanEntryDto>> GetLatestEntriesAsync(int repositoryId, CancellationToken ct = default)
            => Task.FromResult(latestEntries);

        public Task<RepositorySnapshotRestoreDataDto?> GetSnapshotRestoreDataAsync(
            int repositoryId,
            long snapshotId,
            CancellationToken ct = default)
            => Task.FromResult<RepositorySnapshotRestoreDataDto?>(restoreData);

        public Task<FileVersionRestoreDto?> GetFileVersionRestoreDataAsync(long fileVersionId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<SnapshotSaveResultDto> ApplyWorkingSnapshotDeltaAsync(int repositoryId, string trigger, DateTime scannedAtUtc, IReadOnlyCollection<RepositoryScanEntryDto> upsertEntries, IReadOnlyCollection<string> removedPaths, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<SnapshotSaveResultDto> ApplyVersionedSnapshotDeltaAsync(int repositoryId, string trigger, DateTime scannedAtUtc, IReadOnlyCollection<RepositoryScanEntryDto> entries, IReadOnlyCollection<RepositoryScanEntryDto> upsertEntries, IReadOnlyCollection<string> removedPaths, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<int, IReadOnlyList<RepositoryScanEntryDto>>> GetLatestEntriesBatchAsync(IReadOnlyCollection<int> repositoryIds, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<FileVersionInfoDto>> GetFileVersionsAsync(int repositoryId, string relativePath, int take = 50, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<RepositoryPendingChangesDto> GetPendingChangesAsync(int repositoryId, int take = 200, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<RepositorySnapshotHistoryItemDto>> GetSnapshotHistoryAsync(int repositoryId, int take = 100, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<int, IReadOnlyList<RepositorySnapshotHistoryItemDto>>> GetSnapshotHistoryBatchAsync(IReadOnlyCollection<int> repositoryIds, int take = 100, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<RepositorySnapshotFileChangeDto>> GetSnapshotChangedFilesAsync(int repositoryId, long snapshotId, int take = 1000, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<PendingFileDiffPreviewDto> GetPendingFileDiffPreviewAsync(int repositoryId, string relativePath, int maxLines = 3000, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<PendingFileDiffPreviewDto> GetFileVersionDiffPreviewAsync(long leftFileVersionId, long rightFileVersionId, int maxLines = 3000, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<TextDiffResultDto?> GetStoredTextDiffAsync(long leftFileVersionId, long rightFileVersionId, int maxLines, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task SaveStoredTextDiffAsync(TextDiffResultDto diff, int maxLines, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeCloudSync : IRepositoryCloudSyncOrchestrator
    {
        public int PushCount { get; private set; }

        public Task TryPushLatestSnapshotAsync(int repositoryId, CancellationToken ct = default)
        {
            PushCount++;
            return Task.CompletedTask;
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

        public Task ProcessPendingQueueAsync(CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<RepositoryCloudRepairResultDto> RepairRepositoryCloudDataAsync(int repositoryId, CancellationToken ct = default)
            => Task.FromResult(new RepositoryCloudRepairResultDto(false, 0, 0, 0, 0, 0));

        public Task<bool> CancelRepositorySyncAsync(int repositoryId, CancellationToken ct = default)
            => Task.FromResult(false);
    }
}
