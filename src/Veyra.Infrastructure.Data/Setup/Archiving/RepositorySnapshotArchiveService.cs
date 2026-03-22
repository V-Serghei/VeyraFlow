using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;
using Veyra.Domain.Entities;
using Veyra.Infrastructure.Data.Persistence;

namespace Veyra.Infrastructure.Data.Setup;

public sealed class RepositorySnapshotArchiveService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<RepositorySnapshotArchiveService> log)
    : IRepositorySnapshotArchiveService
{
    private const string ManifestEntryName = "manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public async Task<int> EnsureSnapshotsArchivedAsync(
        int repositoryId,
        IReadOnlyCollection<long> snapshotIds,
        CancellationToken ct = default)
    {
        if (repositoryId <= 0 || snapshotIds.Count == 0)
            return 0;

        var normalizedIds = snapshotIds
            .Where(static id => id > 0)
            .Distinct()
            .ToList();

        if (normalizedIds.Count == 0)
            return 0;

        var archiveRoot = RepositorySnapshotArchivePathResolver.ResolveArchiveRoot(configuration);
        var storeRoot = RepositoryBundleBlockPathResolver.ResolveStoreRoot(configuration);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VeyraDbContext>();

        var snapshots = await db.Set<RepositorySnapshot>()
            .IgnoreQueryFilters()
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted && normalizedIds.Contains(s.Id))
            .OrderBy(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .ToListAsync(ct);

        var archivedCount = 0;

        foreach (var snapshot in snapshots)
        {
            ct.ThrowIfCancellationRequested();

            if (snapshot.IsArchived && !string.IsNullOrWhiteSpace(snapshot.ArchiveFilePath))
                continue;

            var blockRows = await db.Set<FileVersionBlock>()
                .IgnoreQueryFilters()
                .Where(b => !b.IsDeleted)
                .Where(b => b.FileVersion.SnapshotLinks.Any(l => !l.IsDeleted && l.SnapshotId == snapshot.Id))
                .Select(b => new RepositorySnapshotArchiveManifestBlock
                {
                    BlockStorageKey = b.BlockStorageKey,
                    LengthBytes = b.LengthBytes,
                    StoredSizeBytes = b.StoredSizeBytes
                })
                .Distinct()
                .ToListAsync(ct);

            var manifest = new RepositorySnapshotArchiveManifest
            {
                RepositoryId = repositoryId,
                SnapshotId = snapshot.Id,
                SnapshotCreatedAtUtc = snapshot.CreatedAt,
                SnapshotTrigger = snapshot.Trigger,
                SnapshotTitle = snapshot.Title
            };

            var missingBlock = false;
            for (var i = 0; i < blockRows.Count; i++)
            {
                var block = blockRows[i];
                var localPath = RepositoryBundleBlockPathResolver.ResolveLocalBlockPath(storeRoot, block.BlockStorageKey);
                if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
                {
                    missingBlock = true;
                    log.LogWarning(
                        "Snapshot archive creation skipped because a block file is missing. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}. Block {BlockStorageKey}",
                        repositoryId,
                        snapshot.Id,
                        block.BlockStorageKey);
                    break;
                }

                block.EntryName = $"blocks/{i:000000}_{Path.GetFileName(localPath)}";
                manifest.Blocks.Add(block);
            }

            if (missingBlock)
                continue;

            var relativePath = RepositorySnapshotArchivePathResolver.BuildArchiveRelativePath(
                repositoryId,
                snapshot.Id,
                snapshot.CreatedAt,
                snapshot.Title);
            var fullArchivePath = RepositorySnapshotArchivePathResolver.ResolveArchivePath(archiveRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullArchivePath)!);

            if (File.Exists(fullArchivePath))
                File.Delete(fullArchivePath);

            using (var archiveStream = new FileStream(fullArchivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                var manifestEntry = zip.CreateEntry(ManifestEntryName, CompressionLevel.SmallestSize);
                await using (var manifestStream = manifestEntry.Open())
                {
                    await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonOptions, ct);
                }

                foreach (var block in manifest.Blocks)
                {
                    var localPath = RepositoryBundleBlockPathResolver.ResolveLocalBlockPath(storeRoot, block.BlockStorageKey);
                    if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
                        continue;

                    var entry = zip.CreateEntry(block.EntryName, CompressionLevel.SmallestSize);
                    await using var output = entry.Open();
                    await using var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    await input.CopyToAsync(output, ct);
                }
            }

            snapshot.IsArchived = true;
            snapshot.ArchivedAt = DateTime.UtcNow;
            snapshot.ArchiveFilePath = relativePath;
            snapshot.ArchiveFileSizeBytes = new FileInfo(fullArchivePath).Length;
            archivedCount++;
        }

        if (archivedCount > 0)
            await db.SaveChangesAsync(ct);

        return archivedCount;
    }

    public async Task<int> EnsureArchivedBlocksAvailableAsync(
        IReadOnlyCollection<string> blockStorageKeys,
        CancellationToken ct = default)
    {
        var normalizedKeys = blockStorageKeys
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Select(static key => key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedKeys.Count == 0)
            return 0;

        var storeRoot = RepositoryBundleBlockPathResolver.ResolveStoreRoot(configuration);
        var missingKeys = normalizedKeys
            .Where(key =>
            {
                var localPath = RepositoryBundleBlockPathResolver.ResolveLocalBlockPath(storeRoot, key);
                return string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath);
            })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (missingKeys.Count == 0)
            return 0;

        var archiveRoot = RepositorySnapshotArchivePathResolver.ResolveArchiveRoot(configuration);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VeyraDbContext>();

        var candidateSnapshots = await db.Set<RepositorySnapshot>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => !s.IsDeleted && s.IsArchived && s.ArchiveFilePath != null)
            .Where(s => s.FileLinks.Any(l => !l.IsDeleted && l.FileVersion.Blocks.Any(b => !b.IsDeleted && missingKeys.Contains(b.BlockStorageKey))))
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => new
            {
                s.Id,
                s.ArchiveFilePath
            })
            .ToListAsync(ct);

        var restoredCount = 0;

        foreach (var snapshot in candidateSnapshots)
        {
            ct.ThrowIfCancellationRequested();

            if (missingKeys.Count == 0)
                break;

            var fullArchivePath = RepositorySnapshotArchivePathResolver.ResolveArchivePath(archiveRoot, snapshot.ArchiveFilePath!);
            if (!File.Exists(fullArchivePath))
            {
                log.LogWarning(
                    "Archived snapshot payload is missing locally. SnapshotId {SnapshotId}. ArchivePath {ArchivePath}",
                    snapshot.Id,
                    fullArchivePath);
                continue;
            }

            using var zip = ZipFile.OpenRead(fullArchivePath);
            var manifest = await ReadManifestAsync(zip, ct);
            if (manifest is null)
                continue;

            foreach (var block in manifest.Blocks)
            {
                if (!missingKeys.Contains(block.BlockStorageKey))
                    continue;

                var entry = zip.GetEntry(block.EntryName);
                if (entry is null)
                    continue;

                var destinationPath = RepositoryBundleBlockPathResolver.ResolveLocalBlockPath(storeRoot, block.BlockStorageKey);
                if (string.IsNullOrWhiteSpace(destinationPath))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

                if (!File.Exists(destinationPath))
                {
                    await using var input = entry.Open();
                    await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await input.CopyToAsync(output, ct);
                    restoredCount++;
                }

                missingKeys.Remove(block.BlockStorageKey);
                if (missingKeys.Count == 0)
                    break;
            }
        }

        return restoredCount;
    }

    public async Task<(int DeletedFiles, long DeletedBytes)> PruneArchivedOnlyLocalBlocksAsync(
        int repositoryId,
        CancellationToken ct = default)
    {
        if (repositoryId <= 0)
            return (0, 0);

        var archiveRoot = RepositorySnapshotArchivePathResolver.ResolveArchiveRoot(configuration);
        var storeRoot = RepositoryBundleBlockPathResolver.ResolveStoreRoot(configuration);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VeyraDbContext>();

        var archivedSnapshots = await db.Set<RepositorySnapshot>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted && s.IsArchived && s.ArchiveFilePath != null)
            .Select(s => new { s.Id, s.ArchiveFilePath })
            .ToListAsync(ct);

        var validArchivedSnapshotIds = archivedSnapshots
            .Where(s => File.Exists(RepositorySnapshotArchivePathResolver.ResolveArchivePath(archiveRoot, s.ArchiveFilePath!)))
            .Select(s => s.Id)
            .ToList();

        if (validArchivedSnapshotIds.Count == 0)
            return (0, 0);

        var activeBlockKeys = await db.Set<FileVersionBlock>()
            .IgnoreQueryFilters()
            .Where(b => !b.IsDeleted)
            .Where(b => b.FileVersion.SnapshotLinks.Any(l =>
                !l.IsDeleted
                && l.Snapshot.RepositoryId == repositoryId
                && !l.Snapshot.IsDeleted
                && !l.Snapshot.IsArchived))
            .Select(b => b.BlockStorageKey)
            .Distinct()
            .ToListAsync(ct);

        var archivedBlockKeys = await db.Set<FileVersionBlock>()
            .IgnoreQueryFilters()
            .Where(b => !b.IsDeleted)
            .Where(b => b.FileVersion.SnapshotLinks.Any(l =>
                !l.IsDeleted
                && validArchivedSnapshotIds.Contains(l.SnapshotId)))
            .Select(b => b.BlockStorageKey)
            .Distinct()
            .ToListAsync(ct);

        var activeSet = new HashSet<string>(activeBlockKeys, StringComparer.OrdinalIgnoreCase);
        var deletedFiles = 0;
        long deletedBytes = 0;

        foreach (var blockStorageKey in archivedBlockKeys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            if (activeSet.Contains(blockStorageKey))
                continue;

            var localPath = RepositoryBundleBlockPathResolver.ResolveLocalBlockPath(storeRoot, blockStorageKey);
            if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
                continue;

            try
            {
                var info = new FileInfo(localPath);
                var size = info.Exists ? info.Length : 0;
                File.Delete(localPath);
                deletedFiles++;
                deletedBytes += size;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to prune archived local block file {Path}", localPath);
            }
        }

        return (deletedFiles, deletedBytes);
    }

    private static async Task<RepositorySnapshotArchiveManifest?> ReadManifestAsync(ZipArchive zip, CancellationToken ct)
    {
        var manifestEntry = zip.GetEntry(ManifestEntryName);
        if (manifestEntry is null)
            return null;

        await using var stream = manifestEntry.Open();
        return await JsonSerializer.DeserializeAsync<RepositorySnapshotArchiveManifest>(stream, JsonOptions, ct);
    }
}
