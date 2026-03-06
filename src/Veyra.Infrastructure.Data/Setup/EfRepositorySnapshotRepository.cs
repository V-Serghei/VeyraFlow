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
        bool saveFileVersions = true,
        string? snapshotTitle = null,
        CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();

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

        var previousSnapshotsQuery = db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId);

        if (saveFileVersions)
        {
            previousSnapshotsQuery = previousSnapshotsQuery
                .Where(s => db.Set<SnapshotFileLink>().Any(l => l.SnapshotId == s.Id));
        }

        var previousSnapshotId = await previousSnapshotsQuery
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
            Title = NormalizeSnapshotTitle(snapshotTitle),
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

        if (!saveFileVersions)
        {
            repo.FileCount = fileEntries;
            repo.TotalSizeBytes = totalFileBytes;
            repo.LastScannedAt = scannedAtUtc;
            repo.UpdatedAt = scannedAtUtc;

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            log.LogInformation(
                "Repository sync saved without file versions. RepositoryId {RepositoryId}. Entries {Entries}. Files {Files}. Trigger {Trigger}",
                repositoryId,
                totalEntries,
                fileEntries,
                safeTrigger);

            return;
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

        var latestVersionIds = latestVersionsByIdentityId.Values
            .Select(v => v.Id)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        var latestVersionIdsWithBlocks = latestVersionIds.Count == 0
            ? new HashSet<long>()
            : (await db.Set<FileVersionBlock>()
                    .Where(b => latestVersionIds.Contains(b.FileVersionId))
                    .Select(b => b.FileVersionId)
                    .Distinct()
                    .ToListAsync(ct))
                .ToHashSet();

        var newVersions = new List<FileVersion>();
        var links = new List<SnapshotFileLink>();
        var pendingBlocks = new Dictionary<FileVersion, IReadOnlyList<StoredFileBlockDto>>();

        foreach (var path in allPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var identity = identitiesByPath[path];
            currentFilesByPath.TryGetValue(path, out var current);
            var hadPrevious = previousFilesByPath.TryGetValue(path, out var previous);

            FileVersion selectedVersion;

            if (current is not null)
            {
                identity.Name = current.Name;
                identity.Extension = current.Extension;
                identity.IsDeleted = false;
                identity.UpdatedAt = scannedAtUtc;

                var previousHash = previous.Hash ?? string.Empty;
                var currentHash = current.ContentHashSha256 ?? string.Empty;

                var changed = !hadPrevious
                    || !string.Equals(previousHash, currentHash, StringComparison.OrdinalIgnoreCase)
                    || previous.SizeBytes != current.SizeBytes;

                var hasLatest = latestVersionsByIdentityId.TryGetValue(identity.Id, out selectedVersion!);
                var latestHasBlocks = hasLatest
                                      && selectedVersion.Id > 0
                                      && (selectedVersion.SizeBytes == 0 || latestVersionIdsWithBlocks.Contains(selectedVersion.Id));

                var shouldCreateNewVersion = changed
                                             || !hasLatest
                                             || selectedVersion.IsDeletionMarker
                                             || !latestHasBlocks;

                if (shouldCreateNewVersion)
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
                    var stored = await TryStoreBlocksAsync(absolutePath, throwOnFailure: true, ct);
                    if (stored is null || (current.SizeBytes > 0 && stored.Blocks.Count == 0))
                        throw new InvalidOperationException($"Failed to store blocks for file {path}.");

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
                v.ContentHashSha256,
                v.SizeBytes == 0 || v.Blocks.Any()))
            .ToListAsync(ct);
    }

    public async Task<RepositoryPendingChangesDto> GetPendingChangesAsync(
        int repositoryId,
        int take = 200,
        CancellationToken ct = default)
    {
        if (repositoryId <= 0)
            return RepositoryPendingChangesDto.Empty;

        var latestSnapshot = await db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => new { s.Id, s.CreatedAt })
            .FirstOrDefaultAsync(ct);

        if (latestSnapshot is null)
            return RepositoryPendingChangesDto.Empty;

        var currentFiles = await db.Set<RepositorySnapshotEntry>()
            .Where(e => e.RepositoryId == repositoryId && e.SnapshotId == latestSnapshot.Id && !e.IsDirectory)
            .Select(e => new SnapshotEntryLight(
                e.RelativePath,
                e.Name,
                e.SizeBytes,
                e.LastWriteUtc,
                e.ContentHashSha256))
            .ToListAsync(ct);

        var baselineSnapshot = await db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId)
            .Where(s => db.Set<SnapshotFileLink>().Any(l => l.SnapshotId == s.Id))
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => new { s.Id, s.CreatedAt })
            .FirstOrDefaultAsync(ct);

        var baselineFiles = baselineSnapshot is null
            ? []
            : await db.Set<RepositorySnapshotEntry>()
                .Where(e => e.RepositoryId == repositoryId && e.SnapshotId == baselineSnapshot.Id && !e.IsDirectory)
                .Select(e => new SnapshotEntryLight(
                    e.RelativePath,
                    e.Name,
                    e.SizeBytes,
                    e.LastWriteUtc,
                    e.ContentHashSha256))
                .ToListAsync(ct);

        var currentByPath = currentFiles.ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
        var baselineByPath = baselineFiles.ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);

        var changes = new List<RepositoryPendingChangeEntryDto>();
        var added = 0;
        var modified = 0;
        var deleted = 0;

        foreach (var current in currentByPath.Values.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            if (!baselineByPath.TryGetValue(current.RelativePath, out var previous))
            {
                added++;
                changes.Add(new RepositoryPendingChangeEntryDto(
                    current.RelativePath,
                    current.Name,
                    "added",
                    current.SizeBytes,
                    0,
                    current.LastWriteUtc,
                    null));
                continue;
            }

            var isModified = !string.Equals(previous.ContentHashSha256, current.ContentHashSha256, StringComparison.OrdinalIgnoreCase)
                             || previous.SizeBytes != current.SizeBytes;

            if (!isModified)
                continue;

            modified++;
            changes.Add(new RepositoryPendingChangeEntryDto(
                current.RelativePath,
                current.Name,
                "modified",
                current.SizeBytes,
                previous.SizeBytes,
                current.LastWriteUtc,
                previous.LastWriteUtc));
        }

        foreach (var previous in baselineByPath.Values.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            if (currentByPath.ContainsKey(previous.RelativePath))
                continue;

            deleted++;
            changes.Add(new RepositoryPendingChangeEntryDto(
                previous.RelativePath,
                previous.Name,
                "deleted",
                0,
                previous.SizeBytes,
                previous.LastWriteUtc,
                previous.LastWriteUtc));
        }

        var limit = Math.Clamp(take, 1, 2000);

        var ordered = changes
            .OrderBy(x => x.ChangeKind switch
            {
                "added" => 0,
                "modified" => 1,
                "deleted" => 2,
                _ => 9
            })
            .ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        return new RepositoryPendingChangesDto(
            baselineSnapshot?.CreatedAt,
            added,
            modified,
            deleted,
            ordered);
    }

    public async Task<IReadOnlyList<RepositorySnapshotHistoryItemDto>> GetSnapshotHistoryAsync(
        int repositoryId,
        int take = 100,
        CancellationToken ct = default)
    {
        if (repositoryId <= 0)
            return Array.Empty<RepositorySnapshotHistoryItemDto>();

        var limit = Math.Clamp(take, 1, 500);

        var snapshots = await db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId)
            .Where(s => db.Set<SnapshotFileLink>().Any(l => l.SnapshotId == s.Id))
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Take(limit + 1)
            .Select(s => new SnapshotLight(
                s.Id,
                s.Title,
                s.CreatedAt,
                s.Trigger))
            .ToListAsync(ct);

        if (snapshots.Count == 0)
            return Array.Empty<RepositorySnapshotHistoryItemDto>();

        var snapshotIds = snapshots.Select(s => s.SnapshotId).ToList();

        var linkRows = await db.Set<SnapshotFileLink>()
            .Where(l => snapshotIds.Contains(l.SnapshotId))
            .Select(l => new SnapshotLinkState(
                l.SnapshotId,
                l.FileIdentityId,
                l.FileVersionId,
                l.FileVersion != null && l.FileVersion.IsDeletionMarker,
                l.FileVersion != null ? l.FileVersion.SizeBytes : 0,
                l.FileVersion != null ? l.FileVersion.CreatedAt : DateTime.MinValue,
                l.FileIdentity.RelativePath,
                l.FileIdentity.Name))
            .ToListAsync(ct);

        var statesBySnapshot = linkRows
            .GroupBy(l => l.SnapshotId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<long, SnapshotLinkState>)g.ToDictionary(x => x.FileIdentityId));

        var result = new List<RepositorySnapshotHistoryItemDto>(Math.Min(limit, snapshots.Count));

        for (var i = 0; i < snapshots.Count && result.Count < limit; i++)
        {
            var current = snapshots[i];
            var previous = i + 1 < snapshots.Count ? snapshots[i + 1] : null;

            var currentStates = statesBySnapshot.GetValueOrDefault(current.SnapshotId, EmptyLinkStateMap);
            var previousStates = previous is null
                ? EmptyLinkStateMap
                : statesBySnapshot.GetValueOrDefault(previous.SnapshotId, EmptyLinkStateMap);

            var changedFilesCount = CountChangedFiles(currentStates, previousStates);

            result.Add(new RepositorySnapshotHistoryItemDto(
                current.SnapshotId,
                current.Title,
                current.CreatedAtUtc,
                current.Trigger,
                changedFilesCount));
        }

        return result;
    }

    public async Task<IReadOnlyList<RepositorySnapshotFileChangeDto>> GetSnapshotChangedFilesAsync(
        int repositoryId,
        long snapshotId,
        int take = 1000,
        CancellationToken ct = default)
    {
        if (repositoryId <= 0 || snapshotId <= 0)
            return Array.Empty<RepositorySnapshotFileChangeDto>();

        var limit = Math.Clamp(take, 1, 5000);

        var versionedSnapshotIds = await db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId)
            .Where(s => db.Set<SnapshotFileLink>().Any(l => l.SnapshotId == s.Id))
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var index = versionedSnapshotIds.FindIndex(id => id == snapshotId);
        if (index < 0)
            return Array.Empty<RepositorySnapshotFileChangeDto>();

        var previousSnapshotId = index + 1 < versionedSnapshotIds.Count
            ? versionedSnapshotIds[index + 1]
            : 0;

        var currentRows = await db.Set<SnapshotFileLink>()
            .Where(l => l.SnapshotId == snapshotId)
            .Select(l => new SnapshotLinkState(
                l.SnapshotId,
                l.FileIdentityId,
                l.FileVersionId,
                l.FileVersion != null && l.FileVersion.IsDeletionMarker,
                l.FileVersion != null ? l.FileVersion.SizeBytes : 0,
                l.FileVersion != null ? l.FileVersion.CreatedAt : DateTime.MinValue,
                l.FileIdentity.RelativePath,
                l.FileIdentity.Name))
            .ToListAsync(ct);

        var previousRows = previousSnapshotId == 0
            ? []
            : await db.Set<SnapshotFileLink>()
                .Where(l => l.SnapshotId == previousSnapshotId)
                .Select(l => new SnapshotLinkState(
                    l.SnapshotId,
                    l.FileIdentityId,
                    l.FileVersionId,
                    l.FileVersion != null && l.FileVersion.IsDeletionMarker,
                    l.FileVersion != null ? l.FileVersion.SizeBytes : 0,
                    l.FileVersion != null ? l.FileVersion.CreatedAt : DateTime.MinValue,
                    l.FileIdentity.RelativePath,
                    l.FileIdentity.Name))
                .ToListAsync(ct);

        var previousByIdentity = previousRows.ToDictionary(x => x.FileIdentityId);

        var changes = new List<RepositorySnapshotFileChangeDto>();

        foreach (var current in currentRows.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            previousByIdentity.TryGetValue(current.FileIdentityId, out var previous);

            if (previous is not null && current.FileVersionId == previous.FileVersionId)
                continue;

            var kind = ResolveChangeKind(current, previous);
            var previousSize = previous?.SizeBytes ?? 0;

            changes.Add(new RepositorySnapshotFileChangeDto(
                snapshotId,
                current.FileIdentityId,
                current.FileVersionId,
                current.RelativePath,
                current.Name,
                kind,
                current.SizeBytes,
                previousSize,
                current.VersionCreatedAtUtc));
        }

        return changes
            .OrderBy(c => c.ChangeKind switch
            {
                "added" => 0,
                "modified" => 1,
                "deleted" => 2,
                _ => 9
            })
            .ThenBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
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

    private static readonly IReadOnlyDictionary<long, SnapshotLinkState> EmptyLinkStateMap =
        new Dictionary<long, SnapshotLinkState>();

    private static string? NormalizeSnapshotTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var title = value.Trim();
        if (title.Length <= 256)
            return title;

        return title[..256];
    }

    private static int CountChangedFiles(
        IReadOnlyDictionary<long, SnapshotLinkState> current,
        IReadOnlyDictionary<long, SnapshotLinkState> previous)
    {
        var changed = 0;

        foreach (var state in current.Values)
        {
            if (!previous.TryGetValue(state.FileIdentityId, out var previousState)
                || previousState.FileVersionId != state.FileVersionId)
            {
                changed++;
            }
        }

        return changed;
    }

    private static string ResolveChangeKind(SnapshotLinkState current, SnapshotLinkState? previous)
    {
        if (current.IsDeletionMarker)
            return "deleted";

        if (previous is null || previous.IsDeletionMarker)
            return "added";

        return "modified";
    }
    private static string NormalizeRelativePath(string value)
        => value.Trim().Replace('\\', '/');

    private static string ToAbsolutePath(string rootPath, string relativePath)
    {
        var rel = relativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(rootPath, rel);
    }

    private async Task<StoredFileContentDto?> TryStoreBlocksAsync(
        string absolutePath,
        bool throwOnFailure,
        CancellationToken ct)
    {
        try
        {
            if (!File.Exists(absolutePath))
            {
                if (throwOnFailure)
                    throw new FileNotFoundException("File not found for block storage.", absolutePath);

                return null;
            }

            return await contentStore.StoreFileAsync(absolutePath, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to store file blocks for {Path}", absolutePath);

            if (throwOnFailure)
                throw new InvalidOperationException($"Failed to store file blocks {absolutePath}", ex);

            return null;
        }
    }

    private sealed record SnapshotLight(
        long SnapshotId,
        string? Title,
        DateTime CreatedAtUtc,
        string Trigger);

    private sealed record SnapshotLinkState(
        long SnapshotId,
        long FileIdentityId,
        long FileVersionId,
        bool IsDeletionMarker,
        long SizeBytes,
        DateTime VersionCreatedAtUtc,
        string RelativePath,
        string Name);
    private sealed record SnapshotEntryLight(
        string RelativePath,
        string Name,
        long SizeBytes,
        DateTime LastWriteUtc,
        string? ContentHashSha256);
}


