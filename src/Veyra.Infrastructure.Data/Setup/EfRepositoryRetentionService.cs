using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.DTOs;
using Veyra.Domain.Entities;
using Veyra.Infrastructure.Data.Persistence;

namespace Veyra.Infrastructure.Data.Setup;

public sealed class EfRepositoryRetentionService(
    VeyraDbContext db,
    IConfiguration configuration,
    ILogger<EfRepositoryRetentionService> log)
    : IRepositoryRetentionService
{
    private const string ManagedHashPrefix = "sha256-";

    public async Task<IReadOnlyList<RepositoryRetentionRunResultDto>> RunDueRetentionAsync(
        IProgress<RepositoryRetentionProgressDto>? progress = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var dueRepos = await db.Set<Repository>()
            .Where(r => !r.IsDeleted && r.RetentionEnabled)
            .Select(r => new
            {
                r.Id,
                r.RetentionRunIntervalMinutes,
                r.RetentionLastRunAt
            })
            .ToListAsync(ct);

        var results = new List<RepositoryRetentionRunResultDto>();

        foreach (var repo in dueRepos)
        {
            ct.ThrowIfCancellationRequested();

            var intervalMinutes = Math.Clamp(repo.RetentionRunIntervalMinutes, 5, 7 * 24 * 60);
            if (repo.RetentionLastRunAt is not null && now - repo.RetentionLastRunAt.Value < TimeSpan.FromMinutes(intervalMinutes))
                continue;

            try
            {
                var result = await RunRetentionAsync(repo.Id, dryRun: false, progress, ct);
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

        if (!repository.RetentionEnabled)
        {
            return BuildResult(
                repositoryId,
                dryRun,
                startedAt,
                DateTime.UtcNow,
                policyApplied: false,
                summary: "Retention is disabled for this repository.");
        }

        var snapshots = await db.Set<RepositorySnapshot>()
            .IgnoreQueryFilters()
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
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

        var triggerFilter = ParseTriggerFilter(repository.RetentionTriggerFilter);
        var snapshotsToDelete = PlanSnapshotsToDelete(
            snapshots,
            repository.RetentionMaxAgeDays,
            repository.RetentionMaxSnapshots,
            repository.RetentionMaxTotalSizeBytes,
            triggerFilter,
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

        var snapshotEntriesMarked = await CountSnapshotEntriesToMarkAsync(repositoryId, snapshotsToDelete, ct);
        var snapshotLinksMarked = await CountSnapshotLinksToMarkAsync(snapshotsToDelete, ct);

        progress?.Report(new RepositoryRetentionProgressDto("analyze", 25, "Collecting GC candidates..."));

        var candidateVersionIds = await GetCandidateVersionIdsAsync(repositoryId, retainedSnapshotIds, ct);
        var candidateIdentityIds = await GetCandidateIdentityIdsAsync(repositoryId, retainedSnapshotIds, candidateVersionIds, ct);

        var candidateBlocks = await db.Set<FileVersionBlock>()
            .IgnoreQueryFilters()
            .Where(b => !b.IsDeleted && candidateVersionIds.Contains(b.FileVersionId))
            .Select(b => new BlockState(b.Id, b.BlockHashBlake3, b.StoredSizeBytes))
            .ToListAsync(ct);

        var candidateDiffIds = await db.Set<FileVersionTextDiff>()
            .IgnoreQueryFilters()
            .Where(d => !d.IsDeleted && (candidateVersionIds.Contains(d.LeftFileVersionId) || candidateVersionIds.Contains(d.RightFileVersionId)))
            .Select(d => d.Id)
            .ToListAsync(ct);

        var candidateHunkIds = await db.Set<FileVersionTextDiffHunk>()
            .IgnoreQueryFilters()
            .Where(h => !h.IsDeleted && candidateDiffIds.Contains(h.DiffId))
            .Select(h => h.Id)
            .ToListAsync(ct);

        var candidateLineIds = await db.Set<FileVersionTextDiffLine>()
            .IgnoreQueryFilters()
            .Where(l => !l.IsDeleted && candidateDiffIds.Contains(l.DiffId))
            .Select(l => l.Id)
            .ToListAsync(ct);

        var deletableManagedHashes = await ResolveDeletableManagedHashesAsync(candidateBlocks, ct);
        var estimatedManagedFreedBytes = EstimateManagedFreedBytes(deletableManagedHashes);

        if (dryRun)
        {
            var finished = DateTime.UtcNow;
            var summary = BuildSummary(
                snapshotsToDelete.Count,
                candidateVersionIds.Count,
                candidateDiffIds.Count,
                candidateBlocks.Count,
                true,
                estimatedManagedFreedBytes);

            return BuildResult(
                repositoryId,
                dryRun,
                startedAt,
                finished,
                policyApplied: snapshotsToDelete.Count > 0 || candidateVersionIds.Count > 0 || candidateDiffIds.Count > 0,
                snapshotsMarked: snapshotsToDelete.Count,
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
            candidateVersionIds.Count,
            candidateDiffIds.Count,
            candidateBlocks.Count,
            false,
            deletedManagedBytes);

        await UpdateRetentionRunStateAsync(repositoryId, finishedAt, summaryText, ct);
        await tx.CommitAsync(ct);

        progress?.Report(new RepositoryRetentionProgressDto("done", 100, "Retention completed."));

        return BuildResult(
            repositoryId,
            dryRun,
            startedAt,
            finishedAt,
            policyApplied: snapshotsToDelete.Count > 0 || candidateVersionIds.Count > 0 || candidateDiffIds.Count > 0,
            snapshotsMarked: snapshotsToDelete.Count,
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
            .Where(b => !b.IsDeleted && candidateHashes.Contains(b.BlockHashBlake3))
            .Select(b => b.BlockHashBlake3)
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

    private static HashSet<long> PlanSnapshotsToDelete(
        IReadOnlyList<SnapshotState> snapshots,
        int? maxAgeDays,
        int? maxSnapshots,
        long? maxTotalSizeBytes,
        HashSet<string> triggerFilter,
        DateTime nowUtc)
    {
        var eligible = snapshots
            .Where(s => triggerFilter.Count == 0 || triggerFilter.Contains(s.Trigger))
            .ToList();

        var toDelete = new HashSet<long>();

        if (maxAgeDays is > 0)
        {
            var cutoff = nowUtc.AddDays(-maxAgeDays.Value);
            foreach (var snapshot in eligible)
            {
                if (snapshot.CreatedAt < cutoff)
                    toDelete.Add(snapshot.Id);
            }
        }

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
                    .Where(s => !toDelete.Contains(s.Id) && (triggerFilter.Count == 0 || triggerFilter.Contains(s.Trigger)))
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

        return toDelete;
    }

    private static HashSet<string> ParseTriggerFilter(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return [];

        return csv
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

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
        int versions,
        int diffs,
        int blocks,
        bool dryRun,
        long freedBytes)
    {
        var mode = dryRun ? "Dry-run" : "Applied";
        return $"{mode}: snapshots={snapshots}, versions={versions}, diffs={diffs}, blocks={blocks}, freed={FormatBytes(freedBytes)}";
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
        int snapshotsMarked = 0,
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
            snapshotsMarked,
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

    private sealed record SnapshotState(
        long Id,
        DateTime CreatedAt,
        long TotalFileBytes,
        string Trigger);

    private sealed record BlockState(
        long Id,
        string BlockHash,
        long StoredSizeBytes);
}
