using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.DTOs;
using Veyra.Domain.Entities;
using Veyra.Infrastructure.Data.Persistence;

namespace Veyra.Infrastructure.Data.Setup;

public sealed class EfRepositorySnapshotRepository(
    VeyraDbContext db,
    IFileContentStore contentStore,
    ILogger<EfRepositorySnapshotRepository> log) : IRepositorySnapshotRepository
{
    public async Task SaveSnapshotAsync(
        int repositoryId,
        string trigger,
        DateTime scannedAtUtc,
        IReadOnlyCollection<RepositoryScanEntryDto> entries,
        CancellationToken ct = default)
    {
        var repo = await db.Set<Repository>()
            .Include(r => r.Directory)
            .FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted, ct);

        if (repo is null)
            return;

        var safeTrigger = string.IsNullOrWhiteSpace(trigger)
            ? "manual"
            : trigger.Trim();

        var totalEntries = entries.Count;
        var fileEntries = entries.Count(e => !e.IsDirectory);
        var dirEntries = totalEntries - fileEntries;
        var totalFileBytes = entries
            .Where(e => !e.IsDirectory)
            .Sum(e => e.SizeBytes);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var previousSnapshotId = await db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => s.Id)
            .FirstOrDefaultAsync(ct);

        var previousFilesByPath = previousSnapshotId == 0
            ? new Dictionary<string, (string? Hash, long SizeBytes)>(StringComparer.OrdinalIgnoreCase)
            : await db.Set<RepositorySnapshotEntry>()
                .Where(e => e.RepositoryId == repositoryId && e.SnapshotId == previousSnapshotId && !e.IsDirectory)
                .ToDictionaryAsync(
                    e => e.RelativePath,
                    e => (Hash: e.ContentHashSha256, SizeBytes: e.SizeBytes),
                    StringComparer.OrdinalIgnoreCase,
                    ct);

        var snapshot = new RepositorySnapshot
        {
            RepositoryId = repositoryId,
            Trigger = safeTrigger,
            CreatedAt = scannedAtUtc,
            TotalEntries = totalEntries,
            FileEntries = fileEntries,
            DirectoryEntries = dirEntries,
            TotalFileBytes = totalFileBytes
        };

        db.Add(snapshot);
        await db.SaveChangesAsync(ct);

        if (totalEntries > 0)
        {
            var rows = entries.Select(e => new RepositorySnapshotEntry
            {
                SnapshotId = snapshot.Id,
                RepositoryId = repositoryId,
                RelativePath = e.RelativePath,
                ParentRelativePath = e.ParentRelativePath,
                Name = e.Name,
                IsDirectory = e.IsDirectory,
                Extension = e.Extension,
                SizeBytes = e.SizeBytes,
                LastWriteUtc = e.LastWriteUtc,
                ContentHashSha256 = e.ContentHashSha256,
                CreatedAt = scannedAtUtc
            }).ToList();

            db.AddRange(rows);
        }

        var currentFilesByPath = entries
            .Where(e => !e.IsDirectory)
            .ToDictionary(e => e.RelativePath, e => e, StringComparer.OrdinalIgnoreCase);

        var allPaths = currentFilesByPath.Keys
            .Concat(previousFilesByPath.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var identitiesByPath = allPaths.Count == 0
            ? new Dictionary<string, FileIdentity>(StringComparer.OrdinalIgnoreCase)
            : await db.Set<FileIdentity>()
                .Where(i => i.RepositoryId == repositoryId && allPaths.Contains(i.RelativePath))
                .ToDictionaryAsync(i => i.RelativePath, StringComparer.OrdinalIgnoreCase, ct);

        foreach (var path in allPaths)
        {
            if (identitiesByPath.ContainsKey(path))
                continue;

            var current = currentFilesByPath.GetValueOrDefault(path);

            var identity = new FileIdentity
            {
                RepositoryId = repositoryId,
                RelativePath = path,
                Name = current?.Name ?? Path.GetFileName(path),
                Extension = current?.Extension,
                IsDeleted = false,
                CreatedAt = scannedAtUtc,
                UpdatedAt = scannedAtUtc
            };

            db.Add(identity);
            identitiesByPath[path] = identity;
        }

        await db.SaveChangesAsync(ct);

        var identityIds = identitiesByPath.Values.Select(i => i.Id).Distinct().ToList();

        var latestVersionsByIdentityId = new Dictionary<long, FileVersion>();
        if (identityIds.Count > 0)
        {
            var existingVersions = await db.Set<FileVersion>()
                .Where(v => identityIds.Contains(v.FileIdentityId))
                .OrderByDescending(v => v.CreatedAt)
                .ThenByDescending(v => v.Id)
                .ToListAsync(ct);

            latestVersionsByIdentityId = existingVersions
                .GroupBy(v => v.FileIdentityId)
                .ToDictionary(g => g.Key, g => g.First());
        }

        var newVersions = new List<FileVersion>();
        var links = new List<SnapshotFileLink>();
        var pendingBlocks = new Dictionary<FileVersion, IReadOnlyList<StoredFileBlockDto>>();

        foreach (var path in allPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var identity = identitiesByPath[path];
            currentFilesByPath.TryGetValue(path, out var current);
            previousFilesByPath.TryGetValue(path, out var previous);

            FileVersion selectedVersion;

            if (current is not null)
            {
                identity.Name = current.Name;
                identity.Extension = current.Extension;
                identity.IsDeleted = false;
                identity.UpdatedAt = scannedAtUtc;

                var changed = previous.Hash is null
                    || !string.Equals(previous.Hash, current.ContentHashSha256, StringComparison.OrdinalIgnoreCase)
                    || previous.SizeBytes != current.SizeBytes;

                if (changed
                    || !latestVersionsByIdentityId.TryGetValue(identity.Id, out selectedVersion!)
                    || selectedVersion.IsDeletionMarker)
                {
                    selectedVersion = new FileVersion
                    {
                        FileIdentityId = identity.Id,
                        ContentHashSha256 = current.ContentHashSha256 ?? "unknown",
                        SizeBytes = current.SizeBytes,
                        LastWriteUtc = current.LastWriteUtc,
                        IsDeletionMarker = false,
                        CreatedAt = scannedAtUtc
                    };

                    var absolutePath = ToAbsolutePath(repo.Directory.Path, path);
                    var stored = await TryStoreBlocksAsync(absolutePath, ct);
                    if (stored is not null && stored.Blocks.Count > 0)
                        pendingBlocks[selectedVersion] = stored.Blocks;

                    newVersions.Add(selectedVersion);
                    latestVersionsByIdentityId[identity.Id] = selectedVersion;
                }
            }
            else
            {
                identity.IsDeleted = true;
                identity.UpdatedAt = scannedAtUtc;

                if (!latestVersionsByIdentityId.TryGetValue(identity.Id, out selectedVersion!)
                    || !selectedVersion.IsDeletionMarker)
                {
                    selectedVersion = new FileVersion
                    {
                        FileIdentityId = identity.Id,
                        ContentHashSha256 = previous.Hash ?? "deleted",
                        SizeBytes = previous.SizeBytes,
                        LastWriteUtc = scannedAtUtc,
                        IsDeletionMarker = true,
                        CreatedAt = scannedAtUtc
                    };

                    newVersions.Add(selectedVersion);
                    latestVersionsByIdentityId[identity.Id] = selectedVersion;
                }
            }

            var link = new SnapshotFileLink
            {
                SnapshotId = snapshot.Id,
                FileIdentityId = identity.Id,
                CreatedAt = scannedAtUtc
            };

            if (selectedVersion.Id > 0)
                link.FileVersionId = selectedVersion.Id;
            else
                link.FileVersion = selectedVersion;

            links.Add(link);
        }

        foreach (var pair in pendingBlocks)
        {
            var version = pair.Key;
            foreach (var block in pair.Value.OrderBy(b => b.Sequence))
            {
                version.Blocks.Add(new FileVersionBlock
                {
                    Sequence = block.Sequence,
                    BlockHashBlake3 = block.BlockHashBlake3,
                    LengthBytes = block.LengthBytes,
                    StoredSizeBytes = block.StoredSizeBytes,
                    CreatedAt = scannedAtUtc
                });
            }
        }

        if (newVersions.Count > 0)
            db.AddRange(newVersions);

        if (links.Count > 0)
            db.AddRange(links);

        repo.FileCount = fileEntries;
        repo.VersionCount += 1;
        repo.TotalSizeBytes = totalFileBytes;
        repo.LastScannedAt = scannedAtUtc;
        repo.UpdatedAt = scannedAtUtc;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var totalBlockRefs = pendingBlocks.Values.Sum(v => v.Count);

        log.LogInformation(
            "Snapshot saved for repository {RepositoryId}. Entries {Entries}. Files {Files}. FileIdentities {FileIdentities}. NewVersions {NewVersions}. Links {Links}. BlockRefs {BlockRefs}. Trigger {Trigger}",
            repositoryId,
            totalEntries,
            fileEntries,
            identitiesByPath.Count,
            newVersions.Count,
            links.Count,
            totalBlockRefs,
            safeTrigger);
    }

    public async Task<IReadOnlyList<RepositoryScanEntryDto>> GetLatestEntriesAsync(
        int repositoryId,
        CancellationToken ct = default)
    {
        var snapshotId = await db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => s.Id)
            .FirstOrDefaultAsync(ct);

        if (snapshotId == 0)
            return Array.Empty<RepositoryScanEntryDto>();

        return await db.Set<RepositorySnapshotEntry>()
            .Where(e => e.RepositoryId == repositoryId && e.SnapshotId == snapshotId)
            .OrderBy(e => e.RelativePath)
            .Select(e => new RepositoryScanEntryDto(
                e.RelativePath,
                e.ParentRelativePath,
                e.Name,
                e.IsDirectory,
                e.Extension,
                e.SizeBytes,
                e.LastWriteUtc,
                e.ContentHashSha256))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<FileVersionInfoDto>> GetFileVersionsAsync(
        int repositoryId,
        string relativePath,
        int take = 50,
        CancellationToken ct = default)
    {
        if (repositoryId <= 0 || string.IsNullOrWhiteSpace(relativePath))
            return Array.Empty<FileVersionInfoDto>();

        var normalizedPath = NormalizeRelativePath(relativePath);

        var identity = await db.Set<FileIdentity>()
            .FirstOrDefaultAsync(i => i.RepositoryId == repositoryId && i.RelativePath == normalizedPath, ct);

        if (identity is null)
            return Array.Empty<FileVersionInfoDto>();

        var limit = Math.Clamp(take, 1, 500);

        return await db.Set<FileVersion>()
            .Where(v => v.FileIdentityId == identity.Id)
            .OrderByDescending(v => v.CreatedAt)
            .ThenByDescending(v => v.Id)
            .Take(limit)
            .Select(v => new FileVersionInfoDto(
                v.Id,
                identity.RelativePath,
                identity.Extension,
                v.CreatedAt,
                v.SizeBytes,
                v.IsDeletionMarker,
                v.ContentHashSha256))
            .ToListAsync(ct);
    }

    public async Task<FileVersionRestoreDto?> GetFileVersionRestoreDataAsync(
        long fileVersionId,
        CancellationToken ct = default)
    {
        var version = await db.Set<FileVersion>()
            .Include(v => v.FileIdentity)
            .FirstOrDefaultAsync(v => v.Id == fileVersionId, ct);

        if (version is null)
            return null;

        var blocks = await db.Set<FileVersionBlock>()
            .Where(b => b.FileVersionId == fileVersionId)
            .OrderBy(b => b.Sequence)
            .Select(b => new StoredFileBlockDto(
                b.Sequence,
                b.BlockHashBlake3,
                b.LengthBytes,
                b.StoredSizeBytes))
            .ToListAsync(ct);

        return new FileVersionRestoreDto(
            version.Id,
            version.FileIdentity.RepositoryId,
            version.FileIdentity.RelativePath,
            version.FileIdentity.Extension,
            version.SizeBytes,
            version.IsDeletionMarker,
            version.ContentHashSha256,
            blocks);
    }

    private static string NormalizeRelativePath(string value)
        => value.Trim().Replace('\\', '/');

    private static string ToAbsolutePath(string rootPath, string relativePath)
    {
        var rel = relativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(rootPath, rel);
    }

    private async Task<StoredFileContentDto?> TryStoreBlocksAsync(string absolutePath, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(absolutePath))
                return null;

            return await contentStore.StoreFileAsync(absolutePath, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to store file blocks for {Path}", absolutePath);
            return null;
        }
    }
}
