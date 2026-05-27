using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.DTOs;
using Veyra.Domain.Entities;
using Veyra.Domain.Entities.Watched;
using Veyra.Infrastructure.Data.Persistence;
using Veyra.Infrastructure.Data.Setup;

namespace Veyra.Infrastructure.Data.Tests;

public sealed class RetentionPolicyQualityGateTests
{
    // ────────────────────────────────────────────────────────────
    // Never-delete tag protection — verified via dryRun result
    // ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("#keep")]
    [InlineData("#protected")]
    [InlineData("#never-delete")]
    [InlineData("#no-delete")]
    [InlineData("#навсегда")]
    [InlineData("#не_удалять")]
    public async Task RunRetention_DryRun_DoesNotMarkSnapshotWithProtectedTag(string protectedTag)
    {
        await using var scope = await SqliteDbScope.CreateAsync($"tag-{Guid.NewGuid():N}");
        var repositoryId = await SeedRepositoryAsync(scope.Db, maxSnapshots: 1);
        var now = DateTime.UtcNow;

        var protectedId = await SeedSnapshotAsync(scope.Db, repositoryId, now.AddDays(-30), protectedTag);
        await SeedSnapshotAsync(scope.Db, repositoryId, now, tags: null);

        var service = BuildService(scope.Db);
        var result = await service.RunRetentionAsync(repositoryId, dryRun: true);

        Assert.True(result.DryRun);

        // With only one eligible slot and the old snapshot protected by tag,
        // nothing should be marked for deletion.
        Assert.Equal(0, result.SnapshotsMarked);
    }

    [Fact]
    public async Task RunRetention_DryRun_MarksOldUnprotectedSnapshot()
    {
        await using var scope = await SqliteDbScope.CreateAsync("unprotected");
        var repositoryId = await SeedRepositoryAsync(scope.Db, maxSnapshots: 1);
        var now = DateTime.UtcNow;

        var oldId = await SeedSnapshotAsync(scope.Db, repositoryId, now.AddDays(-30), tags: null);
        await SeedSnapshotAsync(scope.Db, repositoryId, now, tags: null);

        var service = BuildService(scope.Db);
        var result = await service.RunRetentionAsync(repositoryId, dryRun: true);

        Assert.True(result.DryRun);
        Assert.True(result.SnapshotsMarked > 0, "Old unprotected snapshot should be a deletion candidate.");
    }

    // ────────────────────────────────────────────────────────────
    // Dry run must not modify database or block files
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunRetention_DryRun_DoesNotDeleteSnapshotsFromDatabase()
    {
        await using var scope = await SqliteDbScope.CreateAsync("dryrun-db");
        var repositoryId = await SeedRepositoryAsync(scope.Db, maxSnapshots: 1);
        var now = DateTime.UtcNow;

        await SeedSnapshotAsync(scope.Db, repositoryId, now.AddDays(-30), tags: null);
        await SeedSnapshotAsync(scope.Db, repositoryId, now, tags: null);

        var service = BuildService(scope.Db);
        await service.RunRetentionAsync(repositoryId, dryRun: true);

        var count = await scope.Db.Set<RepositorySnapshot>()
            .AsNoTracking()
            .CountAsync(s => s.RepositoryId == repositoryId && !s.IsDeleted);

        Assert.Equal(2, count);
    }

    // ────────────────────────────────────────────────────────────
    // Apply (non-dry-run) soft-deletes old snapshots
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunRetention_Apply_SoftDeletesOldSnapshot_WhenMaxSnapshotsExceeded()
    {
        await using var scope = await SqliteDbScope.CreateAsync("apply-soft-delete");
        var tempStore = Path.Combine(Path.GetTempPath(), "veyra-ret-apply", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempStore);

        try
        {
            var repositoryId = await SeedRepositoryAsync(scope.Db, maxSnapshots: 1);
            var now = DateTime.UtcNow;

            var oldId = await SeedSnapshotAsync(scope.Db, repositoryId, now.AddDays(-30), tags: null);
            await SeedSnapshotAsync(scope.Db, repositoryId, now, tags: null);

            var service = BuildService(scope.Db, tempStore);
            await service.RunRetentionAsync(repositoryId, dryRun: false);

            // Retention does a two-phase delete: soft-mark then hard-delete in the same pass,
            // so the row is physically removed.
            var deletedSnapshot = await scope.Db.Set<RepositorySnapshot>()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == oldId);

            Assert.Null(deletedSnapshot); // Hard-deleted by retention in the same transaction

            var remainingCount = await scope.Db.Set<RepositorySnapshot>()
                .AsNoTracking()
                .CountAsync(s => s.RepositoryId == repositoryId && !s.IsDeleted);
            Assert.Equal(1, remainingCount); // Exactly one snapshot survives
        }
        finally
        {
            if (Directory.Exists(tempStore))
                Directory.Delete(tempStore, recursive: true);
        }
    }

    // ────────────────────────────────────────────────────────────
    // Block deletion safety — block shared by retained snapshot must survive
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunRetention_Apply_KeepsBlockFile_WhenRetainedSnapshotStillReferencesIt()
    {
        await using var scope = await SqliteDbScope.CreateAsync("block-safe");
        var tempStore = Path.Combine(Path.GetTempPath(), "veyra-block-safe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempStore);

        try
        {
            var repositoryId = await SeedRepositoryAsync(scope.Db, maxSnapshots: 1);
            var now = DateTime.UtcNow;
            var sharedHash = "sha256-" + new string('a', 64);

            // Both old and new snapshots share the same block
            await SeedSnapshotWithBlockAsync(scope.Db, repositoryId, now.AddDays(-30), sharedHash, "old.txt");
            await SeedSnapshotWithBlockAsync(scope.Db, repositoryId, now, sharedHash, "current.txt");

            CreateManagedBlockFile(tempStore, sharedHash, "shared-content");

            var service = BuildService(scope.Db, tempStore);
            await service.RunRetentionAsync(repositoryId, dryRun: false);

            var blockPath = GetManagedBlockPath(tempStore, sharedHash);
            Assert.True(File.Exists(blockPath),
                "Block referenced by the retained snapshot must not be physically deleted.");
        }
        finally
        {
            if (Directory.Exists(tempStore))
                Directory.Delete(tempStore, recursive: true);
        }
    }

    // ────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────

    private static EfRepositoryRetentionService BuildService(VeyraDbContext db, string? blockStore = null)
    {
        var store = blockStore ?? Path.GetTempPath();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BlockStore:RootPath"] = store,
                ["BlockStore:ManagedPath"] = Path.Combine(store, "managed", "blocks")
            })
            .Build();

        return new EfRepositoryRetentionService(
            db,
            config,
            new FakeArchiveService(),
            NullLogger<EfRepositoryRetentionService>.Instance);
    }

    private static async Task<int> SeedRepositoryAsync(VeyraDbContext db, int? maxSnapshots = null)
    {
        var now = DateTime.UtcNow;
        var path = Path.Combine(Path.GetTempPath(), "veyra-ret", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        var dir = new WatchedDirectory
        {
            Path = path,
            IsEnabled = true,
            CreatedAt = now,
            UpdatedAt = now,
            IsDeleted = false,
            DeletedAt = null
        };
        db.Add(dir);
        await db.SaveChangesAsync();

        var repo = new Repository
        {
            Name = "ret-repo-" + Guid.NewGuid().ToString("N")[..6],
            Description = "quality gate",
            DirectoryId = dir.Id,
            RetentionPolicyOverrideEnabled = true,
            RetentionEnabled = true,
            RetentionMaxSnapshots = maxSnapshots,
            RetentionStorageMode = "delete",
            RetentionTriggerFilter = "automatic",
            CreatedAt = now,
            UpdatedAt = now,
            IsDeleted = false,
            DeletedAt = null
        };
        db.Add(repo);
        await db.SaveChangesAsync();
        return repo.Id;
    }

    private static async Task<long> SeedSnapshotAsync(
        VeyraDbContext db, int repositoryId, DateTime createdAt, string? tags)
    {
        var snapshot = new RepositorySnapshot
        {
            RepositoryId = repositoryId,
            Trigger = "auto_snapshot_rust",
            Title = "auto",
            TagsCsv = tags,
            CreatedAt = createdAt,
            TotalEntries = 0,
            FileEntries = 0,
            DirectoryEntries = 0,
            TotalFileBytes = 0,
            IsDeleted = false,
            DeletedAt = null
        };
        db.Add(snapshot);
        await db.SaveChangesAsync();
        return snapshot.Id;
    }

    private static async Task<long> SeedSnapshotWithBlockAsync(
        VeyraDbContext db, int repositoryId, DateTime createdAt, string blockHash, string relativePath)
    {
        var now = createdAt;
        var snapshot = new RepositorySnapshot
        {
            RepositoryId = repositoryId,
            Trigger = "auto_snapshot_rust",
            Title = "auto",
            CreatedAt = now,
            TotalEntries = 1,
            FileEntries = 1,
            DirectoryEntries = 0,
            TotalFileBytes = 64,
            IsDeleted = false,
            DeletedAt = null
        };
        db.Add(snapshot);
        await db.SaveChangesAsync();

        var identity = new FileIdentity
        {
            RepositoryId = repositoryId,
            RelativePath = relativePath,
            Name = relativePath,
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
            SizeBytes = 64,
            LastWriteUtc = now,
            IsDeletionMarker = false,
            CreatedAt = now,
            IsDeleted = false,
            DeletedAt = null
        };
        db.Add(version);
        await db.SaveChangesAsync();

        var block = new FileVersionBlock
        {
            FileVersionId = version.Id,
            Sequence = 0,
            BlockStorageKey = blockHash,
            LengthBytes = 64,
            StoredSizeBytes = 40,
            CreatedAt = now,
            IsDeleted = false,
            DeletedAt = null
        };
        db.Add(block);

        var link = new SnapshotFileLink
        {
            SnapshotId = snapshot.Id,
            FileIdentityId = identity.Id,
            FileVersionId = version.Id,
            CreatedAt = now,
            IsDeleted = false,
            DeletedAt = null
        };
        db.Add(link);
        await db.SaveChangesAsync();

        return snapshot.Id;
    }

    private static void CreateManagedBlockFile(string blockStore, string blockHash, string content)
    {
        var path = GetManagedBlockPath(blockStore, blockHash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string GetManagedBlockPath(string blockStore, string blockHash)
    {
        var hash = blockHash.StartsWith("sha256-", StringComparison.OrdinalIgnoreCase)
            ? blockHash["sha256-".Length..]
            : blockHash;
        return Path.Combine(blockStore, "managed", "blocks", hash[..2], hash[2..4], hash + ".bin");
    }

    private sealed class FakeArchiveService : IRepositorySnapshotArchiveService
    {
        public Task<int> EnsureSnapshotsArchivedAsync(int repositoryId, IReadOnlyCollection<long> snapshotIds, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> EnsureArchivedBlocksAvailableAsync(IReadOnlyCollection<string> blockStorageKeys, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<(int DeletedFiles, long DeletedBytes)> PruneArchivedOnlyLocalBlocksAsync(int repositoryId, CancellationToken ct = default)
            => Task.FromResult((0, 0L));
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

        public static async Task<SqliteDbScope> CreateAsync(string label)
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"veyra-ret-tests-{label}", $"{Guid.NewGuid():N}.db");
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

            for (var i = 0; i < 5; i++)
            {
                try
                {
                    if (File.Exists(_dbPath))
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
}
