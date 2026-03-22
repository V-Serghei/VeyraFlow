using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Abstractions.Sync;
using Veyra.Application.DTOs;
using Veyra.Desktop.Services.Sync;
using Veyra.Desktop.Services.Sync.Runtime;
using Veyra.Desktop.Services.Sync.Runtime.Models;
using Veyra.Domain.Entities;
using Veyra.Domain.Entities.Watched;
using Veyra.Infrastructure.Data.Persistence;

namespace Veyra.Desktop.Tests;

public sealed class RepositoryCloudSyncOrchestratorQualityGateTests
{
    [Fact]
    public async Task ProcessPendingQueue_ResumesUploadFromCheckpointAndCompletes()
    {
        await using var scope = await SqliteDbScope.CreateAsync();
        var blockStoreRoot = CreateTempDirectory();

        try
        {
            var blockHashA = "sha256-" + new string('a', 64);
            var blockHashB = "sha256-" + new string('b', 64);
            CreateManagedBlockFile(blockStoreRoot, blockHashA, "block-a");
            CreateManagedBlockFile(blockStoreRoot, blockHashB, "block-b");

            var repositoryId = await SeedRepositoryGraphAsync(
                scope.Db,
                rootPath: Path.Combine(blockStoreRoot, "repo"),
                blockHashes: [blockHashA, blockHashB],
                syncRetryMaxAttempts: 3,
                syncRetryBaseDelaySeconds: 5);

            var cloudSync = new CheckpointCloudSyncService(blockHashA, blockHashB);
            var orchestrator = CreateOrchestrator(scope.Db, blockStoreRoot, cloudSync);

            await orchestrator.TryPushLatestSnapshotAsync(repositoryId);

            var retryItem = await scope.Db.Set<RepositorySyncQueueItem>()
                .FirstAsync(x => x.RepositoryId == repositoryId);

            Assert.Equal(RepositorySyncQueueItem.StatusRetry, retryItem.Status);
            Assert.True(retryItem.UploadCheckpointNextIndex > 0);
            Assert.Equal(2, retryItem.UploadCheckpointTotal);

            retryItem.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);
            await scope.Db.SaveChangesAsync();

            await orchestrator.ProcessPendingQueueAsync();

            var completed = await scope.Db.Set<RepositorySyncQueueItem>()
                .AsNoTracking()
                .FirstAsync(x => x.Id == retryItem.Id);

            Assert.Equal(RepositorySyncQueueItem.StatusCompleted, completed.Status);
            Assert.Equal(0, completed.UploadCheckpointNextIndex);
            Assert.Equal(0, completed.UploadCheckpointTotal);
            Assert.Null(completed.UploadCheckpointSignature);
            Assert.Equal(2, cloudSync.UploadedHashes.Count);
            Assert.Equal(3, cloudSync.PushCalls);
            Assert.All(cloudSync.IdempotencyKeys, key => Assert.False(string.IsNullOrWhiteSpace(key)));
        }
        finally
        {
            DeleteDirectoryQuietly(blockStoreRoot);
        }
    }

    [Fact]
    public async Task ProcessPendingQueue_MarksDeadLetterWhenRetryBudgetIsExhausted()
    {
        await using var scope = await SqliteDbScope.CreateAsync();
        var blockStoreRoot = CreateTempDirectory();

        try
        {
            var blockHash = "sha256-" + new string('c', 64);
            CreateManagedBlockFile(blockStoreRoot, blockHash, "block-c");

            var repositoryId = await SeedRepositoryGraphAsync(
                scope.Db,
                rootPath: Path.Combine(blockStoreRoot, "repo"),
                blockHashes: [blockHash],
                syncRetryMaxAttempts: 1,
                syncRetryBaseDelaySeconds: 5);

            var cloudSync = new AlwaysFailPushCloudSyncService();
            var orchestrator = CreateOrchestrator(scope.Db, blockStoreRoot, cloudSync);

            await orchestrator.TryPushLatestSnapshotAsync(repositoryId);

            var queueItem = await scope.Db.Set<RepositorySyncQueueItem>()
                .AsNoTracking()
                .FirstAsync(x => x.RepositoryId == repositoryId);

            var repository = await scope.Db.Set<Repository>()
                .AsNoTracking()
                .FirstAsync(x => x.Id == repositoryId);

            Assert.Equal(RepositorySyncQueueItem.StatusDeadLetter, queueItem.Status);
            Assert.Equal(1, queueItem.AttemptCount);
            Assert.Equal("dead_letter", repository.CloudSyncLastStatus);
            Assert.False(string.IsNullOrWhiteSpace(queueItem.LastError));
        }
        finally
        {
            DeleteDirectoryQuietly(blockStoreRoot);
        }
    }

    [Fact]
    public async Task ProcessPendingQueue_UsesBatchUploadWhenProviderSupportsIt()
    {
        await using var scope = await SqliteDbScope.CreateAsync();
        var blockStoreRoot = CreateTempDirectory();

        try
        {
            var blockHashA = "sha256-" + new string('d', 64);
            var blockHashB = "sha256-" + new string('e', 64);
            CreateManagedBlockFile(blockStoreRoot, blockHashA, "block-d");
            CreateManagedBlockFile(blockStoreRoot, blockHashB, "block-e");

            var repositoryId = await SeedRepositoryGraphAsync(
                scope.Db,
                rootPath: Path.Combine(blockStoreRoot, "repo"),
                blockHashes: [blockHashA, blockHashB],
                syncRetryMaxAttempts: 3,
                syncRetryBaseDelaySeconds: 5);

            var cloudSync = new BatchUploadCloudSyncService(blockHashA, blockHashB);
            var orchestrator = CreateOrchestrator(scope.Db, blockStoreRoot, cloudSync);

            await orchestrator.TryPushLatestSnapshotAsync(repositoryId);

            var completed = await scope.Db.Set<RepositorySyncQueueItem>()
                .AsNoTracking()
                .FirstAsync(x => x.RepositoryId == repositoryId);

            Assert.Equal(RepositorySyncQueueItem.StatusCompleted, completed.Status);
            Assert.Equal(2, cloudSync.BatchUploadedHashes.Count);
            Assert.Equal(0, cloudSync.SingleUploadCalls);
            Assert.Equal(1, cloudSync.BatchUploadCalls);
            Assert.Equal(2, cloudSync.PushCalls);
        }
        finally
        {
            DeleteDirectoryQuietly(blockStoreRoot);
        }
    }

    private static RepositoryCloudSyncOrchestrator CreateOrchestrator(
        VeyraDbContext db,
        string blockStoreRoot,
        ICloudSyncService cloudSync)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:BlockStorePath"] = blockStoreRoot,
                ["CloudSync:ClientInstanceId"] = "test-client"
            })
            .Build();

        return new RepositoryCloudSyncOrchestrator(
            db,
            cloudSync,
            new FakeAuthService(),
            new FakeUserProfileRepository(),
            new FakeAccessTokenPolicyService(),
            new FakeRepositoryRepository(),
            new FakeFileContentStore(),
            new FakeMediator(),
            cfg,
            new FakeCloudSyncRuntimeControlService(),
            NullLogger<RepositoryCloudSyncOrchestrator>.Instance);
    }

    private static async Task<int> SeedRepositoryGraphAsync(
        VeyraDbContext db,
        string rootPath,
        IReadOnlyList<string> blockHashes,
        int syncRetryMaxAttempts,
        int syncRetryBaseDelaySeconds)
    {
        Directory.CreateDirectory(rootPath);
        var now = DateTime.UtcNow;

        var watchedDirectory = new WatchedDirectory
        {
            Path = rootPath,
            IsEnabled = true,
            CreatedAt = now,
            UpdatedAt = now,
            IsDeleted = false,
            DeletedAt = null
        };

        db.Add(watchedDirectory);
        await db.SaveChangesAsync();

        var repository = new Repository
        {
            Name = "sync-repo",
            Description = "quality gate",
            DirectoryId = watchedDirectory.Id,
            SyncConflictStrategy = RepositorySyncConflictStrategies.LastWriteWins,
            SyncRetryMaxAttempts = syncRetryMaxAttempts,
            SyncRetryBaseDelaySeconds = syncRetryBaseDelaySeconds,
            FileCount = 1,
            VersionCount = 1,
            TotalSizeBytes = 128,
            CreatedAt = now,
            UpdatedAt = now,
            IsDeleted = false,
            DeletedAt = null
        };

        db.Add(repository);
        await db.SaveChangesAsync();

        var snapshot = new RepositorySnapshot
        {
            RepositoryId = repository.Id,
            Trigger = "manual",
            Title = "snapshot",
            CreatedAt = now,
            TotalEntries = 1,
            FileEntries = 1,
            DirectoryEntries = 0,
            TotalFileBytes = 128,
            IsDeleted = false,
            DeletedAt = null
        };

        db.Add(snapshot);
        await db.SaveChangesAsync();

        var entry = new RepositorySnapshotEntry
        {
            SnapshotId = snapshot.Id,
            RepositoryId = repository.Id,
            RelativePath = "file.txt",
            ParentRelativePath = string.Empty,
            Name = "file.txt",
            IsDirectory = false,
            Extension = ".txt",
            SizeBytes = 128,
            LastWriteUtc = now,
            ContentHashSha256 = "content-hash",
            CreatedAt = now,
            IsDeleted = false,
            DeletedAt = null
        };

        db.Add(entry);

        var identity = new FileIdentity
        {
            RepositoryId = repository.Id,
            RelativePath = "file.txt",
            Name = "file.txt",
            Extension = ".txt",
            CreatedAt = now,
            UpdatedAt = now,
            IsDeleted = false,
            DeletedAt = null
        };

        db.Add(identity);
        await db.SaveChangesAsync();

        var version = new FileVersion
        {
            FileIdentityId = identity.Id,
            ContentHashSha256 = "content-hash",
            SizeBytes = 128,
            LastWriteUtc = now,
            IsDeletionMarker = false,
            CreatedAt = now,
            IsDeleted = false,
            DeletedAt = null
        };

        db.Add(version);
        await db.SaveChangesAsync();

        for (var i = 0; i < blockHashes.Count; i++)
        {
            db.Add(new FileVersionBlock
            {
                FileVersionId = version.Id,
                Sequence = i,
                BlockStorageKey = blockHashes[i],
                LengthBytes = 64,
                StoredSizeBytes = 64,
                CreatedAt = now,
                IsDeleted = false,
                DeletedAt = null
            });
        }

        db.Add(new SnapshotFileLink
        {
            SnapshotId = snapshot.Id,
            FileIdentityId = identity.Id,
            FileVersionId = version.Id,
            CreatedAt = now,
            IsDeleted = false,
            DeletedAt = null
        });

        await db.SaveChangesAsync();
        return repository.Id;
    }

    private static void CreateManagedBlockFile(string storeRoot, string blockHash, string payload)
    {
        var hash = blockHash["sha256-".Length..].ToLowerInvariant();
        var path = Path.Combine(storeRoot, "managed", "blocks", hash[..2], hash[2..4], hash + ".bin");
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        File.WriteAllText(path, payload);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "veyra-desktop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectoryQuietly(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class SqliteDbScope : IAsyncDisposable
    {
        private readonly string _dbPath;

        private SqliteDbScope(string dbPath, VeyraDbContext db)
        {
            _dbPath = dbPath;
            Db = db;
        }

        public VeyraDbContext Db { get; }

        public static async Task<SqliteDbScope> CreateAsync()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "veyra-sync-tests", $"{Guid.NewGuid():N}.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

            var options = new DbContextOptionsBuilder<VeyraDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            var db = new VeyraDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new SqliteDbScope(dbPath, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            SqliteConnection.ClearAllPools();

            if (!File.Exists(_dbPath))
                return;

            for (var i = 0; i < 5; i++)
            {
                try
                {
                    File.Delete(_dbPath);
                    break;
                }
                catch (IOException) when (i < 4)
                {
                    await Task.Delay(50);
                }
            }
        }
    }

    private sealed class FakeUserProfileRepository : IUserProfileRepository
    {
        private readonly UserProfileSessionDto _profile = new(
            Username: "tester",
            Email: "tester@example.com",
            CloudUserId: 10,
            CloudSessionId: 20,
            AccessToken: "valid-token",
            AccessTokenExpiresAtUtc: DateTime.UtcNow.AddHours(1),
            RefreshToken: "refresh-token",
            RefreshTokenExpiresAtUtc: DateTime.UtcNow.AddDays(7),
            RequirePasswordForSensitiveActions: false,
            LastLoginAtUtc: DateTime.UtcNow);

        public Task<string?> GetActiveUsernameAsync(CancellationToken ct = default)
            => Task.FromResult<string?>(_profile.Username);

        public Task<UserProfileSessionDto?> GetActiveProfileAsync(CancellationToken ct = default)
            => Task.FromResult<UserProfileSessionDto?>(_profile);

        public Task<IReadOnlyList<UserProfileSessionDto>> GetProfilesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UserProfileSessionDto>>([_profile]);

        public Task SaveOrUpdateProfileAsync(string username, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SaveOrUpdateProfileAsync(
            string username,
            long? cloudUserId,
            string? accessToken,
            string? email,
            long? cloudSessionId,
            string? refreshToken,
            DateTime? accessTokenExpiresAtUtc,
            DateTime? refreshTokenExpiresAtUtc,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> SetActiveProfileAsync(string username, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> SetRequirePasswordForSensitiveActionsAsync(bool enabled, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task SignOutActiveAsync(CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeAccessTokenPolicyService : IAccessTokenPolicyService
    {
        public AccessTokenPolicyEvaluationDto Evaluate(string? accessToken, DateTime? nowUtc = null)
            => new(
                AccessTokenValidityState.Valid,
                ExpiresAtUtc: DateTime.UtcNow.AddHours(1),
                RemainingLifetime: TimeSpan.FromHours(1),
                CanUseForSync: true,
                Description: "valid");

        public string GetPolicySummary() => "test";
    }

    private sealed class FakeAuthService : IAuthService
    {
        public Task<AuthSessionDto?> LoginAsync(string username, string password, CancellationToken ct = default)
            => Task.FromResult<AuthSessionDto?>(null);

        public Task<AuthSessionDto?> RegisterAsync(string username, string email, string password, CancellationToken ct = default)
            => Task.FromResult<AuthSessionDto?>(null);

        public Task<AuthSessionDto?> RefreshAsync(string refreshToken, CancellationToken ct = default)
            => Task.FromResult<AuthSessionDto?>(null);

        public Task<bool> LogoutAsync(string accessToken, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    private sealed class FakeRepositoryRepository : IRepositoryRepository
    {
        public Task<int> CreateRepositoryAsync(string name, string? description, int directoryId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task UpdateRepositoryAsync(int id, string name, string? description, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task UpdateRepositoryAsync(int id, string name, string? description, bool autoCaptureFileVersions, bool protectCloudMetadata, IReadOnlyCollection<string> excludedPatterns, RepositoryRetentionPolicyDto retentionPolicy, string syncConflictStrategy, int syncRetryMaxAttempts, int syncRetryBaseDelaySeconds, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task DeleteRepositoryAsync(int id, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task RestoreRepositoryAsync(int id, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<RepositoryDto?> GetRepositoryByIdAsync(int id, CancellationToken ct = default)
            => Task.FromResult<RepositoryDto?>(null);

        public Task<IReadOnlyList<RepositoryDto>> GetAllRepositoriesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RepositoryDto>>([]);

        public Task EnsureRepositoriesForAllDirectoriesAsync(CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeFileContentStore : IFileContentStore
    {
        public Task<StoredFileContentDto> StoreFileAsync(string filePath, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<long> RestoreFileAsync(IReadOnlyList<StoredFileBlockDto> blocks, string targetPath, bool overwriteExisting, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeMediator : IMediator
    {
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest
            => Task.CompletedTask;

        public Task Publish(object notification, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
            => Task.CompletedTask;

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeCloudSyncRuntimeControlService : ICloudSyncRuntimeControlService
    {
        public bool IsPaused => false;

        public CloudSyncRuntimeSnapshot Snapshot => new(false);

        public CancellationToken PauseToken => CancellationToken.None;

        public event Action<CloudSyncRuntimeSnapshot>? StateChanged
        {
            add { }
            remove { }
        }

        public Task SetPausedAsync(bool paused, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class CheckpointCloudSyncService(string blockHashA, string blockHashB) : ICloudSyncService
    {
        private bool _failedUploadOnce;

        public int PushCalls { get; private set; }
        public List<string> UploadedHashes { get; } = [];
        public List<string?> IdempotencyKeys { get; } = [];

        public Task<IReadOnlyList<CloudRepositoryHeaderDto>> GetRepositoriesAsync(string accessToken, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CloudRepositoryHeaderDto>>([]);

        public Task<CloudSnapshotPackageDto?> GetLatestSnapshotAsync(string accessToken, int repositoryId, CancellationToken ct = default)
            => Task.FromResult<CloudSnapshotPackageDto?>(null);

        public Task<CloudPushResultDto?> PushSnapshotAsync(
            string accessToken,
            int repositoryId,
            CloudSnapshotPackageDto package,
            string? idempotencyKey = null,
            CancellationToken ct = default)
        {
            PushCalls++;
            IdempotencyKeys.Add(idempotencyKey);

            return Task.FromResult<CloudPushResultDto?>(PushCalls switch
            {
                1 => new CloudPushResultDto(true, [blockHashA, blockHashB]),
                2 => new CloudPushResultDto(true, [blockHashA, blockHashB]),
                _ => new CloudPushResultDto(true, [])
            });
        }

        public Task<bool> BlockExistsAsync(string accessToken, string blockHash, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task UploadBlockAsync(string accessToken, string blockHash, Stream content, long? contentLength = null, CancellationToken ct = default)
        {
            if (!_failedUploadOnce && string.Equals(blockHash, blockHashB, StringComparison.OrdinalIgnoreCase))
            {
                _failedUploadOnce = true;
                throw new IOException("fault-injection: upload failure");
            }

            UploadedHashes.Add(blockHash);
            return Task.CompletedTask;
        }

        public Task<bool> DownloadBlockToFileAsync(string accessToken, string blockHash, string targetPath, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<CloudStorageMetricsDto?> GetStorageMetricsAsync(string accessToken, CancellationToken ct = default)
            => Task.FromResult<CloudStorageMetricsDto?>(null);

        public Task<CloudStorageRepairResultDto?> RepairStorageAsync(string accessToken, int scanLimit = 512, int compactLimit = 128, CancellationToken ct = default)
            => Task.FromResult<CloudStorageRepairResultDto?>(null);
    }

    private sealed class AlwaysFailPushCloudSyncService : ICloudSyncService
    {
        public Task<IReadOnlyList<CloudRepositoryHeaderDto>> GetRepositoriesAsync(string accessToken, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CloudRepositoryHeaderDto>>([]);

        public Task<CloudSnapshotPackageDto?> GetLatestSnapshotAsync(string accessToken, int repositoryId, CancellationToken ct = default)
            => Task.FromResult<CloudSnapshotPackageDto?>(null);

        public Task<CloudPushResultDto?> PushSnapshotAsync(
            string accessToken,
            int repositoryId,
            CloudSnapshotPackageDto package,
            string? idempotencyKey = null,
            CancellationToken ct = default)
            => throw new IOException("fault-injection: network drop");

        public Task<bool> BlockExistsAsync(string accessToken, string blockHash, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task UploadBlockAsync(string accessToken, string blockHash, Stream content, long? contentLength = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> DownloadBlockToFileAsync(string accessToken, string blockHash, string targetPath, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<CloudStorageMetricsDto?> GetStorageMetricsAsync(string accessToken, CancellationToken ct = default)
            => Task.FromResult<CloudStorageMetricsDto?>(null);

        public Task<CloudStorageRepairResultDto?> RepairStorageAsync(string accessToken, int scanLimit = 512, int compactLimit = 128, CancellationToken ct = default)
            => Task.FromResult<CloudStorageRepairResultDto?>(null);
    }

    private sealed class BatchUploadCloudSyncService(string blockHashA, string blockHashB) : ICloudSyncService
    {
        public int PushCalls { get; private set; }
        public int BatchUploadCalls { get; private set; }
        public int SingleUploadCalls { get; private set; }
        public List<string> BatchUploadedHashes { get; } = [];

        public Task<IReadOnlyList<CloudRepositoryHeaderDto>> GetRepositoriesAsync(string accessToken, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CloudRepositoryHeaderDto>>([]);

        public Task<CloudSnapshotPackageDto?> GetLatestSnapshotAsync(string accessToken, int repositoryId, CancellationToken ct = default)
            => Task.FromResult<CloudSnapshotPackageDto?>(null);

        public Task<CloudPushResultDto?> PushSnapshotAsync(
            string accessToken,
            int repositoryId,
            CloudSnapshotPackageDto package,
            string? idempotencyKey = null,
            CancellationToken ct = default)
        {
            PushCalls++;
            return Task.FromResult<CloudPushResultDto?>(PushCalls switch
            {
                1 => new CloudPushResultDto(true, [blockHashA, blockHashB]),
                _ => new CloudPushResultDto(true, [])
            });
        }

        public Task<bool> BlockExistsAsync(string accessToken, string blockHash, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task UploadBlockAsync(string accessToken, string blockHash, Stream content, long? contentLength = null, CancellationToken ct = default)
        {
            SingleUploadCalls++;
            return Task.CompletedTask;
        }

        public Task<CloudBatchUploadResultDto?> UploadBlockBatchAsync(
            string accessToken,
            IReadOnlyList<CloudUploadBlockItemDto> blocks,
            CancellationToken ct = default)
        {
            BatchUploadCalls++;
            BatchUploadedHashes.AddRange(blocks.Select(b => b.BlockHash));
            return Task.FromResult<CloudBatchUploadResultDto?>(new CloudBatchUploadResultDto(true, blocks.Count, 0));
        }

        public Task<bool> DownloadBlockToFileAsync(string accessToken, string blockHash, string targetPath, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<CloudStorageMetricsDto?> GetStorageMetricsAsync(string accessToken, CancellationToken ct = default)
            => Task.FromResult<CloudStorageMetricsDto?>(null);

        public Task<CloudStorageRepairResultDto?> RepairStorageAsync(string accessToken, int scanLimit = 512, int compactLimit = 128, CancellationToken ct = default)
            => Task.FromResult<CloudStorageRepairResultDto?>(null);
    }
}
