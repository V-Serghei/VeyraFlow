using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Veyra.Domain.Entities;
using Veyra.Domain.Entities.Watched;
using Veyra.Infrastructure.Data.Persistence;
using Veyra.Infrastructure.Data.Setup;

namespace Veyra.Infrastructure.Data.Tests;

public sealed class RepositoryBundleQualityGateTests
{
    [Fact]
    public async Task ExportImportRoundTrip_PreservesSnapshotGraphAndBlocks()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "veyra-bundle-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var sourceStore = Path.Combine(tempRoot, "source-store");
        var targetStore = Path.Combine(tempRoot, "target-store");
        var bundlePath = Path.Combine(tempRoot, "bundle", "repo.veyra.zip");
        var importDirectory = Path.Combine(tempRoot, "import-root");

        await using var sourceScope = await SqliteDbScope.CreateAsync("source");
        await using var targetScope = await SqliteDbScope.CreateAsync("target");

        try
        {
            var blockHash = "sha256-" + new string('d', 64);
            CreateManagedBlockFile(sourceStore, blockHash, "bundle-data");

            var sourceRepositoryId = await SeedRepositoryGraphAsync(
                sourceScope.Db,
                rootPath: Path.Combine(tempRoot, "source-repo"),
                blockHash: blockHash);

            var sourceService = CreateBundleService(sourceScope.Db, sourceStore);
            var export = await sourceService.ExportRepositoryAsync(sourceRepositoryId, bundlePath);

            Assert.True(File.Exists(bundlePath));
            Assert.Equal(1, export.SnapshotCount);
            Assert.Equal(1, export.FileIdentityCount);
            Assert.Equal(1, export.FileVersionCount);
            Assert.Equal(1, export.BlockFileCount);

            var validation = await sourceService.ValidateBundleAsync(bundlePath);
            Assert.True(validation.IsValid);

            var targetService = CreateBundleService(targetScope.Db, targetStore);
            var imported = await targetService.ImportRepositoryAsync(bundlePath, importDirectory, "roundtrip-import");

            Assert.True(imported.RepositoryId > 0);
            Assert.Equal(1, imported.SnapshotCount);
            Assert.Equal(1, imported.FileIdentityCount);
            Assert.Equal(1, imported.FileVersionCount);
            Assert.Equal(1, imported.BlockFileCount);

            var importedRepo = await targetScope.Db.Set<Repository>()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == imported.RepositoryId);
            Assert.NotNull(importedRepo);

            var importedSnapshotCount = await targetScope.Db.Set<RepositorySnapshot>()
                .AsNoTracking()
                .CountAsync(s => s.RepositoryId == imported.RepositoryId);
            var importedLinkCount = await targetScope.Db.Set<SnapshotFileLink>()
                .AsNoTracking()
                .CountAsync(l => l.Snapshot.RepositoryId == imported.RepositoryId);

            Assert.Equal(1, importedSnapshotCount);
            Assert.Equal(1, importedLinkCount);

            var copiedBlockPath = ResolveManagedBlockPath(targetStore, blockHash);
            Assert.True(File.Exists(copiedBlockPath));
        }
        finally
        {
            DeleteDirectoryQuietly(tempRoot);
        }
    }

    [Fact]
    public async Task ValidateBundleAsync_ReturnsInvalidWhenManifestIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "veyra-bundle-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        await using var scope = await SqliteDbScope.CreateAsync("validate");

        try
        {
            var service = CreateBundleService(scope.Db, Path.Combine(tempRoot, "store"));
            var invalidBundle = Path.Combine(tempRoot, "invalid.zip");

            using (var zip = ZipFile.Open(invalidBundle, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("payload.txt");
                await using var stream = entry.Open();
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync("no manifest");
            }

            var result = await service.ValidateBundleAsync(invalidBundle);

            Assert.False(result.IsValid);
            Assert.Contains("manifest", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectoryQuietly(tempRoot);
        }
    }

    private static EfRepositoryBundleService CreateBundleService(VeyraDbContext db, string blockStoreRoot)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:BlockStorePath"] = blockStoreRoot
            })
            .Build();

        return new EfRepositoryBundleService(
            db,
            configuration,
            NullLogger<EfRepositoryBundleService>.Instance);
    }

    private static async Task<int> SeedRepositoryGraphAsync(VeyraDbContext db, string rootPath, string blockHash)
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
            Name = "bundle-source",
            Description = "quality gate",
            DirectoryId = watchedDirectory.Id,
            FileCount = 1,
            VersionCount = 1,
            TotalSizeBytes = 42,
            LastScannedAt = now,
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
            Title = "bundle-snapshot",
            CreatedAt = now,
            TotalEntries = 1,
            FileEntries = 1,
            DirectoryEntries = 0,
            TotalFileBytes = 42,
            IsDeleted = false,
            DeletedAt = null
        };

        db.Add(snapshot);
        await db.SaveChangesAsync();

        var entry = new RepositorySnapshotEntry
        {
            SnapshotId = snapshot.Id,
            RepositoryId = repository.Id,
            RelativePath = "doc.txt",
            ParentRelativePath = string.Empty,
            Name = "doc.txt",
            IsDirectory = false,
            Extension = ".txt",
            SizeBytes = 42,
            LastWriteUtc = now,
            ContentHashSha256 = "doc-hash",
            CreatedAt = now,
            IsDeleted = false,
            DeletedAt = null
        };

        db.Add(entry);

        var identity = new FileIdentity
        {
            RepositoryId = repository.Id,
            RelativePath = "doc.txt",
            Name = "doc.txt",
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
            ContentHashSha256 = "doc-hash",
            SizeBytes = 42,
            LastWriteUtc = now,
            IsDeletionMarker = false,
            CreatedAt = now,
            IsDeleted = false,
            DeletedAt = null
        };

        db.Add(version);
        await db.SaveChangesAsync();

        db.Add(new FileVersionBlock
        {
            FileVersionId = version.Id,
            Sequence = 0,
            BlockHashBlake3 = blockHash,
            LengthBytes = 42,
            StoredSizeBytes = 42,
            CreatedAt = now,
            IsDeleted = false,
            DeletedAt = null
        });

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
        var path = ResolveManagedBlockPath(storeRoot, blockHash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, payload);
    }

    private static string ResolveManagedBlockPath(string storeRoot, string blockHash)
    {
        var hash = blockHash["sha256-".Length..].ToLowerInvariant();
        return Path.Combine(storeRoot, "managed", "blocks", hash[..2], hash[2..4], hash + ".bin");
    }

    private static void DeleteDirectoryQuietly(string root)
    {
        if (!Directory.Exists(root))
            return;

        try
        {
            Directory.Delete(root, recursive: true);
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

        public static async Task<SqliteDbScope> CreateAsync(string suffix)
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "veyra-bundle-tests", $"{suffix}_{Guid.NewGuid():N}.db");
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
