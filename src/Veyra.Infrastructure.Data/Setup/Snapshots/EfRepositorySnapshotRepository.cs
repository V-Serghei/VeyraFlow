using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Common.Files;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.FileVersions;
using Veyra.Application.DTOs.PendingChanges;
using Veyra.Application.DTOs.Repository.Comparison;
using Veyra.Application.DTOs.Repository.Core;
using Veyra.Application.DTOs.Repository.Scanning;
using Veyra.Application.DTOs.Repository.Snapshots;
using Veyra.Application.DTOs.TextDiff;
using Veyra.Application.Services.Diff;
using Veyra.Domain.Entities;
using Veyra.Infrastructure.Data.Persistence;
using Veyra.Infrastructure.Data.Preview;
using Veyra.Infrastructure.Data.Setup.Models.Snapshots;

namespace Veyra.Infrastructure.Data.Setup.Snapshots;

public sealed class EfRepositorySnapshotRepository(
    VeyraDbContext db,
    IFileContentStore contentStore,
    ITextDiffEngine diffEngine,
    ISnapshotComparisonEngine snapshotComparison,
    ILogger<EfRepositorySnapshotRepository> log) : IRepositorySnapshotRepository
{
    private const int PrecomputedDiffMaxLines = 4000;
    private const int CurrentTextDiffStorageFormatVersion = 3;
    private const int ManagedPreviewChunkSize = 64 * 1024;
    private const int SnapshotEntryInsertBatchSize = 64;
    private const int SnapshotFileLinkInsertBatchSize = 128;
    private const int SnapshotComparisonMaxConcurrency = 8;
    private static readonly SemaphoreSlim[] TextDiffSaveStripes = Enumerable.Range(0, 64)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();

    private static readonly JsonSerializerOptions DiffJsonOptions = new();

    public async Task<SnapshotSaveResultDto> SaveSnapshotAsync(
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
        var overallTimer = Stopwatch.StartNew();
        var stageTimer = Stopwatch.StartNew();
        long preparationMs = 0;
        long snapshotHeaderMs = 0;
        long snapshotEntriesMs = 0;
        long identityPreparationMs = 0;
        long versionBuildMs = 0;
        long versionPersistMs = 0;
        long linkPersistMs = 0;
        long diffPrecomputeMs = 0;

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
        var busyFiles = new List<RepositoryBusyFileDto>();

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

        var previousEntriesByPath = previousSnapshotId == 0
            ? new Dictionary<string, RepositoryScanEntryDto>(StringComparer.OrdinalIgnoreCase)
            : await db.Set<RepositorySnapshotEntry>()
                .Where(e => e.RepositoryId == repositoryId && e.SnapshotId == previousSnapshotId && !e.IsDeleted)
                .ToDictionaryAsync(
                    e => e.RelativePath,
                    e => new RepositoryScanEntryDto(
                        e.RelativePath,
                        e.ParentRelativePath,
                        e.Name,
                        e.IsDirectory,
                        e.Extension,
                        e.SizeBytes,
                        e.LastWriteUtc,
                        e.ContentHashSha256,
                        null),
                    StringComparer.OrdinalIgnoreCase,
                    ct);
        var previousFilesByPath = previousEntriesByPath.Values
            .Where(e => !e.IsDirectory)
            .ToDictionary(
                e => e.RelativePath,
                e => (Hash: e.ContentHashSha256, SizeBytes: e.SizeBytes, Name: e.Name, LastWriteUtc: e.LastWriteUtc),
                StringComparer.OrdinalIgnoreCase);
        var currentFilesByPath = entries
            .Where(e => !e.IsDirectory)
            .ToDictionary(e => e.RelativePath, e => e, StringComparer.OrdinalIgnoreCase);
        if (!saveFileVersions && previousSnapshotId > 0)
        {
            var currentEntriesByPath = entries.ToDictionary(e => e.RelativePath, e => e, StringComparer.OrdinalIgnoreCase);
            if (!HasMeaningfulEntryChanges(currentEntriesByPath, previousEntriesByPath))
            {
                preparationMs = stageTimer.ElapsedMilliseconds;
                repo.FileCount = fileEntries;
                repo.TotalSizeBytes = totalFileBytes;
                repo.LastScannedAt = scannedAtUtc;
                repo.UpdatedAt = scannedAtUtc;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                log.LogInformation(
                    "Working snapshot skipped (no changes). RepositoryId {RepositoryId}. Entries {Entries}. Files {Files}. Trigger {Trigger}. PreparationMs {PreparationMs}. TotalMs {TotalMs}",
                    repositoryId,
                    totalEntries,
                    fileEntries,
                    safeTrigger,
                    preparationMs,
                    overallTimer.ElapsedMilliseconds);
                return SnapshotSaveResultDto.NoChanges();
            }
        }
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
            var needsVersionMaterialization = await NeedsVersionMaterializationAsync(repositoryId, currentFilesByPath, ct);
            if (comparison.ChangedFilesCount == 0 && !needsVersionMaterialization && !forceSnapshotCreation)
            {
                preparationMs = stageTimer.ElapsedMilliseconds;
                repo.FileCount = fileEntries;
                repo.TotalSizeBytes = totalFileBytes;
                repo.LastScannedAt = scannedAtUtc;
                repo.UpdatedAt = scannedAtUtc;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                log.LogInformation(
                    "Snapshot skipped (no changes). RepositoryId {RepositoryId}. Files {Files}. Trigger {Trigger}. PreparationMs {PreparationMs}. TotalMs {TotalMs}",
                    repositoryId,
                    fileEntries,
                    safeTrigger,
                    preparationMs,
                    overallTimer.ElapsedMilliseconds);
                return SnapshotSaveResultDto.NoChanges();
            }
        }
        preparationMs = stageTimer.ElapsedMilliseconds;

        if (!saveFileVersions)
            await DeleteWorkingSnapshotsAsync(repositoryId, ct);

        var snapshot = new RepositorySnapshot
        {
            RepositoryId = repositoryId,
            Trigger = safeTrigger,
            Title = NormalizeSnapshotTitle(snapshotTitle),
            TagsCsv = SerializeSnapshotTags(snapshotTags),
            CreatedAt = scannedAtUtc,
            TotalEntries = totalEntries,
            FileEntries = fileEntries,
            DirectoryEntries = dirEntries,
            TotalFileBytes = totalFileBytes,
            IsDeleted = false,
            DeletedAt = null
        };

        db.Add(snapshot);
        stageTimer.Restart();
        await db.SaveChangesAsync(ct);
        snapshotHeaderMs = stageTimer.ElapsedMilliseconds;

        if (totalEntries > 0)
        {
            stageTimer.Restart();
            await InsertSnapshotEntriesAsync(snapshot.Id, repositoryId, entries, scannedAtUtc, ct);
            snapshotEntriesMs = stageTimer.ElapsedMilliseconds;
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
                "Repository sync saved without file versions. RepositoryId {RepositoryId}. Entries {Entries}. Files {Files}. Trigger {Trigger}. PreparationMs {PreparationMs}. SnapshotHeaderMs {SnapshotHeaderMs}. SnapshotEntriesMs {SnapshotEntriesMs}. TotalMs {TotalMs}",
                repositoryId,
                totalEntries,
                fileEntries,
                safeTrigger,
                preparationMs,
                snapshotHeaderMs,
                snapshotEntriesMs,
                overallTimer.ElapsedMilliseconds);

            return SnapshotSaveResultDto.Created();
        }
        stageTimer.Restart();
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
        identityPreparationMs = stageTimer.ElapsedMilliseconds;

        var identityIds = identitiesByPath.Values.Select(i => i.Id).Distinct().ToList();

        stageTimer.Restart();
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
        var pendingLinks = new List<PendingSnapshotFileLink>();
        var pendingBlocks = new Dictionary<FileVersion, IReadOnlyList<StoredFileBlockDto>>();
        var pendingPrecomputedDiffs = new List<PendingTextDiffPrecompute>();
        var orderedPaths = allPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var totalVersionWork = orderedPaths.Count;
        var processedVersionWork = 0;

        foreach (var path in orderedPaths)
        {
            var identity = identitiesByPath[path];
            currentFilesByPath.TryGetValue(path, out var current);
            var hadPrevious = previousFilesByPath.TryGetValue(path, out var previous);
            planByPath.TryGetValue(path, out var planEntry);

            FileVersion? selectedVersion = null;

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

                var hasLatest = latestVersionsByIdentityId.TryGetValue(identity.Id, out var latestVersion);
                selectedVersion = latestVersion;
                var previousVersionForDiff = hasLatest ? selectedVersion : null;

                var latestHasBlocks = hasLatest
                                      && latestVersion is not null
                                      && latestVersion.Id > 0
                                      && (latestVersion.SizeBytes == 0 || latestVersionIdsWithBlocks.Contains(latestVersion.Id));
                var latestMatchesCurrent = hasLatest
                                           && latestVersion is not null
                                           && !latestVersion.IsDeletionMarker
                                           && latestVersion.SizeBytes == current.SizeBytes
                                           && string.Equals(
                                               latestVersion.ContentHashSha256 ?? string.Empty,
                                               currentHash,
                                               StringComparison.OrdinalIgnoreCase);

                var shouldCreateNewVersion = planEntry?.ShouldCreateNewVersion
                                             ?? (changed
                                                 || !hasLatest
                                                 || (hasLatest && latestVersion is not null && latestVersion.IsDeletionMarker)
                                                 || !latestHasBlocks
                                                 || !latestMatchesCurrent);

                if (!shouldCreateNewVersion && !latestMatchesCurrent)
                    shouldCreateNewVersion = true;

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
                    var storeResult = await TryStoreBlocksAsync(path, absolutePath, ct);
                    if (storeResult.BusyFile is not null)
                    {
                        busyFiles.Add(storeResult.BusyFile);
                        selectedVersion = null;
                    }
                    else
                    {
                        var stored = storeResult.StoredContent;
                        if (stored is null || (current.SizeBytes > 0 && stored.Blocks.Count == 0))
                            throw new InvalidOperationException($"Failed to store blocks for file {path}.");

                        pendingBlocks[newVersion] = stored.Blocks;
                        newVersions.Add(newVersion);
                        latestVersionsByIdentityId[identity.Id] = newVersion;
                        selectedVersion = newVersion;

                        if (previousVersionForDiff is { Id: > 0, IsDeletionMarker: false }
                            && latestHasBlocks
                            && CanBuildTextDiff(current.Extension))
                        {
                            pendingPrecomputedDiffs.Add(new PendingTextDiffPrecompute(
                                path,
                                previousVersionForDiff.Id,
                                newVersion));
                        }
                    }
                }
            }
            else
            {
                identity.IsDeleted = planEntry?.ShouldMarkIdentityDeleted ?? true;
                identity.DeletedAt = identity.IsDeleted ? scannedAtUtc : null;
                identity.UpdatedAt = scannedAtUtc;

                var hasLatest = latestVersionsByIdentityId.TryGetValue(identity.Id, out var latestVersion);
                selectedVersion = latestVersion;
                var shouldCreateDeletionMarker = planEntry?.ShouldCreateNewVersion
                                                ?? (!hasLatest
                                                    || !(latestVersion?.IsDeletionMarker ?? false));

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

            processedVersionWork++;
            if (selectedVersion is not null)
                pendingLinks.Add(new PendingSnapshotFileLink(identity.Id, selectedVersion));

            if (progress is not null
                && totalVersionWork > 0
                && (processedVersionWork == totalVersionWork
                    || processedVersionWork == 1
                    || processedVersionWork % 25 == 0))
            {
                var savePercent = Math.Clamp(
                    (int)Math.Round((double)processedVersionWork / totalVersionWork * 85.0),
                    1,
                    90);

                var message = busyFiles.Count > 0
                    ? $"Saving file versions {processedVersionWork}/{totalVersionWork}. Busy files: {busyFiles.Count}"
                    : $"Saving file versions {processedVersionWork}/{totalVersionWork}";

                progress.Report(new RepositoryScanProgressDto(
                    "save_versions",
                    savePercent,
                    processedVersionWork,
                    totalVersionWork,
                    message));
            }
        }
        versionBuildMs = stageTimer.ElapsedMilliseconds;

        if (previousSnapshotId > 0 && newVersions.Count == 0 && busyFiles.Count == 0 && !forceSnapshotCreation)
        {
            repo.FileCount = fileEntries;
            repo.TotalSizeBytes = totalFileBytes;
            repo.LastScannedAt = scannedAtUtc;
            repo.UpdatedAt = scannedAtUtc;

            await DeleteSnapshotShellAsync(snapshot.Id, repositoryId, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            log.LogInformation(
                "Snapshot skipped after version planning because no tracked file versions changed. RepositoryId {RepositoryId}. Entries {Entries}. Files {Files}. Trigger {Trigger}. PreparationMs {PreparationMs}. VersionBuildMs {VersionBuildMs}. TotalMs {TotalMs}",
                repositoryId,
                totalEntries,
                fileEntries,
                safeTrigger,
                preparationMs,
                versionBuildMs,
                overallTimer.ElapsedMilliseconds);

            return SnapshotSaveResultDto.NoChanges();
        }

        foreach (var pair in pendingBlocks)
        {
            var version = pair.Key;
            foreach (var block in pair.Value.OrderBy(b => b.Sequence))
            {
                version.Blocks.Add(new FileVersionBlock
                {
                    Sequence = block.Sequence,
                    BlockStorageKey = block.BlockStorageKey,
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

        repo.FileCount = fileEntries;
        repo.VersionCount += newVersions.Count;
        repo.TotalSizeBytes = totalFileBytes;
        repo.LastScannedAt = scannedAtUtc;
        repo.UpdatedAt = scannedAtUtc;

        progress?.Report(new RepositoryScanProgressDto(
            "save_persist_versions",
            92,
            processedVersionWork,
            totalVersionWork,
            "Persisting file versions"));

        stageTimer.Restart();
        await db.SaveChangesAsync(ct);
        versionPersistMs = stageTimer.ElapsedMilliseconds;

        progress?.Report(new RepositoryScanProgressDto(
            "save_links",
            95,
            pendingLinks.Count,
            Math.Max(pendingLinks.Count, totalVersionWork),
            "Linking snapshot to saved versions"));

        stageTimer.Restart();
        await InsertSnapshotFileLinksAsync(snapshot.Id, pendingLinks, scannedAtUtc, ct);
        linkPersistMs = stageTimer.ElapsedMilliseconds;

        if (pendingPrecomputedDiffs.Count > 0)
        {
            progress?.Report(new RepositoryScanProgressDto(
                "save_diff_precompute",
                97,
                pendingPrecomputedDiffs.Count,
                pendingPrecomputedDiffs.Count,
                "Preparing text diffs"));

            stageTimer.Restart();
            await PrecomputeSnapshotDiffsAsync(pendingPrecomputedDiffs, ct);
            diffPrecomputeMs = stageTimer.ElapsedMilliseconds;
        }

        await tx.CommitAsync(ct);

        var totalBlockRefs = pendingBlocks.Values.Sum(v => v.Count);

        log.LogInformation(
            "Snapshot saved for repository {RepositoryId}. Entries {Entries}. Files {Files}. FileIdentities {FileIdentities}. NewVersions {NewVersions}. Links {Links}. BlockRefs {BlockRefs}. BusyFiles {BusyFiles}. Trigger {Trigger}. PreparationMs {PreparationMs}. SnapshotHeaderMs {SnapshotHeaderMs}. SnapshotEntriesMs {SnapshotEntriesMs}. IdentityPreparationMs {IdentityPreparationMs}. VersionBuildMs {VersionBuildMs}. VersionPersistMs {VersionPersistMs}. LinkPersistMs {LinkPersistMs}. DiffPrecomputeMs {DiffPrecomputeMs}. TotalMs {TotalMs}",
            repositoryId,
            totalEntries,
            fileEntries,
            identitiesByPath.Count,
            newVersions.Count,
            pendingLinks.Count,
            totalBlockRefs,
            busyFiles.Count,
            safeTrigger,
            preparationMs,
            snapshotHeaderMs,
            snapshotEntriesMs,
            identityPreparationMs,
            versionBuildMs,
            versionPersistMs,
            linkPersistMs,
            diffPrecomputeMs,
            overallTimer.ElapsedMilliseconds);

        return SnapshotSaveResultDto.Created(busyFiles);
    }

    public async Task<SnapshotSaveResultDto> ApplyWorkingSnapshotDeltaAsync(
        int repositoryId,
        string trigger,
        DateTime scannedAtUtc,
        IReadOnlyCollection<RepositoryScanEntryDto> upsertEntries,
        IReadOnlyCollection<string> removedPaths,
        CancellationToken ct = default)
    {
        if (repositoryId <= 0)
            return SnapshotSaveResultDto.Skipped();

        var normalizedUpserts = upsertEntries
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.RelativePath))
            .GroupBy(static entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var normalizedRemovedPaths = removedPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path.Replace('\\', '/').Trim('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedUpserts.Count == 0 && normalizedRemovedPaths.Count == 0)
            return SnapshotSaveResultDto.NoChanges();

        db.ChangeTracker.Clear();

        var repo = await db.Set<Repository>()
            .Include(r => r.Directory)
            .FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted, ct);

        if (repo is null)
            return SnapshotSaveResultDto.Skipped();

        var safeTrigger = string.IsNullOrWhiteSpace(trigger)
            ? "sync_live_watcher_incremental"
            : trigger.Trim();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var latestSnapshot = await db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => new WorkingSnapshotSeed(
                s.Id,
                s.CreatedAt,
                db.Set<SnapshotFileLink>().Any(l => l.SnapshotId == s.Id && !l.IsDeleted)))
            .FirstOrDefaultAsync(ct);

        RepositorySnapshot targetSnapshot;
        if (latestSnapshot is null)
        {
            targetSnapshot = await CreateWorkingSnapshotShellAsync(repositoryId, safeTrigger, scannedAtUtc, ct);
        }
        else if (latestSnapshot.HasFileLinks)
        {
            await DeleteWorkingSnapshotsAsync(repositoryId, ct);
            targetSnapshot = await CreateWorkingSnapshotShellAsync(repositoryId, safeTrigger, scannedAtUtc, ct);
            await CloneSnapshotEntriesAsync(latestSnapshot.SnapshotId, targetSnapshot.Id, repositoryId, scannedAtUtc, ct);
        }
        else
        {
            targetSnapshot = await db.Set<RepositorySnapshot>()
                .FirstAsync(s => s.Id == latestSnapshot.SnapshotId, ct);
        }

        if (normalizedRemovedPaths.Count > 0)
            await DeleteSnapshotEntriesByRootsAsync(targetSnapshot.Id, repositoryId, normalizedRemovedPaths, ct);

        var exactPathsToDelete = normalizedUpserts.Keys
            .Where(path => !normalizedRemovedPaths.Any(root => IsPathOrDescendant(path, root)))
            .ToList();
        if (exactPathsToDelete.Count > 0)
            await DeleteSnapshotEntriesByExactPathsAsync(targetSnapshot.Id, repositoryId, exactPathsToDelete, ct);

        if (normalizedUpserts.Count > 0)
        {
            await InsertSnapshotEntriesAsync(
                targetSnapshot.Id,
                repositoryId,
                normalizedUpserts.Values.ToList(),
                scannedAtUtc,
                ct);
        }

        await UpdateWorkingSnapshotHeaderAsync(targetSnapshot, repo, safeTrigger, scannedAtUtc, ct);
        await tx.CommitAsync(ct);

        log.LogInformation(
            "Working snapshot delta applied. RepositoryId {RepositoryId}. Upserts {Upserts}. RemovedRoots {RemovedRoots}. SnapshotId {SnapshotId}. Trigger {Trigger}",
            repositoryId,
            normalizedUpserts.Count,
            normalizedRemovedPaths.Count,
            targetSnapshot.Id,
            safeTrigger);

        return SnapshotSaveResultDto.Created();
    }

    public async Task<SnapshotSaveResultDto> ApplyVersionedSnapshotDeltaAsync(
        int repositoryId,
        string trigger,
        DateTime scannedAtUtc,
        IReadOnlyCollection<RepositoryScanEntryDto> entries,
        IReadOnlyCollection<RepositoryScanEntryDto> upsertEntries,
        IReadOnlyCollection<string> removedPaths,
        CancellationToken ct = default)
    {
        if (repositoryId <= 0)
            return SnapshotSaveResultDto.Skipped();

        var normalizedEntries = entries
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.RelativePath))
            .GroupBy(static entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var normalizedUpserts = upsertEntries
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.RelativePath))
            .GroupBy(static entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var normalizedRemovedPaths = removedPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path.Replace('\\', '/').Trim('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedUpserts.Count == 0 && normalizedRemovedPaths.Count == 0)
            return SnapshotSaveResultDto.NoChanges();

        db.ChangeTracker.Clear();

        var repo = await db.Set<Repository>()
            .Include(r => r.Directory)
            .FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted, ct);

        if (repo is null)
            return SnapshotSaveResultDto.Skipped();

        var previousSnapshotId = await db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
            .Where(s => db.Set<SnapshotFileLink>().Any(l => l.SnapshotId == s.Id && !l.IsDeleted))
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => s.Id)
            .FirstOrDefaultAsync(ct);

        if (previousSnapshotId == 0)
            return SnapshotSaveResultDto.Skipped();

        var safeTrigger = string.IsNullOrWhiteSpace(trigger)
            ? "auto_snapshot_live_watcher_incremental"
            : trigger.Trim();
        var totalEntries = normalizedEntries.Count;
        var fileEntries = normalizedEntries.Values.Count(static entry => !entry.IsDirectory);
        var totalFileBytes = normalizedEntries.Values
            .Where(static entry => !entry.IsDirectory)
            .Sum(static entry => entry.SizeBytes);
        var busyFiles = new List<RepositoryBusyFileDto>();

        var previousFiles = await db.Set<RepositorySnapshotEntry>()
            .Where(entry => entry.RepositoryId == repositoryId
                            && entry.SnapshotId == previousSnapshotId
                            && !entry.IsDirectory
                            && !entry.IsDeleted)
            .Select(entry => new SnapshotEntryLight(
                entry.RelativePath,
                entry.Name,
                entry.SizeBytes,
                entry.LastWriteUtc,
                entry.ContentHashSha256))
            .ToListAsync(ct);
        var previousFilesByPath = previousFiles.ToDictionary(static entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);
        var currentFilesByPath = normalizedEntries.Values
            .Where(static entry => !entry.IsDirectory)
            .ToDictionary(static entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);

        var affectedPaths = normalizedUpserts.Values
            .Where(static entry => !entry.IsDirectory)
            .Select(static entry => entry.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var removalRoot in normalizedRemovedPaths)
        {
            foreach (var previousPath in previousFilesByPath.Keys.Where(path => IsPathOrDescendant(path, removalRoot)))
                affectedPaths.Add(previousPath);
        }

        if (affectedPaths.Count == 0)
        {
            repo.FileCount = fileEntries;
            repo.TotalSizeBytes = totalFileBytes;
            repo.LastScannedAt = scannedAtUtc;
            repo.UpdatedAt = scannedAtUtc;
            await db.SaveChangesAsync(ct);

            log.LogInformation(
                "Versioned snapshot delta skipped because no tracked file changes were detected. RepositoryId {RepositoryId}. Entries {Entries}. Upserts {Upserts}. RemovedRoots {RemovedRoots}. Trigger {Trigger}",
                repositoryId,
                totalEntries,
                normalizedUpserts.Count,
                normalizedRemovedPaths.Count,
                safeTrigger);

            return SnapshotSaveResultDto.NoChanges();
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var snapshot = new RepositorySnapshot
        {
            RepositoryId = repositoryId,
            Trigger = safeTrigger,
            Title = null,
            TagsCsv = null,
            CreatedAt = scannedAtUtc,
            TotalEntries = totalEntries,
            FileEntries = fileEntries,
            DirectoryEntries = totalEntries - fileEntries,
            TotalFileBytes = totalFileBytes,
            IsDeleted = false,
            DeletedAt = null
        };

        db.Add(snapshot);
        await db.SaveChangesAsync(ct);

        if (normalizedEntries.Count > 0)
        {
            await InsertSnapshotEntriesAsync(
                snapshot.Id,
                repositoryId,
                normalizedEntries.Values.OrderBy(static entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),
                scannedAtUtc,
                ct);
        }

        await CloneSnapshotFileLinksAsync(previousSnapshotId, snapshot.Id, scannedAtUtc, ct);

        var identityPaths = affectedPaths.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToList();
        var identitiesByPath = await db.Set<FileIdentity>()
            .IgnoreQueryFilters()
            .Where(identity => identity.RepositoryId == repositoryId && identityPaths.Contains(identity.RelativePath))
            .ToDictionaryAsync(identity => identity.RelativePath, StringComparer.OrdinalIgnoreCase, ct);

        foreach (var path in identityPaths)
        {
            if (identitiesByPath.ContainsKey(path))
                continue;

            currentFilesByPath.TryGetValue(path, out var current);
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

        var identityIds = identitiesByPath.Values.Select(static identity => identity.Id).Distinct().ToList();
        var latestVersions = identityIds.Count == 0
            ? new List<FileVersion>()
            : await db.Set<FileVersion>()
                .Where(version => identityIds.Contains(version.FileIdentityId) && !version.IsDeleted)
                .OrderByDescending(version => version.CreatedAt)
                .ThenByDescending(version => version.Id)
                .ToListAsync(ct);
        var latestVersionsByIdentityId = latestVersions
            .GroupBy(static version => version.FileIdentityId)
            .ToDictionary(static group => group.Key, static group => group.First());
        var latestVersionIds = latestVersionsByIdentityId.Values
            .Select(static version => version.Id)
            .Distinct()
            .ToList();
        var latestVersionIdsWithBlocks = latestVersionIds.Count == 0
            ? new HashSet<long>()
            : (await db.Set<FileVersionBlock>()
                    .Where(block => latestVersionIds.Contains(block.FileVersionId) && !block.IsDeleted)
                    .Select(block => block.FileVersionId)
                    .Distinct()
                    .ToListAsync(ct))
                .ToHashSet();
        var previousLinksByIdentityId = identityIds.Count == 0
            ? new Dictionary<long, long>()
            : await db.Set<SnapshotFileLink>()
                .Where(link => link.SnapshotId == previousSnapshotId && identityIds.Contains(link.FileIdentityId) && !link.IsDeleted)
                .ToDictionaryAsync(link => link.FileIdentityId, link => link.FileVersionId, ct);

        var newVersions = new List<FileVersion>();
        var pendingBlocks = new Dictionary<FileVersion, IReadOnlyList<StoredFileBlockDto>>();
        var pendingLinks = new List<PendingSnapshotFileLink>();
        var replacementIdentityIds = new HashSet<long>();
        var pendingPrecomputedDiffs = new List<PendingTextDiffPrecompute>();

        foreach (var path in identityPaths)
        {
            var identity = identitiesByPath[path];
            currentFilesByPath.TryGetValue(path, out var current);
            var hadPrevious = previousFilesByPath.TryGetValue(path, out var previous);
            latestVersionsByIdentityId.TryGetValue(identity.Id, out var latestVersion);
            FileVersion? selectedVersion = latestVersion;

            if (current is not null)
            {
                identity.Name = current.Name;
                identity.Extension = current.Extension;
                identity.IsDeleted = false;
                identity.DeletedAt = null;
                identity.UpdatedAt = scannedAtUtc;

                var previousHash = previous?.ContentHashSha256 ?? string.Empty;
                var currentHash = current.ContentHashSha256 ?? string.Empty;
                var changed = !hadPrevious
                              || !string.Equals(previousHash, currentHash, StringComparison.OrdinalIgnoreCase)
                              || previous?.SizeBytes != current.SizeBytes;
                var hasLatest = latestVersion is not null;
                var latestHasBlocks = hasLatest
                                      && latestVersion is not null
                                      && latestVersion.Id > 0
                                      && (latestVersion.SizeBytes == 0 || latestVersionIdsWithBlocks.Contains(latestVersion.Id));
                var latestMatchesCurrent = hasLatest
                                           && latestVersion is not null
                                           && !latestVersion.IsDeletionMarker
                                           && latestVersion.SizeBytes == current.SizeBytes
                                           && string.Equals(
                                               latestVersion.ContentHashSha256 ?? string.Empty,
                                               currentHash,
                                               StringComparison.OrdinalIgnoreCase);
                var previousVersionForDiff = hasLatest ? latestVersion : null;
                var shouldCreateNewVersion = changed
                                             || !hasLatest
                                             || (hasLatest && latestVersion is not null && latestVersion.IsDeletionMarker)
                                             || !latestHasBlocks
                                             || !latestMatchesCurrent;

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
                    var storeResult = await TryStoreBlocksAsync(path, absolutePath, ct);
                    if (storeResult.BusyFile is not null)
                    {
                        busyFiles.Add(storeResult.BusyFile);
                        selectedVersion = null;
                    }
                    else
                    {
                        var stored = storeResult.StoredContent;
                        if (stored is null || (current.SizeBytes > 0 && stored.Blocks.Count == 0))
                            throw new InvalidOperationException($"Failed to store blocks for file {path}.");

                        pendingBlocks[newVersion] = stored.Blocks;
                        newVersions.Add(newVersion);
                        latestVersionsByIdentityId[identity.Id] = newVersion;
                        selectedVersion = newVersion;

                        if (previousVersionForDiff is { Id: > 0, IsDeletionMarker: false }
                            && latestHasBlocks
                            && CanBuildTextDiff(current.Extension))
                        {
                            pendingPrecomputedDiffs.Add(new PendingTextDiffPrecompute(
                                path,
                                previousVersionForDiff.Id,
                                newVersion));
                        }
                    }
                }
            }
            else
            {
                identity.IsDeleted = true;
                identity.DeletedAt = scannedAtUtc;
                identity.UpdatedAt = scannedAtUtc;

                var hasLatest = latestVersion is not null;
                var shouldCreateDeletionMarker = !hasLatest || !(latestVersion?.IsDeletionMarker ?? false);
                if (shouldCreateDeletionMarker)
                {
                    selectedVersion = new FileVersion
                    {
                        FileIdentityId = identity.Id,
                        ContentHashSha256 = previous?.ContentHashSha256 ?? "deleted",
                        SizeBytes = previous?.SizeBytes ?? 0,
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

            if (selectedVersion is null)
                continue;

            if (!previousLinksByIdentityId.TryGetValue(identity.Id, out var previousLinkedVersionId)
                || previousLinkedVersionId != selectedVersion.Id)
            {
                replacementIdentityIds.Add(identity.Id);
                pendingLinks.Add(new PendingSnapshotFileLink(identity.Id, selectedVersion));
            }
        }

        if (newVersions.Count == 0 && pendingLinks.Count == 0 && busyFiles.Count == 0)
        {
            repo.FileCount = fileEntries;
            repo.TotalSizeBytes = totalFileBytes;
            repo.LastScannedAt = scannedAtUtc;
            repo.UpdatedAt = scannedAtUtc;

            await DeleteSnapshotShellAsync(snapshot.Id, repositoryId, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            log.LogInformation(
                "Versioned snapshot delta skipped because affected paths produced no new tracked file versions. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}. Entries {Entries}. Files {Files}. AffectedPaths {AffectedPaths}. Trigger {Trigger}",
                repositoryId,
                snapshot.Id,
                totalEntries,
                fileEntries,
                identityPaths.Count,
                safeTrigger);

            return SnapshotSaveResultDto.NoChanges();
        }

        foreach (var pair in pendingBlocks)
        {
            var version = pair.Key;
            foreach (var block in pair.Value.OrderBy(static block => block.Sequence))
            {
                version.Blocks.Add(new FileVersionBlock
                {
                    Sequence = block.Sequence,
                    BlockStorageKey = block.BlockStorageKey,
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

        repo.FileCount = fileEntries;
        repo.VersionCount += newVersions.Count;
        repo.TotalSizeBytes = totalFileBytes;
        repo.LastScannedAt = scannedAtUtc;
        repo.UpdatedAt = scannedAtUtc;

        await db.SaveChangesAsync(ct);

        if (replacementIdentityIds.Count > 0)
            await DeleteSnapshotFileLinksByIdentityIdsAsync(snapshot.Id, replacementIdentityIds, ct);

        if (pendingLinks.Count > 0)
            await InsertSnapshotFileLinksAsync(snapshot.Id, pendingLinks, scannedAtUtc, ct);

        if (pendingPrecomputedDiffs.Count > 0)
            await PrecomputeSnapshotDiffsAsync(pendingPrecomputedDiffs, ct);

        await tx.CommitAsync(ct);

        log.LogInformation(
            "Versioned snapshot delta applied. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}. Entries {Entries}. Files {Files}. AffectedPaths {AffectedPaths}. NewVersions {NewVersions}. ReplacedLinks {ReplacedLinks}. BusyFiles {BusyFiles}. Trigger {Trigger}",
            repositoryId,
            snapshot.Id,
            totalEntries,
            fileEntries,
            identityPaths.Count,
            newVersions.Count,
            pendingLinks.Count,
            busyFiles.Count,
            safeTrigger);

        return SnapshotSaveResultDto.Created(busyFiles);
    }

    private async Task InsertSnapshotEntriesAsync(
        long snapshotId,
        int repositoryId,
        IReadOnlyCollection<RepositoryScanEntryDto> entries,
        DateTime createdAtUtc,
        CancellationToken ct)
    {
        if (entries.Count == 0)
            return;

        var entryList = entries
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.RelativePath))
            .Select(NormalizeSnapshotEntry)
            .GroupBy(static entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.Last())
            .ToList();

        if (entryList.Count == 0)
            return;

        if (!db.Database.IsSqlite())
        {
            var rows = entryList.Select(e => new RepositorySnapshotEntry
            {
                SnapshotId = snapshotId,
                RepositoryId = repositoryId,
                RelativePath = e.RelativePath,
                ParentRelativePath = e.ParentRelativePath,
                Name = e.Name,
                IsDirectory = e.IsDirectory,
                Extension = e.Extension,
                SizeBytes = e.SizeBytes,
                LastWriteUtc = e.LastWriteUtc,
                ContentHashSha256 = e.ContentHashSha256,
                CreatedAt = createdAtUtc,
                IsDeleted = false,
                DeletedAt = null
            }).ToList();

            db.AddRange(rows);
            return;
        }

        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        var transaction = db.Database.CurrentTransaction?.GetDbTransaction();

        for (var offset = 0; offset < entryList.Count; offset += SnapshotEntryInsertBatchSize)
        {
            var chunk = entryList.Skip(offset).Take(SnapshotEntryInsertBatchSize).ToList();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandType = CommandType.Text;

            var sql = new StringBuilder();
            sql.Append("INSERT INTO \"RepositorySnapshotEntries\" (");
            sql.Append("\"ContentHashSha256\", \"CreatedAt\", \"DeletedAt\", \"Extension\", \"IsDirectory\", ");
            sql.Append("\"LastWriteUtc\", \"Name\", \"ParentRelativePath\", \"RelativePath\", \"RepositoryId\", ");
            sql.Append("\"SizeBytes\", \"SnapshotId\", \"IsDeleted\") VALUES ");

            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0)
                    sql.Append(", ");

                sql.Append('(');
                sql.AppendJoin(", ", new[]
                {
                    AddParameter(command, $"@p{i}_hash", chunk[i].ContentHashSha256),
                    AddParameter(command, $"@p{i}_created", createdAtUtc),
                    AddParameter(command, $"@p{i}_deleted", null),
                    AddParameter(command, $"@p{i}_ext", chunk[i].Extension),
                    AddParameter(command, $"@p{i}_isdir", chunk[i].IsDirectory),
                    AddParameter(command, $"@p{i}_lastwrite", chunk[i].LastWriteUtc),
                    AddParameter(command, $"@p{i}_name", chunk[i].Name),
                    AddParameter(command, $"@p{i}_parent", chunk[i].ParentRelativePath),
                    AddParameter(command, $"@p{i}_relative", chunk[i].RelativePath),
                    AddParameter(command, $"@p{i}_repo", repositoryId),
                    AddParameter(command, $"@p{i}_size", chunk[i].SizeBytes),
                    AddParameter(command, $"@p{i}_snapshot", snapshotId),
                    AddParameter(command, $"@p{i}_isdeleted", false)
                });
                sql.Append(')');
            }

            sql.Append("""
                 ON CONFLICT("SnapshotId", "RelativePath") DO UPDATE SET
                    "ContentHashSha256" = excluded."ContentHashSha256",
                    "CreatedAt" = excluded."CreatedAt",
                    "DeletedAt" = NULL,
                    "Extension" = excluded."Extension",
                    "IsDirectory" = excluded."IsDirectory",
                    "LastWriteUtc" = excluded."LastWriteUtc",
                    "Name" = excluded."Name",
                    "ParentRelativePath" = excluded."ParentRelativePath",
                    "RepositoryId" = excluded."RepositoryId",
                    "SizeBytes" = excluded."SizeBytes",
                    "IsDeleted" = 0
                """);

            command.CommandText = sql.ToString();
            await command.ExecuteNonQueryAsync(ct);
        }

        log.LogDebug(
            "Snapshot entries inserted via sqlite batch path. SnapshotId {SnapshotId}. RepositoryId {RepositoryId}. Entries {Entries}. BatchSize {BatchSize}",
            snapshotId,
            repositoryId,
            entryList.Count,
            SnapshotEntryInsertBatchSize);
    }

    private async Task InsertSnapshotFileLinksAsync(
        long snapshotId,
        IReadOnlyCollection<PendingSnapshotFileLink> links,
        DateTime createdAtUtc,
        CancellationToken ct)
    {
        if (links.Count == 0)
            return;

        if (!db.Database.IsSqlite())
        {
            var rows = links.Select(x => new SnapshotFileLink
            {
                SnapshotId = snapshotId,
                FileIdentityId = x.FileIdentityId,
                FileVersionId = x.FileVersion.Id,
                CreatedAt = createdAtUtc,
                IsDeleted = false,
                DeletedAt = null
            }).ToList();

            db.AddRange(rows);
            await db.SaveChangesAsync(ct);
            return;
        }

        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        var transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        var linkList = links as IList<PendingSnapshotFileLink> ?? links.ToList();

        for (var offset = 0; offset < linkList.Count; offset += SnapshotFileLinkInsertBatchSize)
        {
            var chunk = linkList.Skip(offset).Take(SnapshotFileLinkInsertBatchSize).ToList();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandType = CommandType.Text;

            var sql = new StringBuilder();
            sql.Append("INSERT INTO \"SnapshotFileLinks\" (");
            sql.Append("\"CreatedAt\", \"DeletedAt\", \"FileIdentityId\", \"FileVersionId\", \"IsDeleted\", \"SnapshotId\") VALUES ");

            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0)
                    sql.Append(", ");

                sql.Append('(');
                sql.AppendJoin(", ", new[]
                {
                    AddParameter(command, $"@l{i}_created", createdAtUtc),
                    AddParameter(command, $"@l{i}_deleted", null),
                    AddParameter(command, $"@l{i}_identity", chunk[i].FileIdentityId),
                    AddParameter(command, $"@l{i}_version", chunk[i].FileVersion.Id),
                    AddParameter(command, $"@l{i}_isdeleted", false),
                    AddParameter(command, $"@l{i}_snapshot", snapshotId)
                });
                sql.Append(')');
            }

            command.CommandText = sql.ToString();
            await command.ExecuteNonQueryAsync(ct);
        }

        log.LogDebug(
            "Snapshot file links inserted via sqlite batch path. SnapshotId {SnapshotId}. Links {Links}. BatchSize {BatchSize}",
            snapshotId,
            linkList.Count,
            SnapshotFileLinkInsertBatchSize);
    }

    private static string AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
        return name;
    }

    private async Task DeleteWorkingSnapshotsAsync(int repositoryId, CancellationToken ct)
    {
        var workingSnapshotIds = await db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
            .Where(s => !db.Set<SnapshotFileLink>().Any(l => l.SnapshotId == s.Id && !l.IsDeleted))
            .Select(s => s.Id)
            .ToListAsync(ct);

        if (workingSnapshotIds.Count == 0)
            return;

        await db.Set<RepositorySnapshotEntry>()
            .Where(e => e.RepositoryId == repositoryId && workingSnapshotIds.Contains(e.SnapshotId) && !e.IsDeleted)
            .ExecuteDeleteAsync(ct);

        await db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId && workingSnapshotIds.Contains(s.Id) && !s.IsDeleted)
            .ExecuteDeleteAsync(ct);
    }

    private async Task DeleteSnapshotShellAsync(long snapshotId, int repositoryId, CancellationToken ct)
    {
        if (snapshotId <= 0 || repositoryId <= 0)
            return;

        await db.Set<SnapshotFileLink>()
            .Where(l => l.SnapshotId == snapshotId && !l.IsDeleted)
            .ExecuteDeleteAsync(ct);

        await db.Set<RepositorySnapshotEntry>()
            .Where(e => e.RepositoryId == repositoryId && e.SnapshotId == snapshotId && !e.IsDeleted)
            .ExecuteDeleteAsync(ct);

        await db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId && s.Id == snapshotId && !s.IsDeleted)
            .ExecuteDeleteAsync(ct);
    }

    private async Task<RepositorySnapshot> CreateWorkingSnapshotShellAsync(
        int repositoryId,
        string trigger,
        DateTime createdAtUtc,
        CancellationToken ct)
    {
        var snapshot = new RepositorySnapshot
        {
            RepositoryId = repositoryId,
            Trigger = trigger,
            Title = null,
            TagsCsv = null,
            CreatedAt = createdAtUtc,
            TotalEntries = 0,
            FileEntries = 0,
            DirectoryEntries = 0,
            TotalFileBytes = 0,
            IsDeleted = false,
            DeletedAt = null
        };

        db.Add(snapshot);
        await db.SaveChangesAsync(ct);
        return snapshot;
    }

    private async Task CloneSnapshotEntriesAsync(
        long sourceSnapshotId,
        long targetSnapshotId,
        int repositoryId,
        DateTime createdAtUtc,
        CancellationToken ct)
    {
        if (sourceSnapshotId <= 0 || targetSnapshotId <= 0)
            return;

        if (!db.Database.IsSqlite())
        {
            var sourceEntries = await db.Set<RepositorySnapshotEntry>()
                .Where(entry => entry.RepositoryId == repositoryId && entry.SnapshotId == sourceSnapshotId && !entry.IsDeleted)
                .OrderBy(entry => entry.RelativePath)
                .Select(entry => new RepositoryScanEntryDto(
                    entry.RelativePath,
                    entry.ParentRelativePath,
                    entry.Name,
                    entry.IsDirectory,
                    entry.Extension,
                    entry.SizeBytes,
                    entry.LastWriteUtc,
                    entry.ContentHashSha256,
                    null))
                .ToListAsync(ct);

            await InsertSnapshotEntriesAsync(targetSnapshotId, repositoryId, sourceEntries, createdAtUtc, ct);
            return;
        }

        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandType = CommandType.Text;
        command.CommandText = """
            INSERT INTO "RepositorySnapshotEntries" (
                "ContentHashSha256",
                "CreatedAt",
                "DeletedAt",
                "Extension",
                "IsDirectory",
                "LastWriteUtc",
                "Name",
                "ParentRelativePath",
                "RelativePath",
                "RepositoryId",
                "SizeBytes",
                "SnapshotId",
                "IsDeleted")
            SELECT
                "ContentHashSha256",
                @createdAt,
                NULL,
                "Extension",
                "IsDirectory",
                "LastWriteUtc",
                "Name",
                "ParentRelativePath",
                "RelativePath",
                "RepositoryId",
                "SizeBytes",
                @targetSnapshotId,
                0
            FROM "RepositorySnapshotEntries"
            WHERE "RepositoryId" = @repositoryId
              AND "SnapshotId" = @sourceSnapshotId
              AND "IsDeleted" = 0;
            """;

        AddParameter(command, "@createdAt", createdAtUtc);
        AddParameter(command, "@targetSnapshotId", targetSnapshotId);
        AddParameter(command, "@repositoryId", repositoryId);
        AddParameter(command, "@sourceSnapshotId", sourceSnapshotId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task CloneSnapshotFileLinksAsync(
        long sourceSnapshotId,
        long targetSnapshotId,
        DateTime createdAtUtc,
        CancellationToken ct)
    {
        if (sourceSnapshotId <= 0 || targetSnapshotId <= 0)
            return;

        if (!db.Database.IsSqlite())
        {
            var rows = await db.Set<SnapshotFileLink>()
                .Where(link => link.SnapshotId == sourceSnapshotId && !link.IsDeleted)
                .Select(link => new SnapshotFileLink
                {
                    SnapshotId = targetSnapshotId,
                    FileIdentityId = link.FileIdentityId,
                    FileVersionId = link.FileVersionId,
                    CreatedAt = createdAtUtc,
                    IsDeleted = false,
                    DeletedAt = null
                })
                .ToListAsync(ct);

            if (rows.Count > 0)
                db.AddRange(rows);

            return;
        }

        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandType = CommandType.Text;
        command.CommandText = """
            INSERT INTO "SnapshotFileLinks" (
                "CreatedAt",
                "DeletedAt",
                "FileIdentityId",
                "FileVersionId",
                "IsDeleted",
                "SnapshotId")
            SELECT
                @createdAt,
                NULL,
                "FileIdentityId",
                "FileVersionId",
                0,
                @targetSnapshotId
            FROM "SnapshotFileLinks"
            WHERE "SnapshotId" = @sourceSnapshotId
              AND "IsDeleted" = 0;
            """;

        AddParameter(command, "@createdAt", createdAtUtc);
        AddParameter(command, "@targetSnapshotId", targetSnapshotId);
        AddParameter(command, "@sourceSnapshotId", sourceSnapshotId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task DeleteSnapshotEntriesByRootsAsync(
        long snapshotId,
        int repositoryId,
        IReadOnlyCollection<string> removalRoots,
        CancellationToken ct)
    {
        foreach (var root in removalRoots)
        {
            var normalizedRoot = root.Replace('\\', '/').Trim('/');
            if (string.IsNullOrWhiteSpace(normalizedRoot))
                continue;

            var normalizedRootLower = normalizedRoot.ToLowerInvariant();
            var normalizedPrefixLower = (normalizedRoot + "/").ToLowerInvariant();

            await db.Set<RepositorySnapshotEntry>()
                .Where(entry => entry.RepositoryId == repositoryId && entry.SnapshotId == snapshotId && !entry.IsDeleted)
                .Where(entry =>
                    entry.RelativePath.ToLower() == normalizedRootLower
                    || entry.RelativePath.ToLower().StartsWith(normalizedPrefixLower))
                .ExecuteDeleteAsync(ct);
        }
    }

    private async Task DeleteSnapshotEntriesByExactPathsAsync(
        long snapshotId,
        int repositoryId,
        IReadOnlyCollection<string> exactPaths,
        CancellationToken ct)
    {
        var normalizedPaths = exactPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path.Replace('\\', '/').Trim('/').ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (normalizedPaths.Count == 0)
            return;

        await db.Set<RepositorySnapshotEntry>()
            .Where(entry => entry.RepositoryId == repositoryId && entry.SnapshotId == snapshotId && !entry.IsDeleted)
            .Where(entry => normalizedPaths.Contains(entry.RelativePath.ToLower()))
            .ExecuteDeleteAsync(ct);
    }

    private async Task DeleteSnapshotFileLinksByIdentityIdsAsync(
        long snapshotId,
        IReadOnlyCollection<long> identityIds,
        CancellationToken ct)
    {
        var normalizedIdentityIds = identityIds
            .Where(static id => id > 0)
            .Distinct()
            .ToList();

        if (normalizedIdentityIds.Count == 0)
            return;

        await db.Set<SnapshotFileLink>()
            .Where(link => link.SnapshotId == snapshotId && normalizedIdentityIds.Contains(link.FileIdentityId) && !link.IsDeleted)
            .ExecuteDeleteAsync(ct);
    }

    private async Task UpdateWorkingSnapshotHeaderAsync(
        RepositorySnapshot snapshot,
        Repository repository,
        string trigger,
        DateTime scannedAtUtc,
        CancellationToken ct)
    {
        var totals = await db.Set<RepositorySnapshotEntry>()
            .Where(entry => entry.RepositoryId == repository.Id && entry.SnapshotId == snapshot.Id && !entry.IsDeleted)
            .GroupBy(static _ => 1)
            .Select(group => new
            {
                TotalEntries = group.Count(),
                FileEntries = group.Count(entry => !entry.IsDirectory),
                TotalFileBytes = group.Where(entry => !entry.IsDirectory).Sum(entry => (long?)entry.SizeBytes) ?? 0
            })
            .FirstOrDefaultAsync(ct);

        var totalEntries = totals?.TotalEntries ?? 0;
        var fileEntries = totals?.FileEntries ?? 0;
        var totalFileBytes = totals?.TotalFileBytes ?? 0;

        snapshot.Trigger = trigger;
        snapshot.Title = null;
        snapshot.TagsCsv = null;
        snapshot.CreatedAt = scannedAtUtc;
        snapshot.TotalEntries = totalEntries;
        snapshot.FileEntries = fileEntries;
        snapshot.DirectoryEntries = totalEntries - fileEntries;
        snapshot.TotalFileBytes = totalFileBytes;

        repository.FileCount = fileEntries;
        repository.TotalSizeBytes = totalFileBytes;
        repository.LastScannedAt = scannedAtUtc;
        repository.UpdatedAt = scannedAtUtc;

        await db.SaveChangesAsync(ct);
    }

    private static bool IsPathOrDescendant(string path, string ancestor)
        => string.Equals(path, ancestor, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(ancestor + "/", StringComparison.OrdinalIgnoreCase);

    private static bool HasMeaningfulEntryChanges(
        IReadOnlyDictionary<string, RepositoryScanEntryDto> currentEntriesByPath,
        IReadOnlyDictionary<string, RepositoryScanEntryDto> previousEntriesByPath)
    {
        if (currentEntriesByPath.Count != previousEntriesByPath.Count)
            return true;

        foreach (var entry in currentEntriesByPath)
        {
            if (!previousEntriesByPath.TryGetValue(entry.Key, out var previousEntry))
                return true;

            if (EntriesDifferMeaningfully(previousEntry, entry.Value))
                return true;
        }

        return false;
    }

    private static bool EntriesDifferMeaningfully(RepositoryScanEntryDto previousEntry, RepositoryScanEntryDto currentEntry)
    {
        if (previousEntry.IsDirectory != currentEntry.IsDirectory)
            return true;

        if (!string.Equals(previousEntry.RelativePath, currentEntry.RelativePath, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.Equals(previousEntry.ParentRelativePath, currentEntry.ParentRelativePath, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.Equals(previousEntry.Name, currentEntry.Name, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.Equals(previousEntry.Extension, currentEntry.Extension, StringComparison.OrdinalIgnoreCase))
            return true;

        if (previousEntry.SizeBytes != currentEntry.SizeBytes)
            return true;

        var previousHash = previousEntry.ContentHashSha256 ?? string.Empty;
        var currentHash = currentEntry.ContentHashSha256 ?? string.Empty;
        return !string.Equals(previousHash, currentHash, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<RepositoryScanEntryDto>> GetLatestEntriesAsync(
        int repositoryId,
        CancellationToken ct = default)
    {
        if (repositoryId <= 0)
            return Array.Empty<RepositoryScanEntryDto>();

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
                e.ContentHashSha256,
                db.Set<SnapshotFileLink>()
                    .Where(l => l.SnapshotId == snapshotId
                                && !l.IsDeleted
                                && l.FileIdentity.RepositoryId == repositoryId
                                && l.FileIdentity.RelativePath == e.RelativePath
                                && !l.FileIdentity.IsDeleted
                                && !l.FileVersion.IsDeleted)
                    .Select(l => (DateTime?)l.FileVersion.CreatedAt)
                    .FirstOrDefault()))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<RepositoryScanEntryDto>>> GetLatestEntriesBatchAsync(
        IReadOnlyCollection<int> repositoryIds,
        CancellationToken ct = default)
    {
        var normalizedRepositoryIds = NormalizeRepositoryIds(repositoryIds);
        if (normalizedRepositoryIds.Length == 0)
            return new Dictionary<int, IReadOnlyList<RepositoryScanEntryDto>>();

        var result = normalizedRepositoryIds.ToDictionary(
            repositoryId => repositoryId,
            static _ => (IReadOnlyList<RepositoryScanEntryDto>)Array.Empty<RepositoryScanEntryDto>());

        var shouldCloseConnection = db.Database.GetDbConnection().State != ConnectionState.Open;
        if (shouldCloseConnection)
            await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            var repositoryIdSql = AddRepositoryIdParameters(command, normalizedRepositoryIds);
            command.CommandText = $"""
                WITH ranked_snapshots AS (
                    SELECT
                        s.Id AS SnapshotId,
                        s.RepositoryId,
                        ROW_NUMBER() OVER (PARTITION BY s.RepositoryId ORDER BY s.CreatedAt DESC, s.Id DESC) AS RowNumber
                    FROM RepositorySnapshots AS s
                    WHERE s.IsDeleted = 0
                      AND s.RepositoryId IN ({repositoryIdSql})
                )
                SELECT
                    e.RepositoryId,
                    e.RelativePath,
                    e.ParentRelativePath,
                    e.Name,
                    e.IsDirectory,
                    e.Extension,
                    e.SizeBytes,
                    e.LastWriteUtc,
                    e.ContentHashSha256,
                    fv.CreatedAt AS IndexedAtUtc
                FROM RepositorySnapshotEntries AS e
                INNER JOIN ranked_snapshots AS rs
                    ON rs.RepositoryId = e.RepositoryId
                   AND rs.SnapshotId = e.SnapshotId
                LEFT JOIN FileIdentities AS fi
                    ON fi.RepositoryId = e.RepositoryId
                   AND fi.RelativePath = e.RelativePath
                   AND fi.IsDeleted = 0
                LEFT JOIN SnapshotFileLinks AS l
                    ON l.SnapshotId = e.SnapshotId
                   AND l.FileIdentityId = fi.Id
                   AND l.IsDeleted = 0
                LEFT JOIN FileVersions AS fv
                    ON fv.Id = l.FileVersionId
                   AND fv.IsDeleted = 0
                WHERE rs.RowNumber = 1
                  AND e.IsDeleted = 0
                ORDER BY e.RepositoryId, e.RelativePath;
                """;

            await using var reader = await command.ExecuteReaderAsync(ct);
            var repositoryIdOrdinal = reader.GetOrdinal("RepositoryId");
            var relativePathOrdinal = reader.GetOrdinal("RelativePath");
            var parentRelativePathOrdinal = reader.GetOrdinal("ParentRelativePath");
            var nameOrdinal = reader.GetOrdinal("Name");
            var isDirectoryOrdinal = reader.GetOrdinal("IsDirectory");
            var extensionOrdinal = reader.GetOrdinal("Extension");
            var sizeBytesOrdinal = reader.GetOrdinal("SizeBytes");
            var lastWriteUtcOrdinal = reader.GetOrdinal("LastWriteUtc");
            var contentHashOrdinal = reader.GetOrdinal("ContentHashSha256");
            var indexedAtUtcOrdinal = reader.GetOrdinal("IndexedAtUtc");

            var entriesByRepository = new Dictionary<int, List<RepositoryScanEntryDto>>();
            while (await reader.ReadAsync(ct))
            {
                var repositoryId = reader.GetInt32(repositoryIdOrdinal);
                if (!entriesByRepository.TryGetValue(repositoryId, out var entries))
                {
                    entries = [];
                    entriesByRepository[repositoryId] = entries;
                }

                entries.Add(new RepositoryScanEntryDto(
                    reader.GetString(relativePathOrdinal),
                    ReadNullableString(reader, parentRelativePathOrdinal),
                    reader.GetString(nameOrdinal),
                    ReadBoolean(reader, isDirectoryOrdinal),
                    ReadNullableString(reader, extensionOrdinal),
                    reader.GetInt64(sizeBytesOrdinal),
                    ReadDateTime(reader, lastWriteUtcOrdinal),
                    ReadNullableString(reader, contentHashOrdinal),
                    reader.IsDBNull(indexedAtUtcOrdinal) ? null : ReadDateTime(reader, indexedAtUtcOrdinal)));
            }

            foreach (var pair in entriesByRepository)
                result[pair.Key] = pair.Value;

            return result;
        }
        finally
        {
            if (shouldCloseConnection)
                await db.Database.CloseConnectionAsync();
        }
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
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(i => i.RepositoryId == repositoryId && i.RelativePath == normalizedPath && !i.Repository.IsDeleted)
            .Select(i => new { i.Id, i.RelativePath, i.Extension })
            .FirstOrDefaultAsync(ct);

        if (identity is null)
            return Array.Empty<FileVersionInfoDto>();

        var limit = Math.Clamp(take, 1, 500);

        var versions = await db.Set<FileVersion>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(v => v.FileIdentityId == identity.Id && !v.IsDeleted)
            .OrderByDescending(v => v.CreatedAt)
            .ThenByDescending(v => v.Id)
            .Take(limit)
            .Select(v => new
            {
                v.Id,
                v.CreatedAt,
                v.SizeBytes,
                v.IsDeletionMarker,
                v.ContentHashSha256
            })
            .ToListAsync(ct);

        if (versions.Count == 0)
            return Array.Empty<FileVersionInfoDto>();

        var nonEmptyVersionIds = versions
            .Where(v => v.SizeBytes > 0)
            .Select(v => v.Id)
            .ToList();

        var versionIdsWithBlocks = nonEmptyVersionIds.Count == 0
            ? []
            : await db.Set<FileVersionBlock>()
                .AsNoTracking()
                .Where(b => !b.IsDeleted && nonEmptyVersionIds.Contains(b.FileVersionId))
                .Select(b => b.FileVersionId)
                .Distinct()
                .ToListAsync(ct);

        var blockLookup = versionIdsWithBlocks.ToHashSet();

        return versions
            .Select(v => new FileVersionInfoDto(
                v.Id,
                identity.RelativePath,
                identity.Extension,
                v.CreatedAt,
                v.SizeBytes,
                v.IsDeletionMarker,
                v.ContentHashSha256,
                v.SizeBytes == 0 || blockLookup.Contains(v.Id)))
            .ToList();
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

        var baselineSnapshot = await db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
            .Where(s => db.Set<SnapshotFileLink>().Any(l => l.SnapshotId == s.Id && !l.IsDeleted))
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => new { s.Id, s.CreatedAt })
            .FirstOrDefaultAsync(ct);

        if (baselineSnapshot?.Id == latestSnapshot.Id)
        {
            return new RepositoryPendingChangesDto(
                baselineSnapshot.CreatedAt,
                0,
                0,
                0,
                Array.Empty<RepositoryPendingChangeEntryDto>());
        }

        var currentFilesTask = db.Set<RepositorySnapshotEntry>()
            .Where(e => e.RepositoryId == repositoryId && e.SnapshotId == latestSnapshot.Id && !e.IsDirectory && !e.IsDeleted)
            .Select(e => new SnapshotEntryLight(
                e.RelativePath,
                e.Name,
                e.SizeBytes,
                e.LastWriteUtc,
                e.ContentHashSha256))
            .ToListAsync(ct);

        Task<List<SnapshotEntryLight>> baselineFilesTask = baselineSnapshot is null
            ? Task.FromResult(new List<SnapshotEntryLight>())
            : db.Set<RepositorySnapshotEntry>()
                .Where(e => e.RepositoryId == repositoryId && e.SnapshotId == baselineSnapshot.Id && !e.IsDirectory && !e.IsDeleted)
                .Select(e => new SnapshotEntryLight(
                    e.RelativePath,
                    e.Name,
                    e.SizeBytes,
                    e.LastWriteUtc,
                    e.ContentHashSha256))
                .ToListAsync(ct);

        await Task.WhenAll(currentFilesTask, baselineFilesTask);
        var currentFiles = await currentFilesTask;
        var baselineFiles = await baselineFilesTask;

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
                s.IsArchived,
                s.Trigger,
                s.TagsCsv))
            .ToListAsync(ct);

        if (snapshots.Count == 0)
            return Array.Empty<RepositorySnapshotHistoryItemDto>();

        var snapshotIds = snapshots.Select(s => s.SnapshotId).ToList();

        var linkRows = await db.Set<SnapshotFileLink>()
                        .IgnoreQueryFilters()
            .Where(l => snapshotIds.Contains(l.SnapshotId) && !l.IsDeleted && !l.Snapshot.IsDeleted && !l.FileVersion.IsDeleted && !l.FileIdentity.Repository.IsDeleted)
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

        return await BuildSnapshotHistoryAsync(snapshots, statesBySnapshot, limit, ct);
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<RepositorySnapshotHistoryItemDto>>> GetSnapshotHistoryBatchAsync(
        IReadOnlyCollection<int> repositoryIds,
        int take = 100,
        CancellationToken ct = default)
    {
        var normalizedRepositoryIds = NormalizeRepositoryIds(repositoryIds);
        if (normalizedRepositoryIds.Length == 0)
            return new Dictionary<int, IReadOnlyList<RepositorySnapshotHistoryItemDto>>();

        var limit = Math.Clamp(take, 1, 500);
        var snapshotRowsByRepository = normalizedRepositoryIds.ToDictionary(
            repositoryId => repositoryId,
            static _ => new List<SnapshotLight>());

        var shouldCloseConnection = db.Database.GetDbConnection().State != ConnectionState.Open;
        if (shouldCloseConnection)
            await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            var repositoryIdSql = AddRepositoryIdParameters(command, normalizedRepositoryIds);
            AddCommandParameter(command, "@SnapshotLimit", limit + 1);
            command.CommandText = $"""
                WITH ranked_snapshots AS (
                    SELECT
                        s.Id AS SnapshotId,
                        s.RepositoryId,
                        s.Title,
                        s.CreatedAt,
                        s.IsArchived,
                        s.Trigger,
                        s.TagsCsv,
                        ROW_NUMBER() OVER (PARTITION BY s.RepositoryId ORDER BY s.CreatedAt DESC, s.Id DESC) AS RowNumber
                    FROM RepositorySnapshots AS s
                    WHERE s.IsDeleted = 0
                      AND s.RepositoryId IN ({repositoryIdSql})
                      AND EXISTS (
                          SELECT 1
                          FROM SnapshotFileLinks AS l
                          WHERE l.SnapshotId = s.Id
                            AND l.IsDeleted = 0
                      )
                )
                SELECT
                    SnapshotId,
                    RepositoryId,
                    Title,
                    CreatedAt,
                    IsArchived,
                    Trigger,
                    TagsCsv
                FROM ranked_snapshots
                WHERE RowNumber <= @SnapshotLimit
                ORDER BY RepositoryId, CreatedAt DESC, SnapshotId DESC;
                """;

            await using var reader = await command.ExecuteReaderAsync(ct);
            var snapshotIdOrdinal = reader.GetOrdinal("SnapshotId");
            var repositoryIdOrdinal = reader.GetOrdinal("RepositoryId");
            var titleOrdinal = reader.GetOrdinal("Title");
            var createdAtOrdinal = reader.GetOrdinal("CreatedAt");
            var isArchivedOrdinal = reader.GetOrdinal("IsArchived");
            var triggerOrdinal = reader.GetOrdinal("Trigger");
            var tagsCsvOrdinal = reader.GetOrdinal("TagsCsv");

            while (await reader.ReadAsync(ct))
            {
                var repositoryId = reader.GetInt32(repositoryIdOrdinal);
                if (!snapshotRowsByRepository.TryGetValue(repositoryId, out var snapshots))
                {
                    snapshots = [];
                    snapshotRowsByRepository[repositoryId] = snapshots;
                }

                snapshots.Add(new SnapshotLight(
                    reader.GetInt64(snapshotIdOrdinal),
                    ReadNullableString(reader, titleOrdinal),
                    ReadDateTime(reader, createdAtOrdinal),
                    ReadBoolean(reader, isArchivedOrdinal),
                    reader.GetString(triggerOrdinal),
                    ReadNullableString(reader, tagsCsvOrdinal)));
            }
        }
        finally
        {
            if (shouldCloseConnection)
                await db.Database.CloseConnectionAsync();
        }

        var snapshotIds = snapshotRowsByRepository.Values
            .SelectMany(rows => rows)
            .Select(snapshot => snapshot.SnapshotId)
            .Distinct()
            .ToList();

        var result = normalizedRepositoryIds.ToDictionary(
            repositoryId => repositoryId,
            static _ => (IReadOnlyList<RepositorySnapshotHistoryItemDto>)Array.Empty<RepositorySnapshotHistoryItemDto>());

        if (snapshotIds.Count == 0)
            return result;

        var linkRows = await db.Set<SnapshotFileLink>()
            .IgnoreQueryFilters()
            .Where(l => snapshotIds.Contains(l.SnapshotId) && !l.IsDeleted && !l.Snapshot.IsDeleted && !l.FileVersion.IsDeleted && !l.FileIdentity.Repository.IsDeleted)
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

        foreach (var repositoryId in normalizedRepositoryIds)
        {
            var snapshots = snapshotRowsByRepository.GetValueOrDefault(repositoryId);
            if (snapshots is null || snapshots.Count == 0)
                continue;

            result[repositoryId] = await BuildSnapshotHistoryAsync(snapshots, statesBySnapshot, limit, ct);
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

        var currentSnapshot = await db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId && s.Id == snapshotId && !s.IsDeleted)
            .Where(s => db.Set<SnapshotFileLink>().Any(l => l.SnapshotId == s.Id && !l.IsDeleted))
            .Select(s => new { s.Id, s.CreatedAt })
            .FirstOrDefaultAsync(ct);

        if (currentSnapshot is null)
            return Array.Empty<RepositorySnapshotFileChangeDto>();

        var previousSnapshotId = await db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
            .Where(s => db.Set<SnapshotFileLink>().Any(l => l.SnapshotId == s.Id && !l.IsDeleted))
            .Where(s => s.CreatedAt < currentSnapshot.CreatedAt
                        || (s.CreatedAt == currentSnapshot.CreatedAt && s.Id < currentSnapshot.Id))
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => s.Id)
            .FirstOrDefaultAsync(ct);

        var currentRows = await db.Set<SnapshotFileLink>()
            .IgnoreQueryFilters()
            .Where(l => l.SnapshotId == snapshotId && !l.IsDeleted && !l.Snapshot.IsDeleted && !l.FileVersion.IsDeleted && !l.FileIdentity.Repository.IsDeleted)
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
                .IgnoreQueryFilters()
                .Where(l => l.SnapshotId == previousSnapshotId && !l.IsDeleted && !l.Snapshot.IsDeleted && !l.FileVersion.IsDeleted && !l.FileIdentity.Repository.IsDeleted)
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

        var extension = Path.GetExtension(absolutePath);

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

        var baselineBlocks = baselineVersion.SizeBytes == 0
            ? []
            : await db.Set<FileVersionBlock>()
                .AsNoTracking()
                .Where(b => b.FileVersionId == baselineVersion.Id && !b.IsDeleted)
                .OrderBy(b => b.Sequence)
                .Select(b => new StoredFileBlockDto(
                    b.Sequence,
                    b.BlockStorageKey,
                    b.LengthBytes,
                    b.StoredSizeBytes))
                .ToListAsync(ct);

        if (baselineVersion.SizeBytes > 0 && baselineBlocks.Count == 0)
            return PendingFileDiffPreviewDto.Unavailable(normalizedPath, "Baseline version blocks are missing.");

        var tempFilesToCleanup = new List<string>();

        try
        {
            if (CanBuildTextDiff(extension))
            {
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
                        await contentStore.RestoreFileAsync(baselineBlocks, baselineTemp, true, ct: ct);
                    }

                    var computed = await BuildDiffForPreviewAsync(
                        baselineTemp,
                        absolutePath,
                        extension,
                        normalizedMaxLines,
                        ct);

                    var summary = $"{normalizedPath}   +{computed.AddedLines} / -{computed.RemovedLines}" +
                                  (computed.IsTruncated ? "  (truncated)" : string.Empty);

                    return PendingFileDiffPreviewDto.FromText(
                        relativePath: normalizedPath,
                        message: summary,
                        addedLines: computed.AddedLines,
                        removedLines: computed.RemovedLines,
                        isTruncated: computed.IsTruncated,
                        lines: computed.Lines,
                        hunks: computed.Hunks);
                }
                finally
                {
                    TryDelete(baselineTemp);
                }
            }

            var currentDigest = await ComputeCurrentFileDigestAsync(absolutePath, ManagedPreviewChunkSize, ct);
            var binarySummary = BuildPendingBinarySummary(
                baselineVersion.SizeBytes,
                baselineVersion.ContentHashSha256,
                baselineBlocks,
                currentDigest);

            if (IsArchiveExtension(extension))
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "VeyraFlow", "pending-archive-preview");
                Directory.CreateDirectory(tempDir);

                var baselineExt = GetSafeTempExtension(extension);
                var baselineTempArchive = Path.Combine(tempDir, $"{Guid.NewGuid():N}.baseline{baselineExt}");
                tempFilesToCleanup.Add(baselineTempArchive);

                if (baselineVersion.SizeBytes > 0)
                {
                    await contentStore.RestoreFileAsync(baselineBlocks, baselineTempArchive, true, ct: ct);
                }
                else
                {
                    await File.WriteAllBytesAsync(baselineTempArchive, [], ct);
                }

                var archivePreview = await ArchiveDiffPreviewBuilder.TryBuildZipAsync(baselineTempArchive, absolutePath, ct);
                if (archivePreview is not null)
                {
                    var byteSimilarity = await ComputeByteSimilarityAsync(baselineTempArchive, absolutePath, ct);
                    binarySummary = binarySummary with { ByteSimilarityRatio = byteSimilarity };
                    var archiveMessage = BuildArchivePreviewMessage(normalizedPath, archivePreview);
                    tempFilesToCleanup.Clear();
                    return PendingFileDiffPreviewDto.FromArchive(
                        relativePath: normalizedPath,
                        message: archiveMessage,
                        binarySummary: binarySummary,
                        archivePreview: archivePreview);
                }

                TryDelete(baselineTempArchive);
                tempFilesToCleanup.Remove(baselineTempArchive);
            }

            if (IsImageExtension(extension))
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "VeyraFlow", "pending-image-preview");
                Directory.CreateDirectory(tempDir);

                var baselineExt = string.IsNullOrWhiteSpace(extension) ? ".bin" : extension;
                var baselineTempImage = Path.Combine(tempDir, $"{Guid.NewGuid():N}.baseline{baselineExt}");
                tempFilesToCleanup.Add(baselineTempImage);

                if (baselineVersion.SizeBytes > 0)
                {
                    await contentStore.RestoreFileAsync(baselineBlocks, baselineTempImage, true, ct: ct);
                }
                else
                {
                    await File.WriteAllBytesAsync(baselineTempImage, [], ct);
                }

                var byteSimilarity = await ComputeByteSimilarityAsync(baselineTempImage, absolutePath, ct);

                binarySummary = binarySummary with { ByteSimilarityRatio = byteSimilarity };

                var baselineSize = TryReadImageDimensions(baselineTempImage);
                var currentSize = TryReadImageDimensions(absolutePath);

                var overlay = await ImageDiffOverlayBuilder.TryBuildAsync(baselineTempImage, absolutePath, ct);
                var svgStructuralDiff = string.Equals(extension, ".svg", StringComparison.OrdinalIgnoreCase)
                    ? SvgStructuralDiffAnalyzer.TryAnalyze(baselineTempImage, absolutePath)
                    : null;
                var imagePreview = new PendingImageDiffPreviewDto
                {
                    BaselineImagePath = baselineTempImage,
                    IsBaselineTempFile = true,
                    CurrentImagePath = absolutePath,
                    IsCurrentTempFile = false,
                    OverlayImagePath = overlay?.OverlayImagePath,
                    IsOverlayTempFile = overlay?.IsOverlayTempFile ?? false,
                    BaselineWidth = baselineSize?.Width,
                    BaselineHeight = baselineSize?.Height,
                    CurrentWidth = currentSize?.Width,
                    CurrentHeight = currentSize?.Height,
                    HasDimensionMismatch = baselineSize.HasValue
                                           && currentSize.HasValue
                                           && (baselineSize.Value.Width != currentSize.Value.Width
                                               || baselineSize.Value.Height != currentSize.Value.Height),
                    SimilarityRatio = byteSimilarity,
                    ChangedPixelCount = overlay?.ChangedPixelCount ?? 0,
                    ChangedPixelRatio = overlay?.ChangedPixelRatio,
                    ChangedRegionCount = overlay?.ChangedRegionCount ?? 0,
                    IsVectorImage = svgStructuralDiff is not null,
                    AddedElementCount = svgStructuralDiff?.AddedElementCount ?? 0,
                    RemovedElementCount = svgStructuralDiff?.RemovedElementCount ?? 0,
                    ModifiedElementCount = svgStructuralDiff?.ModifiedElementCount ?? 0,
                    ChangedAttributeCount = svgStructuralDiff?.ChangedAttributeCount ?? 0
                };

                var imageMessage = BuildBinaryPreviewMessage(normalizedPath, binarySummary, "image", extension);
                tempFilesToCleanup.Clear();
                return PendingFileDiffPreviewDto.FromImage(
                    relativePath: normalizedPath,
                    message: imageMessage,
                    binarySummary: binarySummary,
                    imagePreview: imagePreview);
            }

            if (IsAudioExtension(extension))
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "VeyraFlow", "pending-audio-preview");
                Directory.CreateDirectory(tempDir);

                var baselineExt = GetSafeTempExtension(extension);
                var baselineTempAudio = Path.Combine(tempDir, $"{Guid.NewGuid():N}.baseline{baselineExt}");
                tempFilesToCleanup.Add(baselineTempAudio);

                if (baselineVersion.SizeBytes > 0)
                {
                    await contentStore.RestoreFileAsync(baselineBlocks, baselineTempAudio, true, ct: ct);
                }
                else
                {
                    await File.WriteAllBytesAsync(baselineTempAudio, [], ct);
                }

                var byteSimilarity = await ComputeByteSimilarityAsync(baselineTempAudio, absolutePath, ct);
                binarySummary = binarySummary with { ByteSimilarityRatio = byteSimilarity };

                var audioPreview = await AudioDiffPreviewBuilder.TryBuildAsync(baselineTempAudio, absolutePath, ct);
                if (audioPreview is not null)
                {
                    tempFilesToCleanup.Add(audioPreview.DifferenceAudioPath);
                    tempFilesToCleanup.Add(audioPreview.WaveformImagePath);
                    tempFilesToCleanup.Add(audioPreview.SpectrogramImagePath);
                    tempFilesToCleanup.Add(audioPreview.SpectralDeltaImagePath);

                    var audioMessage = BuildAudioPreviewMessage(normalizedPath, binarySummary, audioPreview);
                    tempFilesToCleanup.Clear();
                    return PendingFileDiffPreviewDto.FromAudio(
                        relativePath: normalizedPath,
                        message: audioMessage,
                        binarySummary: binarySummary,
                        audioPreview: CreatePendingAudioPreviewDto(
                            baselineTempAudio,
                            true,
                            absolutePath,
                            false,
                            audioPreview));
                }

                TryDelete(baselineTempAudio);
                tempFilesToCleanup.Remove(baselineTempAudio);
            }

            var binaryMessage = BuildBinaryPreviewMessage(normalizedPath, binarySummary, "binary", extension);
            return PendingFileDiffPreviewDto.FromBinary(
                relativePath: normalizedPath,
                message: binaryMessage,
                binarySummary: binarySummary);
        }
        catch (Exception ex)
        {
            foreach (var tempFile in tempFilesToCleanup)
                TryDelete(tempFile);

            log.LogWarning(
                ex,
                "Failed to build pending file diff preview. RepositoryId {RepositoryId}. Path {Path}",
                repositoryId,
                normalizedPath);

            return PendingFileDiffPreviewDto.Unavailable(normalizedPath, "Unable to build diff preview for selected file.");
        }
    }
    public async Task<PendingFileDiffPreviewDto> GetFileVersionDiffPreviewAsync(
        long leftFileVersionId,
        long rightFileVersionId,
        int maxLines = 3000,
        CancellationToken ct = default)
    {
        if (leftFileVersionId <= 0 || rightFileVersionId <= 0)
            return PendingFileDiffPreviewDto.Unavailable(string.Empty, "Both versions must be selected.");

        if (leftFileVersionId == rightFileVersionId)
            return PendingFileDiffPreviewDto.Unavailable(string.Empty, "Select two different versions.");

        var normalizedMaxLines = NormalizeMaxLines(maxLines);

        var left = await GetFileVersionRestoreDataAsync(leftFileVersionId, ct);
        var right = await GetFileVersionRestoreDataAsync(rightFileVersionId, ct);

        if (left is null || right is null)
            return PendingFileDiffPreviewDto.Unavailable(string.Empty, "One of the selected versions was not found.");

        if (!left.RelativePath.Equals(right.RelativePath, StringComparison.OrdinalIgnoreCase))
            return PendingFileDiffPreviewDto.Unavailable(left.RelativePath, "Selected versions belong to different files.");

        if (left.IsDeletionMarker || right.IsDeletionMarker)
            return PendingFileDiffPreviewDto.Unavailable(left.RelativePath, "Diff preview is unavailable for deletion versions.");

        var extension = NormalizeExtension(left.Extension) ?? NormalizeExtension(right.Extension) ?? NormalizeExtension(Path.GetExtension(left.RelativePath));
        var safeExtension = GetSafeTempExtension(extension);

        var tempDir = Path.Combine(Path.GetTempPath(), "VeyraFlow", "version-diff-preview");
        Directory.CreateDirectory(tempDir);

        var leftTemp = Path.Combine(tempDir, $"{Guid.NewGuid():N}.left{safeExtension}");
        var rightTemp = Path.Combine(tempDir, $"{Guid.NewGuid():N}.right{safeExtension}");

        var keepLeftTemp = false;
        var keepRightTemp = false;
        string? audioDifferenceTemp = null;
        string? audioWaveformTemp = null;
        string? audioSpectrogramTemp = null;
        string? audioSpectralDeltaTemp = null;

        try
        {
            await Task.WhenAll(
                RestoreVersionToTempAsync(left, leftTemp, ct),
                RestoreVersionToTempAsync(right, rightTemp, ct));

            if (CanBuildTextDiff(extension))
            {
                var cached = await GetStoredTextDiffAsync(left.FileVersionId, right.FileVersionId, normalizedMaxLines, ct);
                TextDiffResultDto diff;

                if (cached is not null)
                {
                    diff = cached;
                }
                else
                {
                    var computed = await BuildDiffForPreviewAsync(leftTemp, rightTemp, extension, normalizedMaxLines, ct);
                    diff = new TextDiffResultDto(
                        left.RelativePath,
                        left.FileVersionId,
                        right.FileVersionId,
                        computed.AddedLines,
                        computed.RemovedLines,
                        computed.IsTruncated,
                        computed.Lines,
                        computed.Hunks);

                    try
                    {
                        await SaveStoredTextDiffAsync(diff, normalizedMaxLines, ct);
                    }
                    catch (Exception cacheEx)
                    {
                        log.LogWarning(
                            cacheEx,
                            "Failed to persist text diff cache. LeftVersion {LeftVersion}. RightVersion {RightVersion}",
                            left.FileVersionId,
                            right.FileVersionId);
                    }
                }

                var textMessage = $"{left.RelativePath}   +{diff.AddedLines} / -{diff.RemovedLines}" +
                                  (diff.IsTruncated ? "  (truncated)" : string.Empty);

                return PendingFileDiffPreviewDto.FromText(
                    relativePath: left.RelativePath,
                    message: textMessage,
                    addedLines: diff.AddedLines,
                    removedLines: diff.RemovedLines,
                    isTruncated: diff.IsTruncated,
                    lines: diff.Lines,
                        hunks: diff.Hunks);
            }

            var byteSimilarity = await ComputeByteSimilarityAsync(leftTemp, rightTemp, ct);

            var binarySummary = BuildVersionBinarySummary(left, right)
                with { ByteSimilarityRatio = byteSimilarity };

            if (IsArchiveExtension(extension))
            {
                var archivePreview = await ArchiveDiffPreviewBuilder.TryBuildZipAsync(leftTemp, rightTemp, ct);
                if (archivePreview is not null)
                {
                    return PendingFileDiffPreviewDto.FromArchive(
                        relativePath: left.RelativePath,
                        message: BuildArchivePreviewMessage(left.RelativePath, archivePreview),
                        binarySummary: binarySummary,
                        archivePreview: archivePreview);
                }
            }

            if (IsImageExtension(extension))
            {
                var baselineSize = TryReadImageDimensions(leftTemp);
                var currentSize = TryReadImageDimensions(rightTemp);

                var overlay = await ImageDiffOverlayBuilder.TryBuildAsync(leftTemp, rightTemp, ct);
                var svgStructuralDiff = string.Equals(extension, ".svg", StringComparison.OrdinalIgnoreCase)
                    ? SvgStructuralDiffAnalyzer.TryAnalyze(leftTemp, rightTemp)
                    : null;
                var imagePreview = new PendingImageDiffPreviewDto
                {
                    BaselineImagePath = leftTemp,
                    IsBaselineTempFile = true,
                    CurrentImagePath = rightTemp,
                    IsCurrentTempFile = true,
                    OverlayImagePath = overlay?.OverlayImagePath,
                    IsOverlayTempFile = overlay?.IsOverlayTempFile ?? false,
                    BaselineWidth = baselineSize?.Width,
                    BaselineHeight = baselineSize?.Height,
                    CurrentWidth = currentSize?.Width,
                    CurrentHeight = currentSize?.Height,
                    HasDimensionMismatch = baselineSize.HasValue
                                           && currentSize.HasValue
                                           && (baselineSize.Value.Width != currentSize.Value.Width
                                               || baselineSize.Value.Height != currentSize.Value.Height),
                    SimilarityRatio = byteSimilarity,
                    ChangedPixelCount = overlay?.ChangedPixelCount ?? 0,
                    ChangedPixelRatio = overlay?.ChangedPixelRatio,
                    ChangedRegionCount = overlay?.ChangedRegionCount ?? 0,
                    IsVectorImage = svgStructuralDiff is not null,
                    AddedElementCount = svgStructuralDiff?.AddedElementCount ?? 0,
                    RemovedElementCount = svgStructuralDiff?.RemovedElementCount ?? 0,
                    ModifiedElementCount = svgStructuralDiff?.ModifiedElementCount ?? 0,
                    ChangedAttributeCount = svgStructuralDiff?.ChangedAttributeCount ?? 0
                };

                var imageMessage = BuildBinaryPreviewMessage(left.RelativePath, binarySummary, "image", extension);
                keepLeftTemp = true;
                keepRightTemp = true;

                return PendingFileDiffPreviewDto.FromImage(
                    relativePath: left.RelativePath,
                    message: imageMessage,
                    binarySummary: binarySummary,
                    imagePreview: imagePreview);
            }

            if (IsAudioExtension(extension))
            {
                var audioPreview = await AudioDiffPreviewBuilder.TryBuildAsync(leftTemp, rightTemp, ct);
                if (audioPreview is not null)
                {
                    audioDifferenceTemp = audioPreview.DifferenceAudioPath;
                    audioWaveformTemp = audioPreview.WaveformImagePath;
                    audioSpectrogramTemp = audioPreview.SpectrogramImagePath;
                    audioSpectralDeltaTemp = audioPreview.SpectralDeltaImagePath;
                    keepLeftTemp = true;
                    keepRightTemp = true;

                    return PendingFileDiffPreviewDto.FromAudio(
                        relativePath: left.RelativePath,
                        message: BuildAudioPreviewMessage(left.RelativePath, binarySummary, audioPreview),
                        binarySummary: binarySummary,
                        audioPreview: CreatePendingAudioPreviewDto(
                            leftTemp,
                            true,
                            rightTemp,
                            true,
                            audioPreview));
                }
            }

            var binaryMessage = BuildBinaryPreviewMessage(left.RelativePath, binarySummary, "binary", extension);
            return PendingFileDiffPreviewDto.FromBinary(
                relativePath: left.RelativePath,
                message: binaryMessage,
                binarySummary: binarySummary);
        }
        catch (Exception ex)
        {
            log.LogWarning(
                ex,
                "Failed to build file-version diff preview. LeftVersion {LeftVersion}. RightVersion {RightVersion}. Path {Path}",
                leftFileVersionId,
                rightFileVersionId,
                left.RelativePath);

            return PendingFileDiffPreviewDto.Unavailable(left.RelativePath, FormatVersionPreviewError(ex));
        }
        finally
        {
            if (!keepLeftTemp)
                TryDelete(leftTemp);

            if (!keepRightTemp)
                TryDelete(rightTemp);

            if (!keepLeftTemp && !keepRightTemp && !string.IsNullOrWhiteSpace(audioWaveformTemp))
                TryDelete(audioWaveformTemp);

            if (!keepLeftTemp && !keepRightTemp && !string.IsNullOrWhiteSpace(audioDifferenceTemp))
                TryDelete(audioDifferenceTemp);

            if (!keepLeftTemp && !keepRightTemp && !string.IsNullOrWhiteSpace(audioSpectrogramTemp))
                TryDelete(audioSpectrogramTemp);

            if (!keepLeftTemp && !keepRightTemp && !string.IsNullOrWhiteSpace(audioSpectralDeltaTemp))
                TryDelete(audioSpectralDeltaTemp);
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

        if (row.StorageFormatVersion != CurrentTextDiffStorageFormatVersion)
        {
            await InvalidateTextDiffCacheRowAsync(row.Id, ct);
            return null;
        }

        List<TextDiffLineDto>? lineRows = null;
        if (!string.IsNullOrWhiteSpace(row.LinesJson))
        {
            try
            {
                lineRows = JsonSerializer.Deserialize<List<TextDiffLineDto>>(row.LinesJson, DiffJsonOptions);
            }
            catch (JsonException)
            {
                lineRows = null;
            }
        }

        if (lineRows is null)
        {
            lineRows = await db.Set<FileVersionTextDiffLine>()
                .AsNoTracking()
                .Where(l => l.DiffId == row.Id && !l.IsDeleted)
                .OrderBy(l => l.Sequence)
                .Select(l => new TextDiffLineDto(
                    l.Kind,
                    l.LeftLineNumber,
                    l.RightLineNumber,
                    l.TextLineAtom.Text))
                .ToListAsync(ct);
        }

        if (lineRows.Count == 0)
        {
            if (row.AddedLines == 0 && row.RemovedLines == 0)
            {
                return new TextDiffResultDto(
                    row.RelativePath,
                    row.LeftFileVersionId,
                    row.RightFileVersionId,
                    row.AddedLines,
                    row.RemovedLines,
                    row.IsTruncated,
                    lineRows,
                    Array.Empty<TextDiffHunkDto>());
            }

            await InvalidateTextDiffCacheRowAsync(row.Id, ct);
            return null;
        }

        var hunkRows = await db.Set<FileVersionTextDiffHunk>()
            .AsNoTracking()
            .Where(h => h.DiffId == row.Id && !h.IsDeleted)
            .OrderBy(h => h.Sequence)
            .Select(h => new TextDiffHunkDto(
                h.Sequence,
                h.StartLineSequence,
                h.EndLineSequence,
                h.OldStartLine,
                h.OldLineCount,
                h.NewStartLine,
                h.NewLineCount,
                h.ChangeKind))
            .ToListAsync(ct);

        if (hunkRows.Count == 0)
        {
            if (row.AddedLines == 0 && row.RemovedLines == 0)
            {
                return new TextDiffResultDto(
                    row.RelativePath,
                    row.LeftFileVersionId,
                    row.RightFileVersionId,
                    row.AddedLines,
                    row.RemovedLines,
                    row.IsTruncated,
                    lineRows,
                    Array.Empty<TextDiffHunkDto>());
            }

            await InvalidateTextDiffCacheRowAsync(row.Id, ct);
            return null;
        }

        return new TextDiffResultDto(
            row.RelativePath,
            row.LeftFileVersionId,
            row.RightFileVersionId,
            row.AddedLines,
            row.RemovedLines,
            row.IsTruncated,
            lineRows,
            hunkRows);
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
        var saveLock = GetTextDiffSaveStripe(diff.LeftFileVersionId, diff.RightFileVersionId, normalizedMaxLines);
        await saveLock.WaitAsync(ct);

        try
        {
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
                .IgnoreQueryFilters()
                .Include(d => d.Hunks)
                .Include(d => d.Lines)
                .AsSplitQuery()
                .FirstOrDefaultAsync(d => d.LeftFileVersionId == diff.LeftFileVersionId
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
            existing.StorageFormatVersion = CurrentTextDiffStorageFormatVersion;
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
                        StartLineSequence = h.StartLineSequence,
                        EndLineSequence = h.EndLineSequence,
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

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsTextDiffUniqueConflict(ex))
            {
                db.ChangeTracker.Clear();
            }
        }
        finally
        {
            saveLock.Release();
        }
    }

    public async Task<FileVersionRestoreDto?> GetFileVersionRestoreDataAsync(
        long fileVersionId,
        CancellationToken ct = default)
    {
        var version = await db.Set<FileVersion>()
            .IgnoreQueryFilters()
            .Include(v => v.FileIdentity)
            .ThenInclude(i => i.Repository)
            .FirstOrDefaultAsync(v => v.Id == fileVersionId && !v.IsDeleted && !v.FileIdentity.Repository.IsDeleted, ct);

        if (version is null)
            return null;

        var blocks = await db.Set<FileVersionBlock>()
            .IgnoreQueryFilters()
            .Where(b => b.FileVersionId == fileVersionId && !b.IsDeleted)
            .OrderBy(b => b.Sequence)
            .Select(b => new StoredFileBlockDto(
                b.Sequence,
                b.BlockStorageKey,
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
            version.LastWriteUtc,
            blocks);
    }

    public async Task<RepositorySnapshotRestoreDataDto?> GetSnapshotRestoreDataAsync(
        int repositoryId,
        long snapshotId,
        CancellationToken ct = default)
    {
        var snapshot = await db.Set<RepositorySnapshot>()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == snapshotId && s.RepositoryId == repositoryId && !s.IsDeleted, ct);

        if (snapshot is null)
            return null;

        var entries = await db.Set<RepositorySnapshotEntry>()
            .AsNoTracking()
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
                e.ContentHashSha256,
                null))
            .ToListAsync(ct);

        var linkedVersions = await db.Set<SnapshotFileLink>()
            .AsNoTracking()
            .Where(l => l.SnapshotId == snapshotId
                        && !l.IsDeleted
                        && l.FileIdentity.RepositoryId == repositoryId
                        && !l.FileIdentity.IsDeleted
                        && !l.FileVersion.IsDeleted)
            .Select(l => new
            {
                l.FileVersion.Id,
                l.FileIdentity.RepositoryId,
                l.FileIdentity.RelativePath,
                l.FileIdentity.Extension,
                l.FileVersion.SizeBytes,
                l.FileVersion.IsDeletionMarker,
                l.FileVersion.ContentHashSha256,
                l.FileVersion.LastWriteUtc
            })
            .ToListAsync(ct);

        var versionIds = linkedVersions
            .Select(v => v.Id)
            .Distinct()
            .ToList();

        var blocksByVersion = versionIds.Count == 0
            ? new Dictionary<long, List<StoredFileBlockDto>>()
            : (await db.Set<FileVersionBlock>()
                    .AsNoTracking()
                    .Where(b => versionIds.Contains(b.FileVersionId) && !b.IsDeleted)
                    .OrderBy(b => b.Sequence)
                    .Select(b => new
                    {
                        b.FileVersionId,
                        Block = new StoredFileBlockDto(
                            b.Sequence,
                            b.BlockStorageKey,
                            b.LengthBytes,
                            b.StoredSizeBytes)
                    })
                    .ToListAsync(ct))
                .GroupBy(x => x.FileVersionId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => x.Block).ToList());

        var versions = linkedVersions
            .OrderBy(v => v.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(v => new FileVersionRestoreDto(
                v.Id,
                v.RepositoryId,
                v.RelativePath,
                v.Extension,
                v.SizeBytes,
                v.IsDeletionMarker,
                v.ContentHashSha256,
                v.LastWriteUtc,
                blocksByVersion.GetValueOrDefault(v.Id) ?? []))
            .ToList();

        var item = new RepositorySnapshotHistoryItemDto(
            snapshot.Id,
            snapshot.Title,
            snapshot.CreatedAt,
            RepositorySnapshotTriggerClassifier.GetKind(snapshot.Trigger),
            snapshot.IsArchived,
            snapshot.Trigger,
            0,
            ParseSnapshotTags(snapshot.TagsCsv));

        return new RepositorySnapshotRestoreDataDto(item, entries, versions);
    }

    private static bool IsTextDiffUniqueConflict(DbUpdateException ex)
        => ex.InnerException?.Message.Contains(
               "UNIQUE constraint failed: FileVersionTextDiffs.LeftFileVersionId, FileVersionTextDiffs.RightFileVersionId, FileVersionTextDiffs.MaxLines",
               StringComparison.OrdinalIgnoreCase) == true;


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

                if (!CanBuildTextDiff(right.Extension))
                    continue;

                var tempDir = Path.Combine(Path.GetTempPath(), "VeyraFlow", "diff-precompute");
                Directory.CreateDirectory(tempDir);

                var leftTemp = Path.Combine(tempDir, $"{Guid.NewGuid():N}.left.tmp");
                var rightTemp = Path.Combine(tempDir, $"{Guid.NewGuid():N}.right.tmp");

                try
                {
                    await contentStore.RestoreFileAsync(left.Blocks, leftTemp, true, ct: ct);
                    await contentStore.RestoreFileAsync(right.Blocks, rightTemp, true, ct: ct);

                    var extension = NormalizeExtension(right.Extension) ?? NormalizeExtension(left.Extension);
                    var computed = await BuildDiffForPreviewAsync(leftTemp, rightTemp, extension, PrecomputedDiffMaxLines, ct);

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

    private static bool CanBuildTextDiff(string? extension)
    {
        return KnownFileExtensions.IsTextDiffExtension(extension) || WordSemanticProjection.IsWordOoxmlExtension(extension);
    }

    private static bool IsTextExtension(string? extension)
        => KnownFileExtensions.IsTextDiffExtension(extension);

    private static bool IsArchiveExtension(string? extension)
        => KnownFileExtensions.IsArchiveDiffExtension(extension);

    private static bool IsImageExtension(string? extension)
        => KnownFileExtensions.IsImageDiffExtension(extension);

    private static bool IsAudioExtension(string? extension)
        => KnownFileExtensions.IsAudioDiffExtension(extension);

    private static bool IsOfficeDocumentExtension(string? extension)
        => KnownFileExtensions.IsOfficeBinaryHintExtension(extension);

    private static string? NormalizeExtension(string? extension)
        => KnownFileExtensions.NormalizeExtension(extension);

    private static SemaphoreSlim GetTextDiffSaveStripe(long leftFileVersionId, long rightFileVersionId, int maxLines)
    {
        var hash = HashCode.Combine(leftFileVersionId, rightFileVersionId, maxLines);
        var index = (hash & int.MaxValue) % TextDiffSaveStripes.Length;
        return TextDiffSaveStripes[index];
    }

    private static string GetSafeTempExtension(string? extension)
    {
        var normalized = NormalizeExtension(extension);
        if (string.IsNullOrWhiteSpace(normalized))
            return ".tmp";

        if (normalized.Length > 10)
            return ".tmp";

        return normalized;
    }

    private async Task RestoreVersionToTempAsync(
        FileVersionRestoreDto version,
        string tempPath,
        CancellationToken ct)
    {
        if (version.SizeBytes <= 0)
        {
            await File.WriteAllBytesAsync(tempPath, [], ct);
            return;
        }

        if (version.Blocks.Count == 0)
            throw new InvalidOperationException("Blocks are missing for selected version.");

        await contentStore.RestoreFileAsync(version.Blocks, tempPath, true, ct: ct);
    }

    private async Task<TextDiffComputationDto> BuildDiffForPreviewAsync(
        string leftPath,
        string rightPath,
        string? extension,
        int maxLines,
        CancellationToken ct)
    {
        if (!WordSemanticProjection.IsWordOoxmlExtension(extension))
            return await diffEngine.BuildDiffAsync(leftPath, rightPath, maxLines, ct);

        var semanticTempDir = Path.Combine(Path.GetTempPath(), "VeyraFlow", "word-diff-preview");
        Directory.CreateDirectory(semanticTempDir);

        var leftSemanticTemp = Path.Combine(semanticTempDir, $"{Guid.NewGuid():N}.left.txt");
        var rightSemanticTemp = Path.Combine(semanticTempDir, $"{Guid.NewGuid():N}.right.txt");

        try
        {
            var leftSemanticLines = WordSemanticProjection.ExtractSemanticLines(leftPath);
            var rightSemanticLines = WordSemanticProjection.ExtractSemanticLines(rightPath);

            await File.WriteAllLinesAsync(leftSemanticTemp, leftSemanticLines, Encoding.UTF8, ct);
            await File.WriteAllLinesAsync(rightSemanticTemp, rightSemanticLines, Encoding.UTF8, ct);

            return await diffEngine.BuildDiffAsync(leftSemanticTemp, rightSemanticTemp, maxLines, ct);
        }
        finally
        {
            TryDelete(leftSemanticTemp);
            TryDelete(rightSemanticTemp);
        }
    }
    private static string FormatVersionPreviewError(Exception ex)
    {
        var text = ex.ToString();

        if (text.Contains("native block format", StringComparison.OrdinalIgnoreCase)
            || text.Contains("native block hash", StringComparison.OrdinalIgnoreCase))
        {
            return "Preview is unavailable right now: selected versions use native block format, but native restore entrypoints are unavailable. Rebuild/update veyra_core and run Reindex data.";
        }

        if (text.Contains("Block file not found", StringComparison.OrdinalIgnoreCase)
            || text.Contains("blocks are missing", StringComparison.OrdinalIgnoreCase)
            || text.Contains("missing", StringComparison.OrdinalIgnoreCase))
        {
            return "Preview is unavailable because some block files are missing. Run Repair data or Reindex data.";
        }

        if (text.Contains("decryption", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Artifact key", StringComparison.OrdinalIgnoreCase))
        {
            return "Preview is unavailable because encrypted blocks cannot be decrypted with current keys.";
        }

        return ex.Message;
    }

    private async Task InvalidateTextDiffCacheRowAsync(long diffId, CancellationToken ct)
    {
        if (diffId <= 0)
            return;

        var deletedAt = DateTime.UtcNow;

        try
        {
            await db.Set<FileVersionTextDiffLine>()
                .Where(l => l.DiffId == diffId && !l.IsDeleted)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(l => l.IsDeleted, _ => true)
                    .SetProperty(l => l.DeletedAt, _ => deletedAt), ct);

            await db.Set<FileVersionTextDiffHunk>()
                .Where(h => h.DiffId == diffId && !h.IsDeleted)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(h => h.IsDeleted, _ => true)
                    .SetProperty(h => h.DeletedAt, _ => deletedAt), ct);

            await db.Set<FileVersionTextDiff>()
                .Where(d => d.Id == diffId && !d.IsDeleted)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(d => d.IsDeleted, _ => true)
                    .SetProperty(d => d.DeletedAt, _ => deletedAt)
                    .SetProperty(d => d.UpdatedAt, _ => deletedAt), ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to invalidate stale text diff cache row {DiffId}", diffId);
        }
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

    private static PendingBinaryDiffSummaryDto BuildPendingBinarySummary(
        long baselineSizeBytes,
        string? baselineHashSha256,
        IReadOnlyList<StoredFileBlockDto> baselineBlocks,
        CurrentFileDigest currentDigest)
    {
        var baselineFamily = GetDominantHashFamily(baselineBlocks.Select(b => b.BlockStorageKey));
        var (sharedBlockCount, dedupRatio, changedBlockRatio) = ComputeBlockOverlapMetrics(
            baselineBlocks.Select(b => b.BlockStorageKey),
            currentDigest.ChunkHashes,
            baselineFamily,
            currentDigest.HashFamily);

        return new PendingBinaryDiffSummaryDto(
            BaselineSizeBytes: baselineSizeBytes,
            CurrentSizeBytes: currentDigest.SizeBytes,
            SizeDeltaBytes: currentDigest.SizeBytes - baselineSizeBytes,
            BaselineHashSha256: baselineHashSha256 ?? string.Empty,
            CurrentHashSha256: currentDigest.Sha256,
            ChunkSizeBytes: ManagedPreviewChunkSize,
            BaselineBlockCount: baselineBlocks.Count,
            CurrentBlockCount: currentDigest.ChunkHashes.Count,
            SharedBlockCount: sharedBlockCount,
            DedupRatio: dedupRatio,
            ChangedBlockRatio: changedBlockRatio,
            ByteSimilarityRatio: null);
    }

    private static PendingBinaryDiffSummaryDto BuildVersionBinarySummary(
        FileVersionRestoreDto baselineVersion,
        FileVersionRestoreDto currentVersion)
    {
        var baselineFamily = GetDominantHashFamily(baselineVersion.Blocks.Select(b => b.BlockStorageKey));
        var currentFamily = GetDominantHashFamily(currentVersion.Blocks.Select(b => b.BlockStorageKey));

        var (sharedBlockCount, dedupRatio, changedBlockRatio) = ComputeBlockOverlapMetrics(
            baselineVersion.Blocks.Select(b => b.BlockStorageKey),
            currentVersion.Blocks.Select(b => b.BlockStorageKey),
            baselineFamily,
            currentFamily);

        return new PendingBinaryDiffSummaryDto(
            BaselineSizeBytes: baselineVersion.SizeBytes,
            CurrentSizeBytes: currentVersion.SizeBytes,
            SizeDeltaBytes: currentVersion.SizeBytes - baselineVersion.SizeBytes,
            BaselineHashSha256: baselineVersion.ContentHashSha256,
            CurrentHashSha256: currentVersion.ContentHashSha256,
            ChunkSizeBytes: ManagedPreviewChunkSize,
            BaselineBlockCount: baselineVersion.Blocks.Count,
            CurrentBlockCount: currentVersion.Blocks.Count,
            SharedBlockCount: sharedBlockCount,
            DedupRatio: dedupRatio,
            ChangedBlockRatio: changedBlockRatio,
            ByteSimilarityRatio: null);
    }

    private static (int SharedBlockCount, double? DedupRatio, double? ChangedBlockRatio) ComputeBlockOverlapMetrics(
        IEnumerable<string?> baselineHashes,
        IEnumerable<string?> currentHashes,
        BlockHashFamily baselineFamily,
        BlockHashFamily currentFamily)
    {
        var baselineList = baselineHashes
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h!)
            .ToList();

        var currentList = currentHashes
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h!)
            .ToList();

        var baselineBlockCount = baselineList.Count;
        var currentBlockCount = currentList.Count;

        if (currentBlockCount == 0)
        {
            var dedup = baselineBlockCount == 0 ? 1d : 0d;
            return (0, dedup, 1d - dedup);
        }

        if (baselineBlockCount == 0)
            return (0, 0d, 1d);

        if (!CanCompareHashFamilies(baselineFamily, currentFamily))
            return (0, null, null);

        var normalizedBaseline = baselineList
            .Select(h => NormalizeBlockHashForComparison(h, baselineFamily))
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var normalizedCurrent = currentList
            .Select(h => NormalizeBlockHashForComparison(h, currentFamily))
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h!)
            .ToList();

        if (normalizedBaseline.Count == 0 || normalizedCurrent.Count == 0)
            return (0, null, null);

        var sharedBlockCount = 0;
        foreach (var hash in normalizedCurrent)
        {
            if (normalizedBaseline.Contains(hash))
                sharedBlockCount++;
        }

        var dedupRatio = (double)sharedBlockCount / normalizedCurrent.Count;
        return (sharedBlockCount, dedupRatio, 1d - dedupRatio);
    }

    private static bool CanCompareHashFamilies(BlockHashFamily baselineFamily, BlockHashFamily currentFamily)
    {
        if (baselineFamily == BlockHashFamily.Unknown || currentFamily == BlockHashFamily.Unknown)
            return false;

        return baselineFamily == currentFamily;
    }

    private static BlockHashFamily GetDominantHashFamily(IEnumerable<string?> hashes)
    {
        var sha256Count = 0;
        var nativeCount = 0;

        foreach (var hash in hashes)
        {
            switch (DetectHashFamily(hash))
            {
                case BlockHashFamily.Sha256:
                    sha256Count++;
                    break;
                case BlockHashFamily.Native:
                    nativeCount++;
                    break;
            }
        }

        if (sha256Count > 0 && nativeCount == 0)
            return BlockHashFamily.Sha256;

        if (nativeCount > 0 && sha256Count == 0)
            return BlockHashFamily.Native;

        return BlockHashFamily.Unknown;
    }

    private static BlockHashFamily DetectHashFamily(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
            return BlockHashFamily.Unknown;

        var normalized = hash.Trim();

        if (normalized.StartsWith("sha256-", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("msha256:", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            return BlockHashFamily.Sha256;
        }

        if (normalized.StartsWith("blake3:", StringComparison.OrdinalIgnoreCase) || IsHexHash64(normalized))
            return BlockHashFamily.Native;

        return BlockHashFamily.Unknown;
    }

    private static string? NormalizeBlockHashForComparison(string? hash, BlockHashFamily family)
    {
        if (string.IsNullOrWhiteSpace(hash))
            return null;

        var normalized = hash.Trim();

        if (family is BlockHashFamily.Sha256 or BlockHashFamily.Unknown)
        {
            if (normalized.StartsWith("sha256-", StringComparison.OrdinalIgnoreCase))
                normalized = normalized["sha256-".Length..];
            else if (normalized.StartsWith("msha256:", StringComparison.OrdinalIgnoreCase))
                normalized = normalized["msha256:".Length..];
            else if (normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                normalized = normalized["sha256:".Length..];
        }

        if (family is BlockHashFamily.Native or BlockHashFamily.Unknown)
        {
            if (normalized.StartsWith("blake3:", StringComparison.OrdinalIgnoreCase))
                normalized = normalized["blake3:".Length..];
        }

        normalized = normalized.Trim();
        return IsHexHash64(normalized) ? normalized.ToLowerInvariant() : null;
    }

    private static bool IsHexHash64(string value)
    {
        if (value.Length != 64)
            return false;

        foreach (var ch in value)
        {
            var isHex = (ch >= '0' && ch <= '9')
                        || (ch >= 'a' && ch <= 'f')
                        || (ch >= 'A' && ch <= 'F');

            if (!isHex)
                return false;
        }

        return true;
    }

    private static async Task<CurrentFileDigest> ComputeCurrentFileDigestAsync(
        string absolutePath,
        int chunkSize,
        CancellationToken ct)
    {
        var chunkHashes = new List<string>();
        var totalBytes = 0L;
        var buffer = new byte[chunkSize];

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(
            absolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: chunkSize,
            useAsync: true);

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read <= 0)
                break;

            totalBytes += read;

            var payload = buffer.AsSpan(0, read);
            hasher.AppendData(payload);

            var chunkHash = SHA256.HashData(payload);
            chunkHashes.Add($"msha256:{Convert.ToHexString(chunkHash).ToLowerInvariant()}");
        }

        var fileHash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        return new CurrentFileDigest(fileHash, totalBytes, chunkHashes, BlockHashFamily.Sha256);
    }

    private static async Task<double?> ComputeByteSimilarityAsync(
        string baselinePath,
        string currentPath,
        CancellationToken ct)
    {
        if (!File.Exists(baselinePath) || !File.Exists(currentPath))
            return null;

        await using var baseline = new FileStream(
            baselinePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);

        await using var current = new FileStream(
            currentPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);

        var comparedLength = Math.Min(baseline.Length, current.Length);
        if (comparedLength <= 0)
            return baseline.Length == current.Length ? 1d : 0d;

        var baselineBuffer = new byte[128 * 1024];
        var currentBuffer = new byte[128 * 1024];

        long comparedBytes = 0;
        long equalBytes = 0;

        while (comparedBytes < comparedLength)
        {
            var toRead = (int)Math.Min(baselineBuffer.Length, comparedLength - comparedBytes);
            var readLeft = await baseline.ReadAsync(baselineBuffer.AsMemory(0, toRead), ct);
            var readRight = await current.ReadAsync(currentBuffer.AsMemory(0, toRead), ct);

            var read = Math.Min(readLeft, readRight);
            if (read <= 0)
                break;

            for (var i = 0; i < read; i++)
            {
                if (baselineBuffer[i] == currentBuffer[i])
                    equalBytes++;
            }

            comparedBytes += read;
        }

        if (comparedBytes <= 0)
            return null;

        return (double)equalBytes / comparedBytes;
    }

    private static (int Width, int Height)? TryReadImageDimensions(string path)
    {
        return DiffImageLoader.TryReadDimensions(path);
    }

    private static (int Width, int Height)? TryReadPngDimensions(Stream stream)
    {
        if (stream.Length < 24)
            return null;

        var buffer = new byte[24];
        if (stream.Read(buffer, 0, buffer.Length) != buffer.Length)
            return null;

        var isPng = buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47;
        if (!isPng)
            return null;

        var width = (buffer[16] << 24) | (buffer[17] << 16) | (buffer[18] << 8) | buffer[19];
        var height = (buffer[20] << 24) | (buffer[21] << 16) | (buffer[22] << 8) | buffer[23];

        return width > 0 && height > 0 ? (width, height) : null;
    }

    private static (int Width, int Height)? TryReadJpegDimensions(Stream stream)
    {
        if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8)
            return null;

        while (true)
        {
            var markerStart = stream.ReadByte();
            if (markerStart < 0)
                return null;

            if (markerStart != 0xFF)
                continue;

            var marker = stream.ReadByte();
            if (marker < 0)
                return null;

            while (marker == 0xFF)
            {
                marker = stream.ReadByte();
                if (marker < 0)
                    return null;
            }

            if (marker == 0xD9 || marker == 0xDA)
                return null;

            var lengthHigh = stream.ReadByte();
            var lengthLow = stream.ReadByte();
            if (lengthHigh < 0 || lengthLow < 0)
                return null;

            var segmentLength = (lengthHigh << 8) + lengthLow;
            if (segmentLength < 2)
                return null;

            if (IsJpegStartOfFrame(marker))
            {
                _ = stream.ReadByte(); // precision
                var hHigh = stream.ReadByte();
                var hLow = stream.ReadByte();
                var wHigh = stream.ReadByte();
                var wLow = stream.ReadByte();

                if (hHigh < 0 || hLow < 0 || wHigh < 0 || wLow < 0)
                    return null;

                var height = (hHigh << 8) + hLow;
                var width = (wHigh << 8) + wLow;
                return width > 0 && height > 0 ? (width, height) : null;
            }

            stream.Seek(segmentLength - 2, SeekOrigin.Current);
        }
    }

    private static bool IsJpegStartOfFrame(int marker)
        => marker is 0xC0 or 0xC1 or 0xC2 or 0xC3
            or 0xC5 or 0xC6 or 0xC7
            or 0xC9 or 0xCA or 0xCB
            or 0xCD or 0xCE or 0xCF;

    private static (int Width, int Height)? TryReadBmpDimensions(Stream stream)
    {
        if (stream.Length < 26)
            return null;

        var buffer = new byte[26];
        if (stream.Read(buffer, 0, buffer.Length) != buffer.Length)
            return null;

        if (buffer[0] != (byte)'B' || buffer[1] != (byte)'M')
            return null;

        var width = BitConverter.ToInt32(buffer, 18);
        var height = Math.Abs(BitConverter.ToInt32(buffer, 22));

        return width > 0 && height > 0 ? (width, height) : null;
    }

    private static (int Width, int Height)? TryReadGifDimensions(Stream stream)
    {
        if (stream.Length < 10)
            return null;

        var buffer = new byte[10];
        if (stream.Read(buffer, 0, buffer.Length) != buffer.Length)
            return null;

        var isGif = buffer[0] == (byte)'G' && buffer[1] == (byte)'I' && buffer[2] == (byte)'F';
        if (!isGif)
            return null;

        var width = buffer[6] | (buffer[7] << 8);
        var height = buffer[8] | (buffer[9] << 8);

        return width > 0 && height > 0 ? (width, height) : null;
    }

    private static string BuildBinaryPreviewMessage(
        string relativePath,
        PendingBinaryDiffSummaryDto summary,
        string previewType,
        string? extension = null)
    {
        var kind = string.Equals(previewType, "image", StringComparison.OrdinalIgnoreCase)
            ? "image"
            : string.Equals(previewType, "audio", StringComparison.OrdinalIgnoreCase)
                ? "audio"
                : "binary";

        var sizeLabel = $"{FormatBytes(summary.BaselineSizeBytes)} -> {FormatBytes(summary.CurrentSizeBytes)} ({FormatSignedBytes(summary.SizeDeltaBytes)})";

        var dedupLabel = summary.DedupRatio.HasValue
            ? $"shared blocks {summary.DedupRatio.Value * 100:F1}%"
            : "shared blocks n/a";

        var changedLabel = summary.ChangedBlockRatio.HasValue
            ? $"changed blocks {summary.ChangedBlockRatio.Value * 100:F1}%"
            : "changed blocks n/a";

        var similarityLabel = summary.ByteSimilarityRatio.HasValue
            ? $", byte similarity {summary.ByteSimilarityRatio.Value * 100:F1}%"
            : string.Empty;

        var officeHint = WordSemanticProjection.IsWordOoxmlExtension(extension)
            ? "   Word semantic diff is unavailable for this pair, showing binary-only summary."
            : IsOfficeDocumentExtension(extension)
                ? "   Legacy office format is compared as binary."
                : string.Empty;

        return $"{relativePath}   {kind}   {sizeLabel}   {dedupLabel}, {changedLabel}{similarityLabel}{officeHint}";
    }

    private static string BuildAudioPreviewMessage(
        string relativePath,
        PendingBinaryDiffSummaryDto summary,
        AudioDiffPreviewBuildResult preview)
    {
        var sizeLabel = $"{FormatBytes(summary.BaselineSizeBytes)} -> {FormatBytes(summary.CurrentSizeBytes)} ({FormatSignedBytes(summary.SizeDeltaBytes)})";
        var durationLabel = $"{FormatDuration(preview.BaselineDurationSeconds)} -> {FormatDuration(preview.CurrentDurationSeconds)}";
        var similarityLabel = $"signal similarity {preview.SignalSimilarityRatio * 100:F1}%";
        var spectralLabel = $"spectral similarity {preview.SpectralSimilarityRatio * 100:F1}%";
        var changedLabel = $"changed timeline {preview.ChangedTimeRatio * 100:F1}% in {preview.ChangedSegmentCount} segment(s)";
        return $"{relativePath}   audio   {sizeLabel}   {durationLabel}   {similarityLabel}, {spectralLabel}, {changedLabel}";
    }

    private static string BuildArchivePreviewMessage(
        string relativePath,
        PendingArchiveDiffPreviewDto preview)
    {
        return $"{relativePath}   +{preview.AddedEntryCount} / -{preview.RemovedEntryCount} / ~{preview.ChangedEntryCount}"
               + (preview.UnchangedEntryCount > 0 ? $"  (= {preview.UnchangedEntryCount})" : string.Empty);
    }

    private static PendingAudioDiffPreviewDto CreatePendingAudioPreviewDto(
        string baselineAudioPath,
        bool isBaselineTempFile,
        string currentAudioPath,
        bool isCurrentTempFile,
        AudioDiffPreviewBuildResult audioPreview)
    {
        return new PendingAudioDiffPreviewDto
        {
            BaselineAudioPath = baselineAudioPath,
            IsBaselineTempFile = isBaselineTempFile,
            CurrentAudioPath = currentAudioPath,
            IsCurrentTempFile = isCurrentTempFile,
            DifferenceAudioPath = audioPreview.DifferenceAudioPath,
            IsDifferenceTempFile = audioPreview.IsDifferenceTempFile,
            WaveformImagePath = audioPreview.WaveformImagePath,
            IsWaveformTempFile = audioPreview.IsWaveformTempFile,
            SpectrogramImagePath = audioPreview.SpectrogramImagePath,
            IsSpectrogramTempFile = audioPreview.IsSpectrogramTempFile,
            SpectralDeltaImagePath = audioPreview.SpectralDeltaImagePath,
            IsSpectralDeltaTempFile = audioPreview.IsSpectralDeltaTempFile,
            BaselineDurationSeconds = audioPreview.BaselineDurationSeconds,
            CurrentDurationSeconds = audioPreview.CurrentDurationSeconds,
            BaselineSampleRate = audioPreview.BaselineSampleRate,
            CurrentSampleRate = audioPreview.CurrentSampleRate,
            BaselineChannels = audioPreview.BaselineChannels,
            CurrentChannels = audioPreview.CurrentChannels,
            BaselinePeakAmplitude = audioPreview.BaselinePeakAmplitude,
            CurrentPeakAmplitude = audioPreview.CurrentPeakAmplitude,
            BaselineRmsAmplitude = audioPreview.BaselineRmsAmplitude,
            CurrentRmsAmplitude = audioPreview.CurrentRmsAmplitude,
            SignalSimilarityRatio = audioPreview.SignalSimilarityRatio,
            SpectralSimilarityRatio = audioPreview.SpectralSimilarityRatio,
            SpectralDeltaRatio = audioPreview.SpectralDeltaRatio,
            BaselineStereoCorrelation = audioPreview.BaselineStereoCorrelation,
            CurrentStereoCorrelation = audioPreview.CurrentStereoCorrelation,
            ChangedTimeRatio = audioPreview.ChangedTimeRatio,
            ChangedSegmentCount = audioPreview.ChangedSegmentCount,
            HasDurationMismatch = audioPreview.HasDurationMismatch,
            ChannelMetrics = audioPreview.ChannelMetrics
                .Select(metric => new PendingAudioChannelMetricDto
                {
                    ChannelIndex = metric.ChannelIndex,
                    ChannelDisplayName = metric.ChannelDisplayName,
                    BaselinePeakAmplitude = metric.BaselinePeakAmplitude,
                    CurrentPeakAmplitude = metric.CurrentPeakAmplitude,
                    BaselineRmsAmplitude = metric.BaselineRmsAmplitude,
                    CurrentRmsAmplitude = metric.CurrentRmsAmplitude,
                    SimilarityRatio = metric.SimilarityRatio,
                    ChangedTimeRatio = metric.ChangedTimeRatio
                })
                .ToArray(),
            BandMetrics = audioPreview.BandMetrics
                .Select(metric => new PendingAudioBandMetricDto
                {
                    BandDisplayName = metric.BandDisplayName,
                    BaselineEnergyRatio = metric.BaselineEnergyRatio,
                    CurrentEnergyRatio = metric.CurrentEnergyRatio,
                    DeltaRatio = metric.DeltaRatio,
                    SimilarityRatio = metric.SimilarityRatio
                })
                .ToArray(),
            ChangedSegments = audioPreview.ChangedSegments
                .Select(segment => new PendingAudioChangedSegmentDto
                {
                    SegmentIndex = segment.SegmentIndex,
                    StartSeconds = segment.StartSeconds,
                    EndSeconds = segment.EndSeconds,
                    DurationSeconds = segment.DurationSeconds,
                    AverageDifferenceRatio = segment.AverageDifferenceRatio,
                    PeakDifferenceRatio = segment.PeakDifferenceRatio
                })
                .ToArray()
        };
    }

    private static string FormatSignedBytes(long value)
    {
        if (value == 0)
            return "0 B";

        var sign = value > 0 ? "+" : "-";
        var absolute = value == long.MinValue ? long.MaxValue : Math.Abs(value);
        return $"{sign}{FormatBytes(absolute)}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds <= 0d)
            return "0:00";

        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1d
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");
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

    private async Task<IReadOnlyList<RepositorySnapshotHistoryItemDto>> BuildSnapshotHistoryAsync(
        IReadOnlyList<SnapshotLight> snapshots,
        IReadOnlyDictionary<long, IReadOnlyList<SnapshotLinkStateDto>> statesBySnapshot,
        int limit,
        CancellationToken ct)
    {
        if (snapshots.Count == 0)
            return Array.Empty<RepositorySnapshotHistoryItemDto>();

        var pairs = snapshots
            .Take(limit)
            .Select((current, index) => new
            {
                current,
                previous = index + 1 < snapshots.Count ? snapshots[index + 1] : null,
                index
            })
            .ToList();

        var result = new RepositorySnapshotHistoryItemDto?[pairs.Count];
        var concurrency = Math.Clamp(Environment.ProcessorCount, 2, SnapshotComparisonMaxConcurrency);
        using var gate = new SemaphoreSlim(concurrency, concurrency);

        var comparisonTasks = pairs.Select(async pair =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var currentStates = statesBySnapshot.GetValueOrDefault(pair.current.SnapshotId, EmptySnapshotLinkStates);
                var previousStates = pair.previous is null
                    ? EmptySnapshotLinkStates
                    : statesBySnapshot.GetValueOrDefault(pair.previous.SnapshotId, EmptySnapshotLinkStates);

                var comparison = await snapshotComparison.CompareSnapshotLinksAsync(currentStates, previousStates, ct);
                if (pair.previous is not null && comparison.ChangedFilesCount == 0)
                    return;

                result[pair.index] = new RepositorySnapshotHistoryItemDto(
                    pair.current.SnapshotId,
                    pair.current.Title,
                    pair.current.CreatedAtUtc,
                    RepositorySnapshotTriggerClassifier.GetKind(pair.current.Trigger),
                    pair.current.IsArchived,
                    pair.current.Trigger,
                    comparison.ChangedFilesCount,
                    ParseSnapshotTags(pair.current.TagsCsv));
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(comparisonTasks);

        return result
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();
    }

    private static int[] NormalizeRepositoryIds(IReadOnlyCollection<int> repositoryIds)
        => repositoryIds
            .Where(repositoryId => repositoryId > 0)
            .Distinct()
            .ToArray();

    private static string AddRepositoryIdParameters(DbCommand command, IReadOnlyList<int> repositoryIds)
    {
        var parameterNames = new string[repositoryIds.Count];
        for (var index = 0; index < repositoryIds.Count; index++)
        {
            var parameterName = $"@repositoryId{index}";
            AddCommandParameter(command, parameterName, repositoryIds[index]);
            parameterNames[index] = parameterName;
        }

        return string.Join(", ", parameterNames);
    }

    private static void AddCommandParameter(DbCommand command, string parameterName, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = parameterName;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static bool ReadBoolean(DbDataReader reader, int ordinal)
        => !reader.IsDBNull(ordinal) && Convert.ToBoolean(reader.GetValue(ordinal));

    private static DateTime ReadDateTime(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal)
            ? DateTime.MinValue
            : Convert.ToDateTime(reader.GetValue(ordinal));

    private static string? ReadNullableString(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string? NormalizeSnapshotTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var title = value.Trim();
        if (title.Length <= 256)
            return title;
        return title[..256];
    }

    private static string? SerializeSnapshotTags(IReadOnlyCollection<string>? tags)
    {
        var normalized = NormalizeSnapshotTags(tags);
        return normalized.Count == 0 ? null : string.Join(';', normalized);
    }

    private static IReadOnlyList<string> ParseSnapshotTags(string? tagsCsv)
    {
        if (string.IsNullOrWhiteSpace(tagsCsv))
            return Array.Empty<string>();

        return NormalizeSnapshotTags(tagsCsv
            .Split([';', ',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static IReadOnlyList<string> NormalizeSnapshotTags(IEnumerable<string>? tags)
    {
        if (tags is null)
            return Array.Empty<string>();

        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in tags)
        {
            var candidate = (raw ?? string.Empty).Trim().TrimStart('#');
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            if (candidate.Length > 48)
                candidate = candidate[..48].Trim();

            if (candidate.Length == 0 || !seen.Add(candidate))
                continue;

            values.Add(candidate);
            if (values.Count >= 12)
                break;
        }

        return values;
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

    private static RepositoryScanEntryDto NormalizeSnapshotEntry(RepositoryScanEntryDto entry)
    {
        var relativePath = NormalizeRelativePath(entry.RelativePath).Trim('/');
        var parentRelativePath = string.IsNullOrWhiteSpace(entry.ParentRelativePath)
            ? null
            : NormalizeRelativePath(entry.ParentRelativePath).Trim('/');

        return entry with
        {
            RelativePath = relativePath,
            ParentRelativePath = string.IsNullOrWhiteSpace(parentRelativePath) ? null : parentRelativePath,
            Extension = entry.IsDirectory ? null : KnownFileExtensions.NormalizeTrackedFileFormat(entry.Extension)
        };
    }

    private static string ToAbsolutePath(string rootPath, string relativePath)
    {
        var rel = relativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(rootPath, rel);
    }

    private async Task<bool> NeedsVersionMaterializationAsync(
        int repositoryId,
        IReadOnlyDictionary<string, RepositoryScanEntryDto> currentFilesByPath,
        CancellationToken ct)
    {
        if (currentFilesByPath.Count == 0)
            return false;

        var currentPaths = currentFilesByPath.Keys.ToList();
        var identities = await db.Set<FileIdentity>()
            .IgnoreQueryFilters()
            .Where(i => i.RepositoryId == repositoryId && currentPaths.Contains(i.RelativePath))
            .Select(i => new { i.Id, i.RelativePath })
            .ToListAsync(ct);

        if (identities.Count != currentPaths.Count)
            return true;

        var identityIds = identities.Select(i => i.Id).ToList();
        var existingVersions = await db.Set<FileVersion>()
            .Where(v => identityIds.Contains(v.FileIdentityId) && !v.IsDeleted)
            .OrderByDescending(v => v.CreatedAt)
            .ThenByDescending(v => v.Id)
            .Select(v => new
            {
                v.Id,
                v.FileIdentityId,
                v.ContentHashSha256,
                v.SizeBytes,
                v.IsDeletionMarker
            })
            .ToListAsync(ct);

        var latestVersionsByIdentityId = existingVersions
            .GroupBy(v => v.FileIdentityId)
            .ToDictionary(g => g.Key, g => g.First());

        var latestVersionIds = latestVersionsByIdentityId.Values
            .Select(v => v.Id)
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

        foreach (var identity in identities)
        {
            if (!currentFilesByPath.TryGetValue(identity.RelativePath, out var current))
                return true;

            if (!latestVersionsByIdentityId.TryGetValue(identity.Id, out var latest))
                return true;

            if (latest.IsDeletionMarker)
                return true;

            if (latest.SizeBytes != current.SizeBytes)
                return true;

            if (!string.Equals(
                    latest.ContentHashSha256 ?? string.Empty,
                    current.ContentHashSha256 ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (current.SizeBytes > 0 && !latestVersionIdsWithBlocks.Contains(latest.Id))
                return true;
        }

        return false;
    }

    private static RepositoryBusyFileDto BuildBusyFileWarning(
        string relativePath,
        string absolutePath,
        Exception ex)
        => new(
            relativePath,
            absolutePath,
            ex.Message,
            "Close the application that is currently using this file and retry the scan.");

    private async Task<BlockStorageAttemptResult> TryStoreBlocksAsync(
        string relativePath,
        string absolutePath,
        CancellationToken ct)
    {
        try
        {
            if (!File.Exists(absolutePath))
            {
                return new BlockStorageAttemptResult(
                    null,
                    BuildBusyFileWarning(
                        relativePath,
                        absolutePath,
                        new FileNotFoundException("File not found for block storage.", absolutePath)));
            }

            return new BlockStorageAttemptResult(
                await contentStore.StoreFileAsync(absolutePath, ct),
                null);
        }
        catch (IOException ex)
        {
            log.LogWarning(ex, "Failed to store file blocks for {Path}", absolutePath);
            return new BlockStorageAttemptResult(
                null,
                BuildBusyFileWarning(relativePath, absolutePath, ex));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to store file blocks for {Path}", absolutePath);
            throw new InvalidOperationException($"Failed to store file blocks {absolutePath}", ex);
        }
    }

    private sealed record WorkingSnapshotSeed(
        long SnapshotId,
        DateTime CreatedAt,
        bool HasFileLinks);

    private sealed record BlockStorageAttemptResult(
        StoredFileContentDto? StoredContent,
        RepositoryBusyFileDto? BusyFile);

}


