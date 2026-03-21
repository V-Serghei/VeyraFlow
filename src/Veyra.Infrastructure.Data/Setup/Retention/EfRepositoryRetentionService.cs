using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.DTOs;
using Veyra.Domain.Entities;
using Veyra.Infrastructure.Data.Persistence;
using Veyra.Infrastructure.Data.Setup.Models.Retention;

namespace Veyra.Infrastructure.Data.Setup;

public sealed class EfRepositoryRetentionService(
    VeyraDbContext db,
    IConfiguration configuration,
    IRepositorySnapshotArchiveService snapshotArchive,
    ILogger<EfRepositoryRetentionService> log)
    : IRepositoryRetentionService
{
    private const string ManagedHashPrefix = "sha256-";

    public async Task<IReadOnlyList<RepositoryRetentionRunResultDto>> RunDueRetentionAsync(
        IProgress<RepositoryRetentionProgressDto>? progress = null,
        CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var nowLocal = DateTime.Now;
        var dueRepos = await db.Set<Repository>()
            .Where(r => !r.IsDeleted && r.RetentionEnabled)
            .Select(r => new
            {
                r.Id,
                r.RetentionRunIntervalMinutes,
                r.RetentionLastRunAt,
                r.RetentionMaintenanceWindowStartHour,
                r.RetentionMaintenanceWindowEndHour
            })
            .ToListAsync(ct);

        var results = new List<RepositoryRetentionRunResultDto>();

        foreach (var repo in dueRepos)
        {
            ct.ThrowIfCancellationRequested();

            var intervalMinutes = Math.Clamp(repo.RetentionRunIntervalMinutes, 5, 7 * 24 * 60);
            if (repo.RetentionLastRunAt is not null && nowUtc - repo.RetentionLastRunAt.Value < TimeSpan.FromMinutes(intervalMinutes))
                continue;

            if (!IsWithinMaintenanceWindow(
                    nowLocal,
                    repo.RetentionMaintenanceWindowStartHour,
                    repo.RetentionMaintenanceWindowEndHour))
            {
                continue;
            }

            try
            {
                var result = await RunRetentionAsync(
                    repo.Id,
                    dryRun: false,
                    policyOverride: null,
                    progress: progress,
                    ct: ct);
                results.Add(result);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Scheduled retention failed for repository {RepositoryId}", repo.Id);
            }
        }

        return results;
    }

    public async Task<RepositoryRetentionRunResultDto> RunRetentionAsync(
        int repositoryId,
        bool dryRun,
        RepositoryRetentionPolicyDto? policyOverride = null,
        IProgress<RepositoryRetentionProgressDto>? progress = null,
        CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;
        progress?.Report(new RepositoryRetentionProgressDto("start", 0, "Preparing retention run..."));

        var repository = await db.Set<Repository>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == repositoryId, ct);

        if (repository is null || repository.IsDeleted)
        {
            return BuildResult(
                repositoryId,
                dryRun,
                startedAt,
                DateTime.UtcNow,
                policyApplied: false,
                summary: "Repository not found.");
        }

        var effectivePolicy = BuildEffectivePolicy(repository, policyOverride);
        if (!effectivePolicy.Enabled)
        {
            return BuildResult(
                repositoryId,
                dryRun,
                startedAt,
                DateTime.UtcNow,
                policyApplied: false,
                summary: "Retention is disabled for this repository.");
        }

        var archiveMode = RepositoryRetentionStorageModes.IsArchive(effectivePolicy.StorageMode);

        var snapshots = await db.Set<RepositorySnapshot>()
            .IgnoreQueryFilters()
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
            .Where(s => !archiveMode || !s.IsArchived)
            .OrderBy(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .Select(s => new SnapshotState(
                s.Id,
                s.CreatedAt,
                s.TotalFileBytes,
                s.Trigger))
            .ToListAsync(ct);

        if (snapshots.Count == 0)
        {
            var finished = DateTime.UtcNow;
            var summary = "No snapshots found, cleanup is not required.";
            if (!dryRun)
                await UpdateRetentionRunStateAsync(repositoryId, finished, summary, ct);

            return BuildResult(
                repositoryId,
                dryRun,
                startedAt,
                finished,
                policyApplied: false,
                summary: summary);
        }

        progress?.Report(new RepositoryRetentionProgressDto("plan", 10, "Planning retention candidates..."));

        var triggerFilter = NormalizeTriggerFilters(effectivePolicy.TriggerFilters);
        var (snapshotsToDelete, automaticSnapshotsCompacted) = PlanSnapshotsToDelete(
            snapshots,
            effectivePolicy.MaxAgeDays,
            effectivePolicy.MaxSnapshots,
            effectivePolicy.MaxTotalSizeBytes,
            triggerFilter,
            effectivePolicy.AllowManualSnapshotCleanup,
            effectivePolicy.AutomaticCompactionEnabled,
            effectivePolicy.AutomaticCompactionWindowHours,
            DateTime.UtcNow);

        if (snapshots.Count - snapshotsToDelete.Count <= 0)
        {
            var keep = snapshots[^1].Id;
            snapshotsToDelete.Remove(keep);
        }

        var retainedSnapshotIds = snapshots
            .Where(s => !snapshotsToDelete.Contains(s.Id))
            .Select(s => s.Id)
            .ToList();
        var (manualSnapshotsMarked, automaticSnapshotsMarked, workingSnapshotsMarked) =
            CountSnapshotsByKind(snapshots, snapshotsToDelete);

        var snapshotEntriesMarked = await CountSnapshotEntriesToMarkAsync(repositoryId, snapshotsToDelete, ct);
        var snapshotLinksMarked = await CountSnapshotLinksToMarkAsync(snapshotsToDelete, ct);

        progress?.Report(new RepositoryRetentionProgressDto("analyze", 25, "Collecting GC candidates..."));

        List<long> candidateVersionIds;
        List<long> candidateIdentityIds;
        List<BlockState> candidateBlocks;
        List<long> candidateDiffIds;
        List<long> candidateHunkIds;
        List<long> candidateLineIds;
        HashSet<string> deletableManagedHashes;
        long estimatedManagedFreedBytes;

        if (archiveMode)
        {
            candidateVersionIds = [];
            candidateIdentityIds = [];
            candidateDiffIds = [];
            candidateHunkIds = [];
            candidateLineIds = [];
            candidateBlocks = await GetArchiveCandidateBlocksAsync(repositoryId, snapshotsToDelete, retainedSnapshotIds, ct);
            deletableManagedHashes = await ResolveArchivePrunableManagedHashesAsync(repositoryId, retainedSnapshotIds, ct);
            estimatedManagedFreedBytes = EstimateManagedFreedBytes(deletableManagedHashes);
        }
        else
        {
            candidateVersionIds = await GetCandidateVersionIdsAsync(repositoryId, retainedSnapshotIds, ct);
            candidateIdentityIds = await GetCandidateIdentityIdsAsync(repositoryId, retainedSnapshotIds, candidateVersionIds, ct);

            candidateBlocks = await db.Set<FileVersionBlock>()
                .IgnoreQueryFilters()
                .Where(b => !b.IsDeleted && candidateVersionIds.Contains(b.FileVersionId))
                .Select(b => new BlockState(b.Id, b.BlockStorageKey, b.StoredSizeBytes))
                .ToListAsync(ct);

            candidateDiffIds = await db.Set<FileVersionTextDiff>()
                .IgnoreQueryFilters()
                .Where(d => !d.IsDeleted && (candidateVersionIds.Contains(d.LeftFileVersionId) || candidateVersionIds.Contains(d.RightFileVersionId)))
                .Select(d => d.Id)
                .ToListAsync(ct);

            candidateHunkIds = await db.Set<FileVersionTextDiffHunk>()
                .IgnoreQueryFilters()
                .Where(h => !h.IsDeleted && candidateDiffIds.Contains(h.DiffId))
                .Select(h => h.Id)
                .ToListAsync(ct);

            candidateLineIds = await db.Set<FileVersionTextDiffLine>()
                .IgnoreQueryFilters()
                .Where(l => !l.IsDeleted && candidateDiffIds.Contains(l.DiffId))
                .Select(l => l.Id)
                .ToListAsync(ct);

            deletableManagedHashes = await ResolveDeletableManagedHashesAsync(candidateBlocks, ct);
            estimatedManagedFreedBytes = EstimateManagedFreedBytes(deletableManagedHashes);
        }

        if (dryRun)
        {
            var finished = DateTime.UtcNow;
            var summary = BuildSummary(
                snapshotsToDelete.Count,
                automaticSnapshotsCompacted,
                candidateVersionIds.Count,
                candidateDiffIds.Count,
                candidateBlocks.Count,
                true,
                estimatedManagedFreedBytes,
                archiveMode,
                archiveMode ? snapshotsToDelete.Count : 0);

            return BuildResult(
                repositoryId,
                dryRun,
                startedAt,
                finished,
                policyApplied: archiveMode
                    ? snapshotsToDelete.Count > 0
                    : snapshotsToDelete.Count > 0 || candidateVersionIds.Count > 0 || candidateDiffIds.Count > 0,
                archiveMode: archiveMode,
                snapshotsMarked: snapshotsToDelete.Count,
                snapshotsArchived: archiveMode ? snapshotsToDelete.Count : 0,
                manualSnapshotsMarked: manualSnapshotsMarked,
                automaticSnapshotsMarked: automaticSnapshotsMarked,
                workingSnapshotsMarked: workingSnapshotsMarked,
                automaticSnapshotsCompacted: automaticSnapshotsCompacted,
                snapshotEntriesMarked: snapshotEntriesMarked,
                snapshotLinksMarked: snapshotLinksMarked,
                fileVersionsMarked: candidateVersionIds.Count,
                fileVersionBlocksMarked: candidateBlocks.Count,
                fileIdentitiesMarked: candidateIdentityIds.Count,
                diffsMarked: candidateDiffIds.Count,
                diffHunksMarked: candidateHunkIds.Count,
                diffLinesMarked: candidateLineIds.Count,
                textLineAtomsDeleted: 0,
                blockFilesDeleted: deletableManagedHashes.Count,
                estimatedFreedBytes: estimatedManagedFreedBytes,
                summary: summary);
        }

        if (archiveMode)
        {
            progress?.Report(new RepositoryRetentionProgressDto("archive", 55, "Archiving selected snapshots..."));

            var archivedCount = await snapshotArchive.EnsureSnapshotsArchivedAsync(
                repositoryId,
                snapshotsToDelete.ToList(),
                ct);

            progress?.Report(new RepositoryRetentionProgressDto("archive-prune", 85, "Cleaning local blocks that now live in archive..."));

            var (archivedDeletedBlockFiles, archivedDeletedManagedBytes) = await snapshotArchive.PruneArchivedOnlyLocalBlocksAsync(
                repositoryId,
                ct);

            var finishedAtArchive = DateTime.UtcNow;
            var summaryArchive = BuildSummary(
                snapshotsToDelete.Count,
                automaticSnapshotsCompacted,
                0,
                0,
                archivedDeletedBlockFiles,
                false,
                archivedDeletedManagedBytes,
                archiveMode,
                archivedCount);

            await UpdateRetentionRunStateAsync(repositoryId, finishedAtArchive, summaryArchive, ct);

            progress?.Report(new RepositoryRetentionProgressDto("done", 100, "Archive retention completed."));

            return BuildResult(
                repositoryId,
                dryRun,
                startedAt,
                finishedAtArchive,
                policyApplied: archivedCount > 0,
                archiveMode: true,
                snapshotsMarked: snapshotsToDelete.Count,
                snapshotsArchived: archivedCount,
                manualSnapshotsMarked: manualSnapshotsMarked,
                automaticSnapshotsMarked: automaticSnapshotsMarked,
                workingSnapshotsMarked: workingSnapshotsMarked,
                automaticSnapshotsCompacted: automaticSnapshotsCompacted,
                snapshotEntriesMarked: snapshotEntriesMarked,
                snapshotLinksMarked: snapshotLinksMarked,
                fileVersionsMarked: 0,
                fileVersionBlocksMarked: candidateBlocks.Count,
                fileIdentitiesMarked: 0,
                diffsMarked: 0,
                diffHunksMarked: 0,
                diffLinesMarked: 0,
                textLineAtomsDeleted: 0,
                blockFilesDeleted: archivedDeletedBlockFiles,
                estimatedFreedBytes: archivedDeletedManagedBytes,
                summary: summaryArchive);
        }

        progress?.Report(new RepositoryRetentionProgressDto("mark", 45, "Applying soft-delete marks..."));

        var markTimestamp = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        if (snapshotsToDelete.Count > 0)
        {
            await db.Set<SnapshotFileLink>()
                .IgnoreQueryFilters()
                .Where(l => !l.IsDeleted && snapshotsToDelete.Contains(l.SnapshotId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.IsDeleted, true)
                    .SetProperty(x => x.DeletedAt, markTimestamp), ct);

            await db.Set<RepositorySnapshotEntry>()
                .IgnoreQueryFilters()
                .Where(e => !e.IsDeleted && e.RepositoryId == repositoryId && snapshotsToDelete.Contains(e.SnapshotId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.IsDeleted, true)
                    .SetProperty(x => x.DeletedAt, markTimestamp), ct);

            await db.Set<RepositorySnapshot>()
                .IgnoreQueryFilters()
                .Where(s => !s.IsDeleted && s.RepositoryId == repositoryId && snapshotsToDelete.Contains(s.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.IsDeleted, true)
                    .SetProperty(x => x.DeletedAt, markTimestamp), ct);
        }

        if (candidateVersionIds.Count > 0)
        {
            await db.Set<FileVersionBlock>()
                .IgnoreQueryFilters()
                .Where(b => !b.IsDeleted && candidateVersionIds.Contains(b.FileVersionId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.IsDeleted, true)
                    .SetProperty(x => x.DeletedAt, markTimestamp), ct);

            await db.Set<FileVersion>()
                .IgnoreQueryFilters()
                .Where(v => !v.IsDeleted && candidateVersionIds.Contains(v.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.IsDeleted, true)
                    .SetProperty(x => x.DeletedAt, markTimestamp), ct);
        }

        if (candidateDiffIds.Count > 0)
        {
            await db.Set<FileVersionTextDiffLine>()
                .IgnoreQueryFilters()
                .Where(l => !l.IsDeleted && candidateDiffIds.Contains(l.DiffId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.IsDeleted, true)
                    .SetProperty(x => x.DeletedAt, markTimestamp), ct);

            await db.Set<FileVersionTextDiffHunk>()
                .IgnoreQueryFilters()
                .Where(h => !h.IsDeleted && candidateDiffIds.Contains(h.DiffId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.IsDeleted, true)
                    .SetProperty(x => x.DeletedAt, markTimestamp), ct);

            await db.Set<FileVersionTextDiff>()
                .IgnoreQueryFilters()
                .Where(d => !d.IsDeleted && candidateDiffIds.Contains(d.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.IsDeleted, true)
                    .SetProperty(x => x.DeletedAt, markTimestamp)
                    .SetProperty(x => x.UpdatedAt, markTimestamp), ct);
        }

        if (candidateIdentityIds.Count > 0)
        {
            await db.Set<FileIdentity>()
                .IgnoreQueryFilters()
                .Where(i => !i.IsDeleted && candidateIdentityIds.Contains(i.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.IsDeleted, true)
                    .SetProperty(x => x.DeletedAt, markTimestamp)
                    .SetProperty(x => x.UpdatedAt, markTimestamp), ct);
        }

        progress?.Report(new RepositoryRetentionProgressDto("sweep-db", 70, "Sweeping deleted rows and links..."));

        if (candidateLineIds.Count > 0)
        {
            await db.Set<FileVersionTextDiffLine>()
                .IgnoreQueryFilters()
                .Where(l => l.IsDeleted && candidateLineIds.Contains(l.Id))
                .ExecuteDeleteAsync(ct);
        }

        if (candidateHunkIds.Count > 0)
        {
            await db.Set<FileVersionTextDiffHunk>()
                .IgnoreQueryFilters()
                .Where(h => h.IsDeleted && candidateHunkIds.Contains(h.Id))
                .ExecuteDeleteAsync(ct);
        }

        if (candidateDiffIds.Count > 0)
        {
            await db.Set<FileVersionTextDiff>()
                .IgnoreQueryFilters()
                .Where(d => d.IsDeleted && candidateDiffIds.Contains(d.Id))
                .ExecuteDeleteAsync(ct);
        }

        if (candidateBlocks.Count > 0)
        {
            var candidateBlockIds = candidateBlocks.Select(b => b.Id).ToList();
            await db.Set<FileVersionBlock>()
                .IgnoreQueryFilters()
                .Where(b => b.IsDeleted && candidateBlockIds.Contains(b.Id))
                .ExecuteDeleteAsync(ct);
        }

        if (candidateVersionIds.Count > 0)
        {
            await db.Set<FileVersion>()
                .IgnoreQueryFilters()
                .Where(v => v.IsDeleted && candidateVersionIds.Contains(v.Id))
                .ExecuteDeleteAsync(ct);
        }

        if (candidateIdentityIds.Count > 0)
        {
            await db.Set<FileIdentity>()
                .IgnoreQueryFilters()
                .Where(i => i.IsDeleted && candidateIdentityIds.Contains(i.Id))
                .ExecuteDeleteAsync(ct);
        }

        if (snapshotsToDelete.Count > 0)
        {
            await db.Set<SnapshotFileLink>()
                .IgnoreQueryFilters()
                .Where(l => l.IsDeleted && snapshotsToDelete.Contains(l.SnapshotId))
                .ExecuteDeleteAsync(ct);

            await db.Set<RepositorySnapshotEntry>()
                .IgnoreQueryFilters()
                .Where(e => e.IsDeleted && e.RepositoryId == repositoryId && snapshotsToDelete.Contains(e.SnapshotId))
                .ExecuteDeleteAsync(ct);

            await db.Set<RepositorySnapshot>()
                .IgnoreQueryFilters()
                .Where(s => s.IsDeleted && s.RepositoryId == repositoryId && snapshotsToDelete.Contains(s.Id))
                .ExecuteDeleteAsync(ct);
        }

        var deletedTextAtoms = await DeleteOrphanTextAtomsAsync(ct);

        progress?.Report(new RepositoryRetentionProgressDto("sweep-blocks", 85, "Deleting unreferenced block files..."));

        var (deletedBlockFiles, deletedManagedBytes) = DeleteManagedBlockFiles(deletableManagedHashes);

        var finishedAt = DateTime.UtcNow;
        var summaryText = BuildSummary(
            snapshotsToDelete.Count,
            automaticSnapshotsCompacted,
            candidateVersionIds.Count,
            candidateDiffIds.Count,
            candidateBlocks.Count,
            false,
            deletedManagedBytes,
            archiveMode: false,
            snapshotsArchived: 0);

        await UpdateRetentionRunStateAsync(repositoryId, finishedAt, summaryText, ct);
        await tx.CommitAsync(ct);

        progress?.Report(new RepositoryRetentionProgressDto("done", 100, "Retention completed."));

        return BuildResult(
            repositoryId,
            dryRun,
            startedAt,
            finishedAt,
            policyApplied: snapshotsToDelete.Count > 0 || candidateVersionIds.Count > 0 || candidateDiffIds.Count > 0,
            archiveMode: false,
            snapshotsMarked: snapshotsToDelete.Count,
            snapshotsArchived: 0,
            manualSnapshotsMarked: manualSnapshotsMarked,
            automaticSnapshotsMarked: automaticSnapshotsMarked,
            workingSnapshotsMarked: workingSnapshotsMarked,
            automaticSnapshotsCompacted: automaticSnapshotsCompacted,
            snapshotEntriesMarked: snapshotEntriesMarked,
            snapshotLinksMarked: snapshotLinksMarked,
            fileVersionsMarked: candidateVersionIds.Count,
            fileVersionBlocksMarked: candidateBlocks.Count,
            fileIdentitiesMarked: candidateIdentityIds.Count,
            diffsMarked: candidateDiffIds.Count,
            diffHunksMarked: candidateHunkIds.Count,
            diffLinesMarked: candidateLineIds.Count,
            textLineAtomsDeleted: deletedTextAtoms,
            blockFilesDeleted: deletedBlockFiles,
            estimatedFreedBytes: deletedManagedBytes,
            summary: summaryText);
    }

    private async Task<int> CountSnapshotEntriesToMarkAsync(int repositoryId, HashSet<long> snapshotIds, CancellationToken ct)
    {
        if (snapshotIds.Count == 0)
            return 0;

        return await db.Set<RepositorySnapshotEntry>()
            .IgnoreQueryFilters()
            .Where(e => !e.IsDeleted && e.RepositoryId == repositoryId && snapshotIds.Contains(e.SnapshotId))
            .CountAsync(ct);
    }

    private async Task<int> CountSnapshotLinksToMarkAsync(HashSet<long> snapshotIds, CancellationToken ct)
    {
        if (snapshotIds.Count == 0)
            return 0;

        return await db.Set<SnapshotFileLink>()
            .IgnoreQueryFilters()
            .Where(l => !l.IsDeleted && snapshotIds.Contains(l.SnapshotId))
            .CountAsync(ct);
    }

    private async Task<List<long>> GetCandidateVersionIdsAsync(
        int repositoryId,
        IReadOnlyCollection<long> retainedSnapshotIds,
        CancellationToken ct)
    {
        if (retainedSnapshotIds.Count == 0)
        {
            return await db.Set<FileVersion>()
                .IgnoreQueryFilters()
                .Where(v => !v.IsDeleted && v.FileIdentity.RepositoryId == repositoryId)
                .Select(v => v.Id)
                .ToListAsync(ct);
        }

        var retainedSnapshotIdList = retainedSnapshotIds
            .Distinct()
            .ToList();

        var activeVersionIdsQuery = db.Set<SnapshotFileLink>()
            .IgnoreQueryFilters()
            .Where(l => !l.IsDeleted && retainedSnapshotIdList.Contains(l.SnapshotId))
            .Select(l => l.FileVersionId)
            .Distinct();

        return await db.Set<FileVersion>()
            .IgnoreQueryFilters()
            .Where(v => !v.IsDeleted && v.FileIdentity.RepositoryId == repositoryId)
            .Where(v => !activeVersionIdsQuery.Contains(v.Id))
            .Select(v => v.Id)
            .ToListAsync(ct);
    }

    private async Task<List<long>> GetCandidateIdentityIdsAsync(
        int repositoryId,
        IReadOnlyCollection<long> retainedSnapshotIds,
        IReadOnlyCollection<long> candidateVersionIds,
        CancellationToken ct)
    {
        if (candidateVersionIds.Count == 0)
            return [];

        return await db.Set<FileIdentity>()
            .IgnoreQueryFilters()
            .Where(i => !i.IsDeleted && i.RepositoryId == repositoryId)
            .Where(i => !db.Set<FileVersion>()
                .IgnoreQueryFilters()
                .Any(v => !v.IsDeleted && v.FileIdentityId == i.Id && !candidateVersionIds.Contains(v.Id)))
            .Where(i => retainedSnapshotIds.Count == 0 || !db.Set<SnapshotFileLink>()
                .IgnoreQueryFilters()
                .Any(l => !l.IsDeleted && l.FileIdentityId == i.Id && retainedSnapshotIds.Contains(l.SnapshotId)))
            .Select(i => i.Id)
            .ToListAsync(ct);
    }

    private async Task<HashSet<string>> ResolveDeletableManagedHashesAsync(
        IReadOnlyCollection<BlockState> candidateBlocks,
        CancellationToken ct)
    {
        var candidateHashes = candidateBlocks
            .Select(b => b.BlockHash)
            .Where(h => h.StartsWith(ManagedHashPrefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidateHashes.Count == 0)
            return [];

        var activeManagedHashes = await db.Set<FileVersionBlock>()
            .IgnoreQueryFilters()
            .Where(b => !b.IsDeleted && candidateHashes.Contains(b.BlockStorageKey))
            .Select(b => b.BlockStorageKey)
            .Distinct()
            .ToListAsync(ct);

        var activeSet = new HashSet<string>(activeManagedHashes, StringComparer.OrdinalIgnoreCase);
        var deletable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hash in candidateHashes)
        {
            if (!activeSet.Contains(hash))
                deletable.Add(hash);
        }

        return deletable;
    }

    private long EstimateManagedFreedBytes(IReadOnlyCollection<string> managedHashes)
    {
        if (managedHashes.Count == 0)
            return 0;

        var root = ResolveStoreRoot(configuration);
        long total = 0;

        foreach (var managedHash in managedHashes)
        {
            var path = BuildManagedBlockPath(root, managedHash);
            if (!File.Exists(path))
                continue;

            try
            {
                total += new FileInfo(path).Length;
            }
            catch
            {
            }
        }

        return total;
    }

    private (int DeletedFiles, long DeletedBytes) DeleteManagedBlockFiles(IReadOnlyCollection<string> managedHashes)
    {
        if (managedHashes.Count == 0)
            return (0, 0);

        var root = ResolveStoreRoot(configuration);
        var deletedFiles = 0;
        long deletedBytes = 0;

        foreach (var managedHash in managedHashes)
        {
            var path = BuildManagedBlockPath(root, managedHash);
            if (!File.Exists(path))
                continue;

            try
            {
                var info = new FileInfo(path);
                var size = info.Exists ? info.Length : 0;
                File.Delete(path);
                deletedFiles++;
                deletedBytes += size;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to delete managed block file {Path}", path);
            }
        }

        return (deletedFiles, deletedBytes);
    }

    private async Task<int> DeleteOrphanTextAtomsAsync(CancellationToken ct)
    {
        return await db.Set<TextLineAtom>()
            .Where(a => !db.Set<FileVersionTextDiffLine>().Any(l => l.TextLineAtomId == a.Id))
            .ExecuteDeleteAsync(ct);
    }

    private async Task UpdateRetentionRunStateAsync(
        int repositoryId,
        DateTime runAtUtc,
        string status,
        CancellationToken ct)
    {
        await db.Set<Repository>()
            .IgnoreQueryFilters()
            .Where(r => r.Id == repositoryId && !r.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.RetentionLastRunAt, runAtUtc)
                .SetProperty(x => x.RetentionLastStatus, status)
                .SetProperty(x => x.UpdatedAt, runAtUtc), ct);
    }

    private static (HashSet<long> SnapshotsToDelete, int AutomaticSnapshotsCompacted) PlanSnapshotsToDelete(
        IReadOnlyList<SnapshotState> snapshots,
        int? maxAgeDays,
        int? maxSnapshots,
        long? maxTotalSizeBytes,
        HashSet<string> triggerFilter,
        bool allowManualSnapshotCleanup,
        bool automaticCompactionEnabled,
        int? automaticCompactionWindowHours,
        DateTime nowUtc)
    {
        var eligible = snapshots
            .Where(s => MatchesTriggerFilter(s.Trigger, triggerFilter, allowManualSnapshotCleanup))
            .ToList();

        var toDelete = new HashSet<long>();
        var automaticSnapshotsCompacted = 0;

        if (maxAgeDays is > 0)
        {
            var cutoff = nowUtc.AddDays(-maxAgeDays.Value);
            foreach (var snapshot in eligible)
            {
                if (snapshot.CreatedAt < cutoff)
                    toDelete.Add(snapshot.Id);
            }
        }

        automaticSnapshotsCompacted = ApplyAutomaticCompactionCandidates(
            snapshots,
            toDelete,
            triggerFilter,
            allowManualSnapshotCleanup,
            automaticCompactionEnabled,
            automaticCompactionWindowHours);

        if (maxSnapshots is > 0)
        {
            var sorted = eligible
                .Where(s => !toDelete.Contains(s.Id))
                .OrderByDescending(s => s.CreatedAt)
                .ThenByDescending(s => s.Id)
                .ToList();

            var keepSet = sorted
                .Take(maxSnapshots.Value)
                .Select(s => s.Id)
                .ToHashSet();

            foreach (var snapshot in sorted)
            {
                if (!keepSet.Contains(snapshot.Id))
                    toDelete.Add(snapshot.Id);
            }
        }

        if (maxTotalSizeBytes is > 0)
        {
            var total = snapshots
                .Where(s => !toDelete.Contains(s.Id))
                .Sum(s => s.TotalFileBytes);

            if (total > maxTotalSizeBytes.Value)
            {
                var removable = snapshots
                    .Where(s => !toDelete.Contains(s.Id) && MatchesTriggerFilter(s.Trigger, triggerFilter, allowManualSnapshotCleanup))
                    .OrderBy(s => s.CreatedAt)
                    .ThenBy(s => s.Id)
                    .ToList();

                foreach (var snapshot in removable)
                {
                    if (total <= maxTotalSizeBytes.Value)
                        break;

                    var remainingCount = snapshots.Count - toDelete.Count;
                    if (remainingCount <= 1)
                        break;

                    if (toDelete.Add(snapshot.Id))
                        total -= snapshot.TotalFileBytes;
                }
            }
        }

        return (toDelete, automaticSnapshotsCompacted);
    }

    private static RepositoryRetentionPolicyDto BuildEffectivePolicy(
        Repository repository,
        RepositoryRetentionPolicyDto? policyOverride)
    {
        var source = policyOverride ?? new RepositoryRetentionPolicyDto(
            repository.RetentionEnabled,
            repository.RetentionMaxAgeDays,
            repository.RetentionMaxSnapshots,
            repository.RetentionMaxTotalSizeBytes,
            ParseTriggerFilters(repository.RetentionTriggerFilter),
            repository.RetentionRunIntervalMinutes,
            repository.RetentionMaintenanceWindowStartHour,
            repository.RetentionMaintenanceWindowEndHour,
            repository.RetentionLastRunAt,
            repository.RetentionLastStatus,
            repository.RetentionStorageMode,
            repository.RetentionAllowManualSnapshotCleanup,
            repository.RetentionAutomaticCompactionEnabled,
            repository.RetentionAutomaticCompactionWindowHours);

        var normalizedTriggers = source.TriggerFilters
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (source.Enabled && normalizedTriggers.Count == 0)
            normalizedTriggers = ["automatic"];

        return source with
        {
            MaxAgeDays = NormalizePositive(source.MaxAgeDays),
            MaxSnapshots = NormalizePositive(source.MaxSnapshots),
            MaxTotalSizeBytes = NormalizePositive(source.MaxTotalSizeBytes),
            TriggerFilters = normalizedTriggers,
            RunIntervalMinutes = Math.Clamp(source.RunIntervalMinutes, 5, 7 * 24 * 60),
            MaintenanceWindowStartHour = NormalizeHour(source.MaintenanceWindowStartHour),
            MaintenanceWindowEndHour = NormalizeHour(source.MaintenanceWindowEndHour),
            StorageMode = RepositoryRetentionStorageModes.Normalize(source.StorageMode),
            AutomaticCompactionWindowHours = source.AutomaticCompactionEnabled
                ? NormalizePositive(source.AutomaticCompactionWindowHours)
                : null
        };
    }

    private static List<string> ParseTriggerFilters(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return [];

        return csv
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(static v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static HashSet<string> NormalizeTriggerFilters(IReadOnlyList<string> triggerFilters)
    {
        if (triggerFilters.Count == 0)
            return ["automatic"];

        return triggerFilters
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool MatchesTriggerFilter(
        string? trigger,
        IReadOnlySet<string> triggerFilter,
        bool allowManualSnapshotCleanup)
    {
        var normalizedTrigger = (trigger ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedTrigger))
            return false;

        if (!allowManualSnapshotCleanup && RepositorySnapshotTriggerClassifier.IsManual(normalizedTrigger))
            return false;

        if (triggerFilter.Count == 0)
            return true;

        foreach (var token in triggerFilter)
        {
            if (string.IsNullOrWhiteSpace(token))
                continue;

            if (string.Equals(token, "all", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(token, normalizedTrigger, StringComparison.OrdinalIgnoreCase))
                return true;

            if (token.EndsWith('*') &&
                normalizedTrigger.StartsWith(token[..^1], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(token, "automatic", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "auto", StringComparison.OrdinalIgnoreCase))
            {
                if (IsAutomaticTrigger(normalizedTrigger))
                    return true;

                continue;
            }

            if (string.Equals(token, "manual", StringComparison.OrdinalIgnoreCase))
            {
                if (allowManualSnapshotCleanup && IsManualTrigger(normalizedTrigger))
                    return true;

                continue;
            }

            if (string.Equals(token, "working", StringComparison.OrdinalIgnoreCase))
            {
                if (IsWorkingTrigger(normalizedTrigger))
                    return true;

                continue;
            }

            if (string.Equals(token, "scheduled", StringComparison.OrdinalIgnoreCase)
                && normalizedTrigger.StartsWith("scheduled_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int ApplyAutomaticCompactionCandidates(
        IReadOnlyList<SnapshotState> snapshots,
        HashSet<long> toDelete,
        IReadOnlySet<string> triggerFilter,
        bool allowManualSnapshotCleanup,
        bool automaticCompactionEnabled,
        int? automaticCompactionWindowHours)
    {
        if (!automaticCompactionEnabled || automaticCompactionWindowHours is not > 0)
            return 0;

        var ticksPerBucket = TimeSpan.FromHours(automaticCompactionWindowHours.Value).Ticks;
        if (ticksPerBucket <= 0)
            return 0;

        var compactable = snapshots
            .Where(s => !toDelete.Contains(s.Id))
            .Where(s => RepositorySnapshotTriggerClassifier.IsAutomatic(s.Trigger))
            .Where(s => MatchesTriggerFilter(s.Trigger, triggerFilter, allowManualSnapshotCleanup))
            .GroupBy(s => GetCompactionBucket(s.CreatedAt, ticksPerBucket));

        var compacted = 0;

        foreach (var bucket in compactable)
        {
            var ordered = bucket
                .OrderByDescending(s => s.CreatedAt)
                .ThenByDescending(s => s.Id)
                .ToList();

            foreach (var snapshot in ordered.Skip(1))
            {
                if (toDelete.Add(snapshot.Id))
                    compacted++;
            }
        }

        return compacted;
    }

    private static long GetCompactionBucket(DateTime createdAt, long ticksPerBucket)
    {
        var utc = createdAt.Kind == DateTimeKind.Utc
            ? createdAt
            : createdAt.ToUniversalTime();

        return utc.Ticks / ticksPerBucket;
    }

    private static bool IsAutomaticTrigger(string trigger)
    {
        return RepositorySnapshotTriggerClassifier.IsAutomatic(trigger)
               || RepositorySnapshotTriggerClassifier.IsWorking(trigger);
    }

    private static bool IsManualTrigger(string trigger)
    {
        return RepositorySnapshotTriggerClassifier.IsManual(trigger);
    }

    private static bool IsWorkingTrigger(string trigger)
    {
        return RepositorySnapshotTriggerClassifier.IsWorking(trigger);
    }

    private static bool IsWithinMaintenanceWindow(
        DateTime localNow,
        int? startHour,
        int? endHour)
    {
        if (!startHour.HasValue || !endHour.HasValue)
            return false;

        var start = startHour.GetValueOrDefault();
        var end = endHour.GetValueOrDefault();

        if (start is < 0 or > 23 || end is < 0 or > 23)
            return false;

        if (start == end)
            return false;

        var hour = localNow.Hour;
        if (start < end)
            return hour >= start && hour < end;

        return hour >= start || hour < end;
    }

    private static int? NormalizePositive(int? value)
        => value is > 0 ? value : null;

    private static long? NormalizePositive(long? value)
        => value is > 0 ? value : null;

    private static int? NormalizeHour(int? value)
        => value is >= 0 and <= 23 ? value : null;

    private static string ResolveStoreRoot(IConfiguration configuration)
    {
        var fromCfg = configuration["Storage:BlockStorePath"];
        var fromEnv = Environment.GetEnvironmentVariable("VEYRA_BLOCK_STORE");

        var root = !string.IsNullOrWhiteSpace(fromCfg)
            ? fromCfg.Trim()
            : !string.IsNullOrWhiteSpace(fromEnv)
                ? fromEnv.Trim()
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VeyraFlow",
                    "block-store");

        Directory.CreateDirectory(root);
        return Path.GetFullPath(root);
    }

    private static string BuildManagedBlockPath(string storeRoot, string managedHash)
    {
        var normalizedWithPrefix = managedHash.Trim().ToLowerInvariant();
        var normalizedHash = normalizedWithPrefix.StartsWith(ManagedHashPrefix, StringComparison.OrdinalIgnoreCase)
            ? normalizedWithPrefix[ManagedHashPrefix.Length..]
            : normalizedWithPrefix;

        var p1 = normalizedHash.Length >= 2 ? normalizedHash[..2] : "00";
        var p2 = normalizedHash.Length >= 4 ? normalizedHash[2..4] : "00";

        return Path.Combine(storeRoot, "managed", "blocks", p1, p2, $"{normalizedHash}.bin");
    }

    private static string BuildSummary(
        int snapshots,
        int automaticSnapshotsCompacted,
        int versions,
        int diffs,
        int blocks,
        bool dryRun,
        long freedBytes,
        bool archiveMode,
        int snapshotsArchived)
    {
        var mode = dryRun ? "Dry-run" : "Applied";
        if (archiveMode)
        {
            return $"{mode}: snapshots={snapshots}, archived={snapshotsArchived}, auto-compacted={automaticSnapshotsCompacted}, local-blocks-pruned={blocks}, freed={FormatBytes(freedBytes)}";
        }

        return $"{mode}: snapshots={snapshots}, auto-compacted={automaticSnapshotsCompacted}, versions={versions}, diffs={diffs}, blocks={blocks}, freed={FormatBytes(freedBytes)}";
    }

    private static (int ManualSnapshotsMarked, int AutomaticSnapshotsMarked, int WorkingSnapshotsMarked) CountSnapshotsByKind(
        IReadOnlyCollection<SnapshotState> snapshots,
        IReadOnlySet<long> snapshotsToDelete)
    {
        if (snapshots.Count == 0 || snapshotsToDelete.Count == 0)
            return (0, 0, 0);

        var manual = 0;
        var automatic = 0;
        var working = 0;

        foreach (var snapshot in snapshots)
        {
            if (!snapshotsToDelete.Contains(snapshot.Id))
                continue;

            var kind = RepositorySnapshotTriggerClassifier.GetKind(snapshot.Trigger);
            switch (kind)
            {
                case RepositorySnapshotKind.Automatic:
                    automatic++;
                    break;
                case RepositorySnapshotKind.Working:
                    working++;
                    break;
                default:
                    manual++;
                    break;
            }
        }

        return (manual, automatic, working);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "0 B";

        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var index = 0;

        while (value >= 1024 && index < suffixes.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.##} {suffixes[index]}";
    }

    private static RepositoryRetentionRunResultDto BuildResult(
        int repositoryId,
        bool dryRun,
        DateTime startedAtUtc,
        DateTime finishedAtUtc,
        bool policyApplied,
        bool archiveMode = false,
        int snapshotsMarked = 0,
        int snapshotsArchived = 0,
        int manualSnapshotsMarked = 0,
        int automaticSnapshotsMarked = 0,
        int workingSnapshotsMarked = 0,
        int automaticSnapshotsCompacted = 0,
        int snapshotEntriesMarked = 0,
        int snapshotLinksMarked = 0,
        int fileVersionsMarked = 0,
        int fileVersionBlocksMarked = 0,
        int fileIdentitiesMarked = 0,
        int diffsMarked = 0,
        int diffHunksMarked = 0,
        int diffLinesMarked = 0,
        int textLineAtomsDeleted = 0,
        int blockFilesDeleted = 0,
        long estimatedFreedBytes = 0,
        string summary = "")
    {
        return new RepositoryRetentionRunResultDto(
            repositoryId,
            dryRun,
            startedAtUtc,
            finishedAtUtc,
            policyApplied,
            archiveMode,
            snapshotsMarked,
            snapshotsArchived,
            manualSnapshotsMarked,
            automaticSnapshotsMarked,
            workingSnapshotsMarked,
            automaticSnapshotsCompacted,
            snapshotEntriesMarked,
            snapshotLinksMarked,
            fileVersionsMarked,
            fileVersionBlocksMarked,
            fileIdentitiesMarked,
            diffsMarked,
            diffHunksMarked,
            diffLinesMarked,
            textLineAtomsDeleted,
            blockFilesDeleted,
            estimatedFreedBytes,
            summary);
    }

    private async Task<List<BlockState>> GetArchiveCandidateBlocksAsync(
        int repositoryId,
        IReadOnlyCollection<long> snapshotIdsToArchive,
        IReadOnlyCollection<long> retainedSnapshotIds,
        CancellationToken ct)
    {
        if (snapshotIdsToArchive.Count == 0)
            return [];

        var activeVersionIdsQuery = retainedSnapshotIds.Count == 0
            ? db.Set<SnapshotFileLink>()
                .IgnoreQueryFilters()
                .Where(static _ => false)
                .Select(l => l.FileVersionId)
            : db.Set<SnapshotFileLink>()
                .IgnoreQueryFilters()
                .Where(l => !l.IsDeleted && retainedSnapshotIds.Contains(l.SnapshotId))
                .Select(l => l.FileVersionId)
                .Distinct();

        return await db.Set<FileVersionBlock>()
            .IgnoreQueryFilters()
            .Where(b => !b.IsDeleted)
            .Where(b => b.FileVersion.SnapshotLinks.Any(l => !l.IsDeleted && snapshotIdsToArchive.Contains(l.SnapshotId)))
            .Where(b => !activeVersionIdsQuery.Contains(b.FileVersionId))
            .Select(b => new BlockState(b.Id, b.BlockStorageKey, b.StoredSizeBytes))
            .Distinct()
            .ToListAsync(ct);
    }

    private async Task<HashSet<string>> ResolveArchivePrunableManagedHashesAsync(
        int repositoryId,
        IReadOnlyCollection<long> retainedSnapshotIds,
        CancellationToken ct)
    {
        var retainedSnapshotIdList = retainedSnapshotIds
            .Distinct()
            .ToList();

        var activeManagedHashes = retainedSnapshotIdList.Count == 0
            ? []
            : await db.Set<FileVersionBlock>()
                .IgnoreQueryFilters()
                .Where(b => !b.IsDeleted
                            && b.BlockStorageKey.StartsWith(ManagedHashPrefix)
                            && b.FileVersion.SnapshotLinks.Any(l => !l.IsDeleted && retainedSnapshotIdList.Contains(l.SnapshotId)))
                .Select(b => b.BlockStorageKey)
                .Distinct()
                .ToListAsync(ct);

        var archivedManagedHashes = await db.Set<FileVersionBlock>()
            .IgnoreQueryFilters()
            .Where(b => !b.IsDeleted
                        && b.BlockStorageKey.StartsWith(ManagedHashPrefix)
                        && b.FileVersion.SnapshotLinks.Any(l =>
                            !l.IsDeleted
                            && l.Snapshot.RepositoryId == repositoryId
                            && !l.Snapshot.IsDeleted))
            .Select(b => b.BlockStorageKey)
            .Distinct()
            .ToListAsync(ct);

        var activeSet = new HashSet<string>(activeManagedHashes, StringComparer.OrdinalIgnoreCase);
        return archivedManagedHashes
            .Where(hash => !activeSet.Contains(hash))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

}
