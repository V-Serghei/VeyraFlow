using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.FileVersions;
using Veyra.Application.DTOs.Repository.Integrity;
using Veyra.Application.DTOs.Repository.Scanning;
using Veyra.Domain.Entities;
using Veyra.Domain.Entities.Watched;
using Veyra.Infrastructure.Data.Persistence;
using Veyra.Infrastructure.Data.Setup;

namespace Veyra.Infrastructure.Data.Tests;

public sealed class RepositoryRecoveryQualityGateTests
{
    [Fact]
    public async Task RunStartupHealthCheckAsync_RelinksBrokenSnapshotGraph()
    {
        await using var scope = await SqliteDbScope.CreateAsync();
        var repositoryId = await SeedRepositoryWithBrokenLinksAsync(scope.Db);

        var service = new EfRepositoryRecoveryService(
            scope.Db,
            new FakeIntegrityService(),
            new FakeScanner(),
            new FakeContentStore(),
            NullLogger<EfRepositoryRecoveryService>.Instance);

        var results = await service.RunStartupHealthCheckAsync();

        var result = Assert.Single(results, r => r.RepositoryId == repositoryId);
        Assert.Equal("startup_health_check", result.Action);
        Assert.True(result.Success);
        Assert.True(result.AffectedRows > 0);

        var links = await scope.Db.Set<SnapshotFileLink>()
            .AsNoTracking()
            .Where(l => l.Snapshot.RepositoryId == repositoryId)
            .ToListAsync();

        Assert.Single(links);
    }

    [Fact]
    public async Task ReindexRepositoryAsync_UsesRecoveryTriggerAndReturnsSummary()
    {
        await using var scope = await SqliteDbScope.CreateAsync();
        var repositoryId = await SeedRepositoryWithoutSnapshotsAsync(scope.Db);

        var scanner = new FakeScanner
        {
            Result = new RepositoryScanResultDto(
                TotalEntries: 11,
                FileEntries: 7,
                DirectoryEntries: 4,
                Trigger: "recovery_reindex")
        };

        var service = new EfRepositoryRecoveryService(
            scope.Db,
            new FakeIntegrityService(),
            scanner,
            new FakeContentStore(),
            NullLogger<EfRepositoryRecoveryService>.Instance);

        var result = await service.ReindexRepositoryAsync(repositoryId);

        Assert.Equal("reindex", result.Action);
        Assert.True(result.Success);
        Assert.Equal(11, result.AffectedRows);
        Assert.Contains("recovery_reindex", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RepairRepositoryAsync_PropagatesIntegrityWarnings()
    {
        await using var scope = await SqliteDbScope.CreateAsync();
        var repositoryId = await SeedRepositoryWithoutSnapshotsAsync(scope.Db);

        var integrity = new FakeIntegrityService
        {
            Result = new RepositoryIntegrityRunResultDto(
                RepositoryId: repositoryId,
                RepairAttempted: true,
                StartedAtUtc: DateTime.UtcNow.AddMinutes(-1),
                FinishedAtUtc: DateTime.UtcNow,
                TotalBlockReferences: 10,
                UniqueBlockCount: 8,
                VerifiedBlockCount: 8,
                MissingBlockCount: 2,
                CorruptedBlockCount: 0,
                RepairedBlockCount: 1,
                UnresolvedIssueCount: 1,
                Issues: [],
                Summary: "Integrity check finished with unresolved issues.")
        };

        var service = new EfRepositoryRecoveryService(
            scope.Db,
            integrity,
            new FakeScanner(),
            new FakeContentStore(),
            NullLogger<EfRepositoryRecoveryService>.Instance);

        var result = await service.RepairRepositoryAsync(repositoryId);

        Assert.Equal("repair", result.Action);
        Assert.False(result.Success);
        Assert.Contains("warnings", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Integrity check finished", string.Join("\n", result.Messages), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<int> SeedRepositoryWithoutSnapshotsAsync(VeyraDbContext db)
    {
        var now = DateTime.UtcNow;
        var path = Path.Combine(Path.GetTempPath(), "veyra-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        var watchedDirectory = new WatchedDirectory
        {
            Path = path,
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
            Name = "recovery-repo",
            Description = "quality gate",
            DirectoryId = watchedDirectory.Id,
            CreatedAt = now,
            UpdatedAt = now,
            IsDeleted = false,
            DeletedAt = null
        };

        db.Add(repository);
        await db.SaveChangesAsync();
        return repository.Id;
    }

    private static async Task<int> SeedRepositoryWithBrokenLinksAsync(VeyraDbContext db)
    {
        var repositoryId = await SeedRepositoryWithoutSnapshotsAsync(db);
        var now = DateTime.UtcNow;

        var snapshot = new RepositorySnapshot
        {
            RepositoryId = repositoryId,
            Trigger = "manual",
            Title = "needs-relink",
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

        db.Add(new RepositorySnapshotEntry
        {
            SnapshotId = snapshot.Id,
            RepositoryId = repositoryId,
            RelativePath = "notes.txt",
            ParentRelativePath = string.Empty,
            Name = "notes.txt",
            IsDirectory = false,
            Extension = ".txt",
            SizeBytes = 64,
            LastWriteUtc = now,
            ContentHashSha256 = "notes-hash",
            CreatedAt = now,
            IsDeleted = false,
            DeletedAt = null
        });

        var identity = new FileIdentity
        {
            RepositoryId = repositoryId,
            RelativePath = "notes.txt",
            Name = "notes.txt",
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
            ContentHashSha256 = "notes-hash",
            SizeBytes = 64,
            LastWriteUtc = now,
            IsDeletionMarker = false,
            CreatedAt = now,
            IsDeleted = false,
            DeletedAt = null
        };

        db.Add(version);
        await db.SaveChangesAsync();

        await db.SaveChangesAsync();
        return repositoryId;
    }

    private sealed class FakeIntegrityService : IRepositoryIntegrityService
    {
        public RepositoryIntegrityRunResultDto Result { get; set; } = new(
            RepositoryId: 0,
            RepairAttempted: false,
            StartedAtUtc: DateTime.UtcNow,
            FinishedAtUtc: DateTime.UtcNow,
            TotalBlockReferences: 0,
            UniqueBlockCount: 0,
            VerifiedBlockCount: 0,
            MissingBlockCount: 0,
            CorruptedBlockCount: 0,
            RepairedBlockCount: 0,
            UnresolvedIssueCount: 0,
            Issues: [],
            Summary: "Integrity is healthy.");

        public Task<RepositoryIntegrityRunResultDto> VerifyRepositoryAsync(
            int repositoryId,
            bool repairFromCloud = false,
            int maxIssueSamples = 200,
            IProgress<RepositoryIntegrityProgressDto>? progress = null,
            CancellationToken ct = default)
        {
            if (Result.RepositoryId == 0)
                return Task.FromResult(Result with { RepositoryId = repositoryId });

            return Task.FromResult(Result);
        }

        public Task<IReadOnlyList<RepositoryIntegrityRunResultDto>> VerifyDueRepositoriesAsync(
            int intervalMinutes,
            bool repairFromCloud = false,
            int maxIssueSamples = 100,
            IProgress<RepositoryIntegrityProgressDto>? progress = null,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RepositoryIntegrityRunResultDto>>([]);
    }

    private sealed class FakeScanner : IRepositoryScanner
    {
        public RepositoryScanResultDto Result { get; set; } = new(
            TotalEntries: 0,
            FileEntries: 0,
            DirectoryEntries: 0,
            Trigger: "recovery_reindex");

        public Task<RepositoryScanResultDto> ScanRepositoryAsync(
            int repositoryId,
            IProgress<RepositoryScanProgressDto>? progress = null,
            RepositoryScanOptionsDto? options = null,
            CancellationToken ct = default)
            => Task.FromResult(Result);

        public Task ScanAllRepositoriesAsync(CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeContentStore : IFileContentStore
    {
        public Task<StoredFileContentDto> StoreFileAsync(string filePath, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<long> RestoreFileAsync(
            IReadOnlyList<StoredFileBlockDto> blocks,
            string targetPath,
            bool overwriteExisting,
            string? expectedContentHash = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> FindMissingBlocksAsync(
            IReadOnlyCollection<string> blockStorageKeys,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
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
            var dbPath = Path.Combine(Path.GetTempPath(), "veyra-recovery-tests", $"{Guid.NewGuid():N}.db");
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
}
