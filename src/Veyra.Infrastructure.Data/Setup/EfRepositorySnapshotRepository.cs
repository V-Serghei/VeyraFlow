using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.DTOs;
using Veyra.Domain.Entities;
using Veyra.Infrastructure.Data.Persistence;

namespace Veyra.Infrastructure.Data.Setup;

public sealed class EfRepositorySnapshotRepository(
    VeyraDbContext db,
    IFileContentStore contentStore,
    ITextDiffEngine diffEngine,
    ISnapshotComparisonEngine snapshotComparison,
    ILogger<EfRepositorySnapshotRepository> log) : IRepositorySnapshotRepository
{
    private const int PrecomputedDiffMaxLines = 4000;

    private static readonly JsonSerializerOptions DiffJsonOptions = new();

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".json", ".xml", ".yml", ".yaml", ".ini", ".toml", ".log",
        ".cs", ".js", ".ts", ".java", ".py", ".rs", ".go", ".c", ".cpp", ".h", ".hpp",
        ".html", ".css", ".sql", ".xaml", ".axaml"
    };

    public async Task<SnapshotSaveResultDto> SaveSnapshotAsync(
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
            return SnapshotSaveResultDto.Skipped();

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
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted);

        if (saveFileVersions)
        {
            previousSnapshotsQuery = previousSnapshotsQuery
                .Where(s => db.Set<SnapshotFileLink>().Any(l => l.SnapshotId == s.Id && !l.IsDeleted));
        }

        var previousSnapshotId = await previousSnapshotsQuery
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => s.Id)
            .FirstOrDefaultAsync(ct);

        var previousFilesByPath = previousSnapshotId == 0
            ? new Dictionary<string, (string? Hash, long SizeBytes, string Name, DateTime LastWriteUtc)>(StringComparer.OrdinalIgnoreCase)
            : await db.Set<RepositorySnapshotEntry>()
                .Where(e => e.RepositoryId == repositoryId && e.SnapshotId == previousSnapshotId && !e.IsDirectory && !e.IsDeleted)
                .ToDictionaryAsync(
                    e => e.RelativePath,
                    e => (Hash: e.ContentHashSha256, SizeBytes: e.SizeBytes, Name: e.Name, LastWriteUtc: e.LastWriteUtc),
                    StringComparer.OrdinalIgnoreCase,
                    ct);
        var currentFilesByPath = entries
            .Where(e => !e.IsDirectory)
            .ToDictionary(e => e.RelativePath, e => e, StringComparer.OrdinalIgnoreCase);
        if (saveFileVersions && previousSnapshotId > 0)
        {
            var currentStates = currentFilesByPath.Values
                .Select(e => new RepositoryPathStateDto(
                    e.RelativePath,
                    e.Name,
                    e.SizeBytes,
                    e.LastWriteUtc,
                    e.ContentHashSha256))
                .ToList();
            var baselineStates = previousFilesByPath
                .Select(pair => new RepositoryPathStateDto(
                    pair.Key,
                    pair.Value.Name,
                    pair.Value.SizeBytes,
                    pair.Value.LastWriteUtc,
                    pair.Value.Hash))
                .ToList();
            var comparison = await snapshotComparison.CompareRepositoryPathsAsync(currentStates, baselineStates, 1, ct);
            if (comparison.ChangedFilesCount == 0)
            {
                repo.FileCount = fileEntries;
                repo.TotalSizeBytes = totalFileBytes;
                repo.LastScannedAt = scannedAtUtc;
                repo.UpdatedAt = scannedAtUtc;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                log.LogInformation(
                    "Snapshot skipped (no changes). RepositoryId {RepositoryId}. Files {Files}. Trigger {Trigger}",
                    repositoryId,
                    fileEntries,
                    safeTrigger);
                return SnapshotSaveResultDto.NoChanges();
            }
        }
        var snapshot = new RepositorySnapshot
        {
            RepositoryId = repositoryId,
            Trigger = safeTrigger,
            Title = NormalizeSnapshotTitle(snapshotTitle),
            CreatedAt = scannedAtUtc,
            TotalEntries = totalEntries,
            FileEntries = fileEntries,
            DirectoryEntries = dirEntries,
            TotalFileBytes = totalFileBytes,
            IsDeleted = false,
            DeletedAt = null
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
                CreatedAt = scannedAtUtc,
                IsDeleted = false,
                DeletedAt = null
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

            return SnapshotSaveResultDto.Created();
        }
        var allPaths = currentFilesByPath.Keys
            .Concat(previousFilesByPath.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var identitiesByPath = allPaths.Count == 0
            ? new Dictionary<string, FileIdentity>(StringComparer.OrdinalIgnoreCase)
            : await db.Set<FileIdentity>()
                .IgnoreQueryFilters()
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
                CreatedAt = scannedAtUtc,
                IsDeleted = false,
                DeletedAt = null,
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
                .Where(v => identityIds.Contains(v.FileIdentityId) && !v.IsDeleted)
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
                    .Where(b => latestVersionIds.Contains(b.FileVersionId) && !b.IsDeleted)
                    .Select(b => b.FileVersionId)
                    .Distinct()
                    .ToListAsync(ct))
                .ToHashSet();

        var planningStates = new List<RepositoryVersionPlanningFileStateDto>(allPaths.Count);
        foreach (var path in allPaths)
        {
            currentFilesByPath.TryGetValue(path, out var current);
            var hasPrevious = previousFilesByPath.TryGetValue(path, out var previous);
            var identity = identitiesByPath[path];
            var hasLatestVersion = latestVersionsByIdentityId.TryGetValue(identity.Id, out var latestVersion);
            var latestHasBlocks = hasLatestVersion
                                  && latestVersion is not null
                                  && latestVersion.Id > 0
                                  && (latestVersion.SizeBytes == 0 || latestVersionIdsWithBlocks.Contains(latestVersion.Id));

            planningStates.Add(new RepositoryVersionPlanningFileStateDto(
                RelativePath: path,
                HasCurrent: current is not null,
                CurrentSizeBytes: current?.SizeBytes ?? 0,
                CurrentContentHashSha256: current?.ContentHashSha256,
                HasPrevious: hasPrevious,
                PreviousSizeBytes: previous.SizeBytes,
                PreviousContentHashSha256: previous.Hash,
                HasLatestVersion: hasLatestVersion,
                LatestIsDeletionMarker: latestVersion?.IsDeletionMarker ?? false,
                LatestSizeBytes: latestVersion?.SizeBytes ?? 0,
                LatestHasBlocks: latestHasBlocks));
        }

        var versionPlan = await snapshotComparison.PlanRepositoryVersionsAsync(planningStates, ct);
        var planByPath = versionPlan.Entries
            .GroupBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var newVersions = new List<FileVersion>();
        var links = new List<SnapshotFileLink>();
        var pendingBlocks = new Dictionary<FileVersion, IReadOnlyList<StoredFileBlockDto>>();
        var pendingPrecomputedDiffs = new List<PendingTextDiffPrecompute>();

        foreach (var path in allPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var identity = identitiesByPath[path];
            currentFilesByPath.TryGetValue(path, out var current);
            var hadPrevious = previousFilesByPath.TryGetValue(path, out var previous);
            planByPath.TryGetValue(path, out var planEntry);

            FileVersion selectedVersion;

            if (current is not null)
            {
                identity.Name = current.Name;
                identity.Extension = current.Extension;
                identity.IsDeleted = false;
                identity.DeletedAt = null;
                identity.UpdatedAt = scannedAtUtc;

                var previousHash = previous.Hash ?? string.Empty;
                var currentHash = current.ContentHashSha256 ?? string.Empty;

                var changed = !hadPrevious
                    || !string.Equals(previousHash, currentHash, StringComparison.OrdinalIgnoreCase)
                    || previous.SizeBytes != current.SizeBytes;

                var hasLatest = latestVersionsByIdentityId.TryGetValue(identity.Id, out selectedVersion!);
                var previousVersionForDiff = hasLatest ? selectedVersion : null;

                var latestHasBlocks = hasLatest
                                      && selectedVersion.Id > 0
                                      && (selectedVersion.SizeBytes == 0 || latestVersionIdsWithBlocks.Contains(selectedVersion.Id));

                var shouldCreateNewVersion = planEntry?.ShouldCreateNewVersion
                                             ?? (changed
                                                 || !hasLatest
                                                 || (hasLatest && selectedVersion.IsDeletionMarker)
                                                 || !latestHasBlocks);

                if (!hasLatest && !shouldCreateNewVersion)
                    shouldCreateNewVersion = true;

                if (shouldCreateNewVersion)
                {
                    var newVersion = new FileVersion
                    {
                        FileIdentityId = identity.Id,
                        ContentHashSha256 = current.ContentHashSha256 ?? "unknown",
                        SizeBytes = current.SizeBytes,
                        LastWriteUtc = current.LastWriteUtc,
                        IsDeletionMarker = false,
                        CreatedAt = scannedAtUtc,
                        IsDeleted = false,
                        DeletedAt = null
                    };

                    var absolutePath = ToAbsolutePath(repo.Directory.Path, path);
                    var stored = await TryStoreBlocksAsync(absolutePath, throwOnFailure: true, ct);
                    if (stored is null || (current.SizeBytes > 0 && stored.Blocks.Count == 0))
                        throw new InvalidOperationException($"Failed to store blocks for file {path}.");

                    pendingBlocks[newVersion] = stored.Blocks;
                    newVersions.Add(newVersion);
                    latestVersionsByIdentityId[identity.Id] = newVersion;
                    selectedVersion = newVersion;

                    if (previousVersionForDiff is { Id: > 0, IsDeletionMarker: false }
                        && latestHasBlocks
                        && IsTextExtension(current.Extension))
                    {
                        pendingPrecomputedDiffs.Add(new PendingTextDiffPrecompute(
                            path,
                            previousVersionForDiff.Id,
                            newVersion));
                    }
                }
            }
            else
            {
                identity.IsDeleted = planEntry?.ShouldMarkIdentityDeleted ?? true;
                identity.DeletedAt = identity.IsDeleted ? scannedAtUtc : null;
                identity.UpdatedAt = scannedAtUtc;

                var hasLatest = latestVersionsByIdentityId.TryGetValue(identity.Id, out selectedVersion!);
                var shouldCreateDeletionMarker = planEntry?.ShouldCreateNewVersion
                                                ?? (!hasLatest
                                                    || !selectedVersion.IsDeletionMarker);

                if (!hasLatest && !shouldCreateDeletionMarker)
                    shouldCreateDeletionMarker = true;

                if (shouldCreateDeletionMarker)
                {
                    selectedVersion = new FileVersion
                    {
                        FileIdentityId = identity.Id,
                        ContentHashSha256 = previous.Hash ?? "deleted",
                        SizeBytes = previous.SizeBytes,
                        LastWriteUtc = scannedAtUtc,
                        IsDeletionMarker = true,
                        CreatedAt = scannedAtUtc,
                        IsDeleted = false,
                        DeletedAt = null
                    };

                    newVersions.Add(selectedVersion);
                    latestVersionsByIdentityId[identity.Id] = selectedVersion;
                }
            }
            var link = new SnapshotFileLink
            {
                SnapshotId = snapshot.Id,
                FileIdentityId = identity.Id,
                CreatedAt = scannedAtUtc,
                IsDeleted = false,
                DeletedAt = null
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
                    CreatedAt = scannedAtUtc,
                    IsDeleted = false,
                    DeletedAt = null
                });
            }
        }

        if (newVersions.Count > 0)
            db.AddRange(newVersions);

        if (links.Count > 0)
            db.AddRange(links);

        repo.FileCount = fileEntries;
        repo.VersionCount += newVersions.Count;
        repo.TotalSizeBytes = totalFileBytes;
        repo.LastScannedAt = scannedAtUtc;
        repo.UpdatedAt = scannedAtUtc;

        await db.SaveChangesAsync(ct);

        if (pendingPrecomputedDiffs.Count > 0)
            await PrecomputeSnapshotDiffsAsync(pendingPrecomputedDiffs, ct);

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

        return SnapshotSaveResultDto.Created();
    }

    public async Task<IReadOnlyList<RepositoryScanEntryDto>> GetLatestEntriesAsync(
        int repositoryId,
        CancellationToken ct = default)
    {
        var snapshotId = await db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => s.Id)
            .FirstOrDefaultAsync(ct);

        if (snapshotId == 0)
            return Array.Empty<RepositoryScanEntryDto>();

        return await db.Set<RepositorySnapshotEntry>()
            .Where(e => e.RepositoryId == repositoryId && e.SnapshotId == snapshotId && !e.IsDeleted)
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
            .FirstOrDefaultAsync(i => i.RepositoryId == repositoryId && i.RelativePath == normalizedPath && !i.IsDeleted, ct);

        if (identity is null)
            return Array.Empty<FileVersionInfoDto>();

        var limit = Math.Clamp(take, 1, 500);

        return await db.Set<FileVersion>()
            .Where(v => v.FileIdentityId == identity.Id && !v.IsDeleted)
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
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => new { s.Id, s.CreatedAt })
            .FirstOrDefaultAsync(ct);

        if (latestSnapshot is null)
            return RepositoryPendingChangesDto.Empty;

        var currentFiles = await db.Set<RepositorySnapshotEntry>()
            .Where(e => e.RepositoryId == repositoryId && e.SnapshotId == latestSnapshot.Id && !e.IsDirectory && !e.IsDeleted)
            .Select(e => new SnapshotEntryLight(
                e.RelativePath,
                e.Name,
                e.SizeBytes,
                e.LastWriteUtc,
                e.ContentHashSha256))
            .ToListAsync(ct);

        var baselineSnapshot = await db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
            .Where(s => db.Set<SnapshotFileLink>().Any(l => l.SnapshotId == s.Id && !l.IsDeleted))
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => new { s.Id, s.CreatedAt })
            .FirstOrDefaultAsync(ct);

        var baselineFiles = baselineSnapshot is null
            ? []
            : await db.Set<RepositorySnapshotEntry>()
                .Where(e => e.RepositoryId == repositoryId && e.SnapshotId == baselineSnapshot.Id && !e.IsDirectory && !e.IsDeleted)
                .Select(e => new SnapshotEntryLight(
                    e.RelativePath,
                    e.Name,
                    e.SizeBytes,
                    e.LastWriteUtc,
                    e.ContentHashSha256))
                .ToListAsync(ct);

        var currentStates = currentFiles
            .Select(ToRepositoryPathStateDto)
            .ToList();
        var baselineStates = baselineFiles
            .Select(ToRepositoryPathStateDto)
            .ToList();
        var limit = Math.Clamp(take, 1, 2000);
        var comparison = await snapshotComparison.CompareRepositoryPathsAsync(currentStates, baselineStates, limit, ct);
        return new RepositoryPendingChangesDto(
            baselineSnapshot?.CreatedAt,
            comparison.AddedCount,
            comparison.ModifiedCount,
            comparison.DeletedCount,
            comparison.Entries);
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
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
            .Where(s => db.Set<SnapshotFileLink>().Any(l => l.SnapshotId == s.Id && !l.IsDeleted))
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
            .Where(l => snapshotIds.Contains(l.SnapshotId) && !l.IsDeleted && !l.FileIdentity.IsDeleted && !l.FileVersion.IsDeleted)
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
                g => (IReadOnlyList<SnapshotLinkStateDto>)g.Select(ToSnapshotLinkStateDto).ToList());

        var result = new List<RepositorySnapshotHistoryItemDto>(Math.Min(limit, snapshots.Count));

        for (var i = 0; i < snapshots.Count && result.Count < limit; i++)
        {
            var current = snapshots[i];
            var previous = i + 1 < snapshots.Count ? snapshots[i + 1] : null;

            var currentStates = statesBySnapshot.GetValueOrDefault(current.SnapshotId, EmptySnapshotLinkStates);
            var previousStates = previous is null
                ? EmptySnapshotLinkStates
                : statesBySnapshot.GetValueOrDefault(previous.SnapshotId, EmptySnapshotLinkStates);

            var comparison = await snapshotComparison.CompareSnapshotLinksAsync(currentStates, previousStates, ct);

            result.Add(new RepositorySnapshotHistoryItemDto(
                current.SnapshotId,
                current.Title,
                current.CreatedAtUtc,
                current.Trigger,
                comparison.ChangedFilesCount));
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
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
            .Where(s => db.Set<SnapshotFileLink>().Any(l => l.SnapshotId == s.Id && !l.IsDeleted))
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
            .Where(l => l.SnapshotId == snapshotId && !l.IsDeleted && !l.FileIdentity.IsDeleted && !l.FileVersion.IsDeleted)
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
                .Where(l => l.SnapshotId == previousSnapshotId && !l.IsDeleted && !l.FileIdentity.IsDeleted && !l.FileVersion.IsDeleted)
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

        var currentStates = currentRows.Select(ToSnapshotLinkStateDto).ToList();
        var previousStates = previousRows.Select(ToSnapshotLinkStateDto).ToList();

        var comparison = await snapshotComparison.CompareSnapshotLinksAsync(currentStates, previousStates, ct);

        return comparison.Changes
            .Take(limit)
            .Select(c => new RepositorySnapshotFileChangeDto(
                snapshotId,
                c.FileIdentityId,
                c.FileVersionId,
                c.RelativePath,
                c.Name,
                c.ChangeKind,
                c.CurrentSizeBytes,
                c.PreviousSizeBytes,
                c.VersionCreatedAtUtc))
            .ToList();
    }

    public async Task<PendingFileDiffPreviewDto> GetPendingFileDiffPreviewAsync(
        int repositoryId,
        string relativePath,
        int maxLines = 3000,
        CancellationToken ct = default)
    {
        if (repositoryId <= 0 || string.IsNullOrWhiteSpace(relativePath))
            return PendingFileDiffPreviewDto.Unavailable(relativePath ?? string.Empty, "Invalid preview request.");

        var normalizedPath = NormalizeRelativePath(relativePath);
        var normalizedMaxLines = NormalizeMaxLines(maxLines);

        var repo = await db.Set<Repository>()
            .AsNoTracking()
            .Include(r => r.Directory)
            .FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted && !r.Directory.IsDeleted, ct);

        if (repo is null)
            return PendingFileDiffPreviewDto.Unavailable(normalizedPath, "Repository was not found.");

        var absolutePath = ToAbsolutePath(repo.Directory.Path, normalizedPath);

        if (!File.Exists(absolutePath))
            return PendingFileDiffPreviewDto.Unavailable(normalizedPath, "Current file is missing on disk.");

        if (Directory.Exists(absolutePath))
            return PendingFileDiffPreviewDto.Unavailable(normalizedPath, "Selected path is a directory.");

        if (!IsTextExtension(Path.GetExtension(absolutePath)))
            return PendingFileDiffPreviewDto.Unavailable(normalizedPath, "Diff preview is available only for text files.");

        var identity = await db.Set<FileIdentity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                i => i.RepositoryId == repositoryId
                     && i.RelativePath == normalizedPath
                     && !i.IsDeleted,
                ct);

        if (identity is null)
            return PendingFileDiffPreviewDto.Unavailable(normalizedPath, "No baseline version found for this file.");

        var baselineVersion = await db.Set<FileVersion>()
            .AsNoTracking()
            .Where(v => v.FileIdentityId == identity.Id && !v.IsDeletionMarker && !v.IsDeleted)
            .OrderByDescending(v => v.CreatedAt)
            .ThenByDescending(v => v.Id)
            .FirstOrDefaultAsync(ct);

        if (baselineVersion is null)
            return PendingFileDiffPreviewDto.Unavailable(normalizedPath, "No previous content version found.");

        var blocks = baselineVersion.SizeBytes == 0
            ? []
            : await db.Set<FileVersionBlock>()
                .AsNoTracking()
                .Where(b => b.FileVersionId == baselineVersion.Id && !b.IsDeleted)
                .OrderBy(b => b.Sequence)
                .Select(b => new StoredFileBlockDto(
                    b.Sequence,
                    b.BlockHashBlake3,
                    b.LengthBytes,
                    b.StoredSizeBytes))
                .ToListAsync(ct);

        if (baselineVersion.SizeBytes > 0 && blocks.Count == 0)
            return PendingFileDiffPreviewDto.Unavailable(normalizedPath, "Baseline version blocks are missing.");

        var tempDir = Path.Combine(Path.GetTempPath(), "VeyraFlow", "pending-diff-preview");
        Directory.CreateDirectory(tempDir);

        var baselineTemp = Path.Combine(tempDir, $"{Guid.NewGuid():N}.baseline.tmp");

        try
        {
            if (baselineVersion.SizeBytes == 0)
            {
                await File.WriteAllTextAsync(baselineTemp, string.Empty, ct);
            }
            else
            {
                await contentStore.RestoreFileAsync(blocks, baselineTemp, true, ct);
            }

            var computed = await diffEngine.BuildDiffAsync(
                baselineTemp,
                absolutePath,
                normalizedMaxLines,
                ct);

            var summary = $"{normalizedPath}   +{computed.AddedLines} / -{computed.RemovedLines}" +
                          (computed.IsTruncated ? "  (truncated)" : string.Empty);

            return new PendingFileDiffPreviewDto(
                RelativePath: normalizedPath,
                IsAvailable: true,
                Message: summary,
                AddedLines: computed.AddedLines,
                RemovedLines: computed.RemovedLines,
                IsTruncated: computed.IsTruncated,
                Lines: computed.Lines,
                Hunks: computed.Hunks);
        }
        catch (Exception ex)
        {
            log.LogWarning(
                ex,
                "Failed to build pending file diff preview. RepositoryId {RepositoryId}. Path {Path}",
                repositoryId,
                normalizedPath);

            return PendingFileDiffPreviewDto.Unavailable(normalizedPath, "Unable to build diff preview for selected file.");
        }
        finally
        {
            TryDelete(baselineTemp);
        }
    }
    public async Task<TextDiffResultDto?> GetStoredTextDiffAsync(
        long leftFileVersionId,
        long rightFileVersionId,
        int maxLines,
        CancellationToken ct = default)
    {
        if (leftFileVersionId <= 0 || rightFileVersionId <= 0)
            return null;

        var normalizedMaxLines = NormalizeMaxLines(maxLines);

        var row = await db.Set<FileVersionTextDiff>()
            .AsNoTracking()
            .FirstOrDefaultAsync(d => !d.IsDeleted && d.LeftFileVersionId == leftFileVersionId
                                      && d.RightFileVersionId == rightFileVersionId
                                      && d.MaxLines == normalizedMaxLines,
                ct);

        if (row is null)
            return null;

        var lineRows = await db.Set<FileVersionTextDiffLine>()
            .AsNoTracking()
            .Where(l => l.DiffId == row.Id && !l.IsDeleted)
            .OrderBy(l => l.Sequence)
            .Select(l => new TextDiffLineDto(
                l.Kind,
                l.LeftLineNumber,
                l.RightLineNumber,
                l.TextLineAtom.Text))
            .ToListAsync(ct);

        var hunkRows = await db.Set<FileVersionTextDiffHunk>()
            .AsNoTracking()
            .Where(h => h.DiffId == row.Id && !h.IsDeleted)
            .OrderBy(h => h.Sequence)
            .Select(h => new TextDiffHunkDto(
                h.Sequence,
                h.Sequence,
                h.Sequence,
                h.OldStartLine,
                h.OldLineCount,
                h.NewStartLine,
                h.NewLineCount,
                h.ChangeKind))
            .ToListAsync(ct);

        if (lineRows.Count > 0)
        {
            var hunks = hunkRows.Count > 0
                ? await RebuildStoredHunksWithRangesAsync(row.Id, hunkRows, ct)
                : TextDiffHunkBuilder.Build(lineRows, 3);

            return new TextDiffResultDto(
                row.RelativePath,
                row.LeftFileVersionId,
                row.RightFileVersionId,
                row.AddedLines,
                row.RemovedLines,
                row.IsTruncated,
                lineRows,
                hunks);
        }

        try
        {
            var lines = JsonSerializer.Deserialize<List<TextDiffLineDto>>(row.LinesJson, DiffJsonOptions)
                        ?? [];

            var hunks = TextDiffHunkBuilder.Build(lines, 3);

            return new TextDiffResultDto(
                row.RelativePath,
                row.LeftFileVersionId,
                row.RightFileVersionId,
                row.AddedLines,
                row.RemovedLines,
                row.IsTruncated,
                lines,
                hunks);
        }
        catch (Exception ex)
        {
            log.LogWarning(
                ex,
                "Failed to deserialize cached text diff. LeftVersion {LeftVersion}. RightVersion {RightVersion}",
                leftFileVersionId,
                rightFileVersionId);

            return null;
        }
    }

    public async Task SaveStoredTextDiffAsync(
        TextDiffResultDto diff,
        int maxLines,
        CancellationToken ct = default)
    {
        await SaveStoredTextDiffCoreAsync(diff, maxLines, DateTime.UtcNow, ct);
    }

    private async Task SaveStoredTextDiffCoreAsync(
        TextDiffResultDto diff,
        int maxLines,
        DateTime nowUtc,
        CancellationToken ct)
    {
        if (diff.LeftFileVersionId <= 0 || diff.RightFileVersionId <= 0)
            return;

        var normalizedMaxLines = NormalizeMaxLines(maxLines);
        var normalizedLines = (diff.Lines ?? Array.Empty<TextDiffLineDto>())
            .Select(line => new TextDiffLineDto(
                NormalizeDiffKind(line.Kind),
                line.LeftLineNumber,
                line.RightLineNumber,
                line.Text ?? string.Empty))
            .ToList();

        var normalizedHunks = NormalizeHunks(
            diff.Hunks ?? Array.Empty<TextDiffHunkDto>(),
            normalizedLines,
            3);

        var linesJson = JsonSerializer.Serialize(normalizedLines, DiffJsonOptions);
        var diffKey = ComputeDiffKeySha256(diff.LeftFileVersionId, diff.RightFileVersionId, normalizedMaxLines);

        var existing = await db.Set<FileVersionTextDiff>()
            .Include(d => d.Hunks)
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => !d.IsDeleted && d.LeftFileVersionId == diff.LeftFileVersionId
                                      && d.RightFileVersionId == diff.RightFileVersionId
                                      && d.MaxLines == normalizedMaxLines,
                ct);

        if (existing is null)
        {
            existing = new FileVersionTextDiff
            {
                LeftFileVersionId = diff.LeftFileVersionId,
                RightFileVersionId = diff.RightFileVersionId,
                MaxLines = normalizedMaxLines,
                CreatedAt = nowUtc,
                IsDeleted = false,
                DeletedAt = null
            };

            db.Add(existing);
        }

        existing.DiffKeySha256 = diffKey;
        existing.RelativePath = TruncateForColumn(diff.RelativePath, 2048);
        existing.AddedLines = diff.AddedLines;
        existing.RemovedLines = diff.RemovedLines;
        existing.IsTruncated = diff.IsTruncated;
        existing.LinesJson = linesJson;
        existing.IsDeleted = false;
        existing.DeletedAt = null;
        existing.UpdatedAt = nowUtc;

        if (existing.Lines.Count > 0)
            db.RemoveRange(existing.Lines);

        if (existing.Hunks.Count > 0)
            db.RemoveRange(existing.Hunks);

        var createdHunks = new List<FileVersionTextDiffHunk>(normalizedHunks.Count);
        var hunkBySequence = new Dictionary<int, FileVersionTextDiffHunk>();

        if (normalizedHunks.Count > 0)
        {
            foreach (var h in normalizedHunks)
            {
                var entity = new FileVersionTextDiffHunk
                {
                    Diff = existing,
                    Sequence = h.Sequence,
                    OldStartLine = h.OldStartLine,
                    OldLineCount = h.OldLineCount,
                    NewStartLine = h.NewStartLine,
                    NewLineCount = h.NewLineCount,
                    ChangeKind = NormalizeHunkChangeKind(h.ChangeKind),
                    CreatedAt = nowUtc,
                    IsDeleted = false,
                    DeletedAt = null
                };

                createdHunks.Add(entity);
                hunkBySequence[h.Sequence] = entity;
            }

            db.AddRange(createdHunks);
        }

        if (normalizedLines.Count > 0)
        {
            var uniqueTexts = normalizedLines
                .Select(l => l.Text)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var textByHash = uniqueTexts
                .Select(text => new { Text = text, Hash = ComputeLineHashSha256(text) })
                .ToList();

            var hashes = textByHash.Select(x => x.Hash).Distinct(StringComparer.Ordinal).ToList();

            var existingAtoms = hashes.Count == 0
                ? []
                : await db.Set<TextLineAtom>()
                    .Where(a => hashes.Contains(a.HashSha256))
                    .ToListAsync(ct);

            var atomsByKey = existingAtoms.ToDictionary(
                a => (a.HashSha256, a.Text),
                a => a);

            var atomsByText = new Dictionary<string, TextLineAtom>(StringComparer.Ordinal);

            foreach (var item in textByHash)
            {
                if (!atomsByKey.TryGetValue((item.Hash, item.Text), out var atom))
                {
                    atom = new TextLineAtom
                    {
                        HashSha256 = item.Hash,
                        Text = item.Text,
                        CreatedAt = nowUtc
                    };

                    db.Add(atom);
                    atomsByKey[(item.Hash, item.Text)] = atom;
                }

                atomsByText[item.Text] = atom;
            }

            var lineToHunk = new Dictionary<int, (FileVersionTextDiffHunk Hunk, int InHunkSequence)>();
            foreach (var h in normalizedHunks)
            {
                if (!hunkBySequence.TryGetValue(h.Sequence, out var hunkEntity))
                    continue;

                var inSeq = 0;
                for (var seq = h.StartLineSequence; seq <= h.EndLineSequence; seq++)
                {
                    lineToHunk[seq] = (hunkEntity, inSeq);
                    inSeq++;
                }
            }

            var diffLines = new List<FileVersionTextDiffLine>(normalizedLines.Count);

            for (var i = 0; i < normalizedLines.Count; i++)
            {
                var line = normalizedLines[i];
                var hasHunk = lineToHunk.TryGetValue(i, out var hunkRef);

                diffLines.Add(new FileVersionTextDiffLine
                {
                    Diff = existing,
                    Sequence = i,
                    Kind = line.Kind,
                    LeftLineNumber = line.LeftLineNumber,
                    RightLineNumber = line.RightLineNumber,
                    Hunk = hasHunk ? hunkRef.Hunk : null,
                    InHunkSequence = hasHunk ? hunkRef.InHunkSequence : null,
                    TextLineAtom = atomsByText[line.Text],
                    CreatedAt = nowUtc,
                    IsDeleted = false,
                    DeletedAt = null
                });
            }

            db.AddRange(diffLines);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<FileVersionRestoreDto?> GetFileVersionRestoreDataAsync(
        long fileVersionId,
        CancellationToken ct = default)
    {
        var version = await db.Set<FileVersion>()
            .Include(v => v.FileIdentity)
            .FirstOrDefaultAsync(v => v.Id == fileVersionId && !v.IsDeleted && !v.FileIdentity.IsDeleted && !v.FileIdentity.Repository.IsDeleted, ct);

        if (version is null)
            return null;

        var blocks = await db.Set<FileVersionBlock>()
            .Where(b => b.FileVersionId == fileVersionId && !b.IsDeleted)
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


    private async Task PrecomputeSnapshotDiffsAsync(
        IReadOnlyCollection<PendingTextDiffPrecompute> items,
        CancellationToken ct)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var left = await GetFileVersionRestoreDataAsync(item.LeftFileVersionId, ct);
                var right = await GetFileVersionRestoreDataAsync(item.RightFileVersion.Id, ct);

                if (left is null || right is null)
                    continue;

                if (left.IsDeletionMarker || right.IsDeletionMarker)
                    continue;

                if (!HasStoredContent(left) || !HasStoredContent(right))
                    continue;

                if (!IsTextExtension(right.Extension))
                    continue;

                var tempDir = Path.Combine(Path.GetTempPath(), "VeyraFlow", "diff-precompute");
                Directory.CreateDirectory(tempDir);

                var leftTemp = Path.Combine(tempDir, $"{Guid.NewGuid():N}.left.tmp");
                var rightTemp = Path.Combine(tempDir, $"{Guid.NewGuid():N}.right.tmp");

                try
                {
                    await contentStore.RestoreFileAsync(left.Blocks, leftTemp, true, ct);
                    await contentStore.RestoreFileAsync(right.Blocks, rightTemp, true, ct);

                    var computed = await diffEngine.BuildDiffAsync(leftTemp, rightTemp, PrecomputedDiffMaxLines, ct);

                    var diff = new TextDiffResultDto(
                        item.RelativePath,
                        left.FileVersionId,
                        right.FileVersionId,
                        computed.AddedLines,
                        computed.RemovedLines,
                        computed.IsTruncated,
                        computed.Lines,
                        computed.Hunks);

                    await SaveStoredTextDiffCoreAsync(diff, PrecomputedDiffMaxLines, DateTime.UtcNow, ct);
                }
                finally
                {
                    TryDelete(leftTemp);
                    TryDelete(rightTemp);
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(
                    ex,
                    "Failed to precompute snapshot diff. LeftVersion {LeftVersion}. RightVersion {RightVersion}. Path {Path}",
                    item.LeftFileVersionId,
                    item.RightFileVersion.Id,
                    item.RelativePath);
            }
        }
    }

    private static bool HasStoredContent(FileVersionRestoreDto data)
        => data.SizeBytes == 0 || data.Blocks.Count > 0;

    private static bool IsTextExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return false;

        var normalized = extension.Trim();
        if (!normalized.StartsWith('.'))
            normalized = "." + normalized;

        return TextExtensions.Contains(normalized);
    }

    private async Task<IReadOnlyList<TextDiffHunkDto>> RebuildStoredHunksWithRangesAsync(
        long diffId,
        IReadOnlyList<TextDiffHunkDto> hunks,
        CancellationToken ct)
    {
        if (hunks.Count == 0)
            return Array.Empty<TextDiffHunkDto>();

        var mappings = await db.Set<FileVersionTextDiffLine>()
            .AsNoTracking()
            .Where(l => l.DiffId == diffId && !l.IsDeleted && l.HunkId.HasValue)
            .Select(l => new
            {
                HunkId = l.HunkId!.Value,
                l.Sequence
            })
            .GroupBy(x => x.HunkId)
            .Select(g => new
            {
                HunkId = g.Key,
                StartLineSequence = g.Min(x => x.Sequence),
                EndLineSequence = g.Max(x => x.Sequence)
            })
            .ToListAsync(ct);

        var byHunkId = mappings.ToDictionary(m => m.HunkId, m => (m.StartLineSequence, m.EndLineSequence));

        var hunkIdBySequence = await db.Set<FileVersionTextDiffHunk>()
            .AsNoTracking()
            .Where(h => h.DiffId == diffId && !h.IsDeleted)
            .Select(h => new { h.Id, h.Sequence })
            .ToListAsync(ct);

        var idBySequence = hunkIdBySequence.ToDictionary(x => x.Sequence, x => x.Id);

        var rebuilt = new List<TextDiffHunkDto>(hunks.Count);
        foreach (var h in hunks.OrderBy(x => x.Sequence))
        {
            var start = h.StartLineSequence;
            var end = h.EndLineSequence;

            if (idBySequence.TryGetValue(h.Sequence, out var hunkId)
                && byHunkId.TryGetValue(hunkId, out var range))
            {
                start = range.StartLineSequence;
                end = range.EndLineSequence;
            }

            rebuilt.Add(h with { StartLineSequence = start, EndLineSequence = end });
        }

        return rebuilt;
    }

    private static IReadOnlyList<TextDiffHunkDto> NormalizeHunks(
        IReadOnlyList<TextDiffHunkDto> hunks,
        IReadOnlyList<TextDiffLineDto> normalizedLines,
        int contextLines)
    {
        if (normalizedLines.Count == 0)
            return Array.Empty<TextDiffHunkDto>();

        var source = hunks.Count > 0
            ? hunks
            : TextDiffHunkBuilder.Build(normalizedLines, contextLines);

        var normalized = new List<TextDiffHunkDto>(source.Count);

        foreach (var h in source.OrderBy(x => x.Sequence))
        {
            var start = Math.Clamp(h.StartLineSequence, 0, normalizedLines.Count - 1);
            var end = Math.Clamp(h.EndLineSequence, start, normalizedLines.Count - 1);

            normalized.Add(new TextDiffHunkDto(
                Sequence: normalized.Count,
                StartLineSequence: start,
                EndLineSequence: end,
                OldStartLine: Math.Max(0, h.OldStartLine),
                OldLineCount: Math.Max(0, h.OldLineCount),
                NewStartLine: Math.Max(0, h.NewStartLine),
                NewLineCount: Math.Max(0, h.NewLineCount),
                ChangeKind: NormalizeHunkChangeKind(h.ChangeKind)));
        }

        return normalized;
    }

    private static string NormalizeHunkChangeKind(string? kind)
    {
        if (string.Equals(kind, "added", StringComparison.OrdinalIgnoreCase))
            return "added";

        if (string.Equals(kind, "removed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "deleted", StringComparison.OrdinalIgnoreCase))
            return "removed";

        return "modified";
    }
    private static string NormalizeDiffKind(string? kind)
    {
        if (string.Equals(kind, "add", StringComparison.OrdinalIgnoreCase))
            return "add";

        if (string.Equals(kind, "remove", StringComparison.OrdinalIgnoreCase))
            return "remove";

        return "equal";
    }

    private static string ComputeLineHashSha256(string text)
    {
        var payload = text ?? string.Empty;
        var bytes = Encoding.UTF8.GetBytes(payload);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static int NormalizeMaxLines(int maxLines)
        => Math.Clamp(maxLines, 200, 20_000);

    private static string ComputeDiffKeySha256(long leftFileVersionId, long rightFileVersionId, int maxLines)
    {
        var payload = $"{leftFileVersionId}:{rightFileVersionId}:{maxLines}";
        var bytes = Encoding.UTF8.GetBytes(payload);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string TruncateForColumn(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim();
        if (normalized.Length <= maxLength)
            return normalized;

        return normalized[..maxLength];
    }

    private static readonly IReadOnlyList<SnapshotLinkStateDto> EmptySnapshotLinkStates = Array.Empty<SnapshotLinkStateDto>();
    private static string? NormalizeSnapshotTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var title = value.Trim();
        if (title.Length <= 256)
            return title;
        return title[..256];
    }
    private static SnapshotLinkStateDto ToSnapshotLinkStateDto(SnapshotLinkState state)
        => new(
            state.FileIdentityId,
            state.FileVersionId,
            state.IsDeletionMarker,
            state.SizeBytes,
            state.VersionCreatedAtUtc,
            state.RelativePath,
            state.Name);
    private static RepositoryPathStateDto ToRepositoryPathStateDto(SnapshotEntryLight state)
        => new(
            state.RelativePath,
            state.Name,
            state.SizeBytes,
            state.LastWriteUtc,
            state.ContentHashSha256);

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

    private sealed record PendingTextDiffPrecompute(
        string RelativePath,
        long LeftFileVersionId,
        FileVersion RightFileVersion);
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
