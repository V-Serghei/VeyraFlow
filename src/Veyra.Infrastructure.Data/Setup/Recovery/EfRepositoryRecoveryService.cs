using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Common.Repository;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.FileVersions;
using Veyra.Application.DTOs.Repository.Recovery;
using Veyra.Application.DTOs.Repository.Scanning;
using Veyra.Domain.Entities;
using Veyra.Infrastructure.Data.Persistence;
using Veyra.Infrastructure.Data.Setup.Models.Recovery;

namespace Veyra.Infrastructure.Data.Setup;

public sealed class EfRepositoryRecoveryService(
    VeyraDbContext db,
    IRepositoryIntegrityService integrity,
    IRepositoryScanner scanner,
    IFileContentStore contentStore,
    ILogger<EfRepositoryRecoveryService> log)
    : IRepositoryRecoveryService
{
    public async Task<RepositoryRecoveryResultDto> RepairRepositoryAsync(
        int repositoryId,
        bool repairMissingBlocksFromCloud = true,
        CancellationToken ct = default)
    {
        log.LogInformation(
            "Repository repair started. RepositoryId {RepositoryId}. RepairMissingBlocksFromCloud {RepairMissingBlocksFromCloud}",
            repositoryId,
            repairMissingBlocksFromCloud);
        var startedAt = DateTime.UtcNow;
        var messages = new List<string>();

        var integrityRun = await integrity.VerifyRepositoryAsync(
            repositoryId,
            repairFromCloud: repairMissingBlocksFromCloud,
            maxIssueSamples: 200,
            progress: null,
            ct);

        messages.Add(integrityRun.Summary);

        var restoredMissingFiles = await RestoreMissingFilesFromLatestKnownVersionsAsync(repositoryId, ct);
        if (restoredMissingFiles.RestoredFiles > 0 || restoredMissingFiles.SkippedFiles > 0)
            messages.Add(restoredMissingFiles.Summary);
        if (restoredMissingFiles.RestoredFiles > 0)
        {
            var refreshScan = await scanner.ScanRepositoryAsync(
                repositoryId,
                progress: null,
                options: new RepositoryScanOptionsDto(
                    SaveFileVersions: false,
                    TriggerOverride: "sync_index_repair_restore",
                    SnapshotTitle: null),
                ct: ct);
            messages.Add($"Repository index refreshed after missing-file restore. Entries={refreshScan.TotalEntries}, Files={refreshScan.FileEntries}.");
        }

        var relink = await RelinkRepositoryAsync(repositoryId, ct);
        messages.AddRange(relink.Messages);

        var success = integrityRun.UnresolvedIssueCount == 0 && relink.Success;
        var affectedRows = integrityRun.RepairedBlockCount + restoredMissingFiles.RestoredFiles + Math.Max(0, relink.AffectedRows);
        var summary =
            $"Repair {(success ? "completed" : "finished with warnings")}. RepairedBlocks={integrityRun.RepairedBlockCount}, RestoredMissingFiles={restoredMissingFiles.RestoredFiles}, Unresolved={integrityRun.UnresolvedIssueCount}, RelinkAffected={relink.AffectedRows}.";

        var result = new RepositoryRecoveryResultDto(
            RepositoryId: repositoryId,
            Action: "repair",
            Success: success,
            StartedAtUtc: startedAt,
            FinishedAtUtc: DateTime.UtcNow,
            AffectedRows: affectedRows,
            Messages: messages,
            Summary: summary);

        log.LogInformation(
            "Repository repair finished. RepositoryId {RepositoryId}. Success {Success}. AffectedRows {AffectedRows}. Summary {Summary}",
            repositoryId,
            result.Success,
            result.AffectedRows,
            result.Summary);

        return result;
    }

    private async Task<MissingFileRestoreResult> RestoreMissingFilesFromLatestKnownVersionsAsync(
        int repositoryId,
        CancellationToken ct)
    {
        var repository = await db.Set<Repository>()
            .AsNoTracking()
            .Include(r => r.Directory)
            .FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted && !r.Directory.IsDeleted, ct);

        if (repository is null || string.IsNullOrWhiteSpace(repository.Directory.Path) || !Directory.Exists(repository.Directory.Path))
            return MissingFileRestoreResult.None;

        var latestVersionRows = await db.Set<FileVersion>()
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(v => !v.IsDeleted
                        && !v.FileIdentity.Repository.IsDeleted
                        && v.FileIdentity.RepositoryId == repositoryId)
            .Select(v => new
            {
                v.Id,
                v.FileIdentity.RelativePath,
                v.SizeBytes,
                v.IsDeletionMarker,
                v.ContentHashSha256,
                v.LastWriteUtc,
                v.CreatedAt
            })
            .ToListAsync(ct);

        if (latestVersionRows.Count == 0)
            return MissingFileRestoreResult.None;

        var latestVersionsByPath = latestVersionRows
            .GroupBy(v => NormalizeRelativePath(v.RelativePath), StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(v => v.CreatedAt)
                .ThenByDescending(v => v.Id)
                .First())
            .Where(v => !v.IsDeletionMarker)
            .ToList();

        var latestVersionIds = latestVersionsByPath.Select(v => v.Id).ToList();
        var blocksByVersion = latestVersionIds.Count == 0
            ? new Dictionary<long, List<StoredFileBlockDto>>()
            : (await db.Set<FileVersionBlock>()
                    .AsNoTracking()
                    .IgnoreQueryFilters()
                    .Where(b => latestVersionIds.Contains(b.FileVersionId) && !b.IsDeleted)
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
                .ToDictionary(g => g.Key, g => g.Select(x => x.Block).ToList());

        var restored = 0;
        var skipped = 0;

        foreach (var version in latestVersionsByPath)
        {
            ct.ThrowIfCancellationRequested();

            if (RepositoryInternalPathFilter.ShouldIgnoreForSnapshotRestore(version.RelativePath))
                continue;

            var targetPath = ResolvePathWithinRoot(repository.Directory.Path, version.RelativePath);
            if (targetPath is null)
            {
                skipped++;
                continue;
            }

            if (File.Exists(targetPath))
            {
                TrySetLastWriteTimeUtc(targetPath, version.LastWriteUtc);
                continue;
            }

            var blocks = blocksByVersion.GetValueOrDefault(version.Id) ?? [];
            if (version.SizeBytes > 0 && blocks.Count == 0)
            {
                skipped++;
                continue;
            }

            var missingBlocks = await contentStore.FindMissingBlocksAsync(
                blocks.Select(b => b.BlockStorageKey).ToHashSet(StringComparer.OrdinalIgnoreCase),
                ct);
            if (missingBlocks.Count > 0)
            {
                skipped++;
                continue;
            }

            var parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);

            await contentStore.RestoreFileAsync(
                blocks,
                targetPath,
                overwriteExisting: false,
                expectedContentHash: version.ContentHashSha256,
                ct: ct);
            TrySetLastWriteTimeUtc(targetPath, version.LastWriteUtc);
            restored++;
        }

        if (restored == 0 && skipped == 0)
            return MissingFileRestoreResult.None;

        log.LogInformation(
            "Repository missing file restore finished. RepositoryId {RepositoryId}. Restored {Restored}. Skipped {Skipped}",
            repositoryId,
            restored,
            skipped);

        return new MissingFileRestoreResult(
            restored,
            skipped,
            $"Missing file restore completed. Restored={restored}, Skipped={skipped}.");
    }

    public async Task<RepositoryRecoveryResultDto> ReindexRepositoryAsync(
        int repositoryId,
        CancellationToken ct = default)
    {
        log.LogInformation("Repository reindex started. RepositoryId {RepositoryId}", repositoryId);
        var startedAt = DateTime.UtcNow;
        var options = new RepositoryScanOptionsDto(
            SaveFileVersions: true,
            TriggerOverride: "recovery_reindex",
            SnapshotTitle: $"reindex_{DateTime.Now:yyyyMMdd_HHmmss}");

        var scanResult = await scanner.ScanRepositoryAsync(
            repositoryId,
            progress: null,
            options: options,
            ct: ct);

        var summary =
            $"Reindex completed. Entries={scanResult.TotalEntries}, Files={scanResult.FileEntries}, Trigger={scanResult.Trigger}.";

        var result = new RepositoryRecoveryResultDto(
            RepositoryId: repositoryId,
            Action: "reindex",
            Success: true,
            StartedAtUtc: startedAt,
            FinishedAtUtc: DateTime.UtcNow,
            AffectedRows: scanResult.TotalEntries,
            Messages: [summary],
            Summary: summary);

        log.LogInformation(
            "Repository reindex finished. RepositoryId {RepositoryId}. AffectedRows {AffectedRows}. Summary {Summary}",
            repositoryId,
            result.AffectedRows,
            result.Summary);

        return result;
    }

    public async Task<RepositoryRecoveryResultDto> RelinkRepositoryAsync(
        int repositoryId,
        CancellationToken ct = default)
    {
        log.LogInformation("Repository relink started. RepositoryId {RepositoryId}", repositoryId);
        var startedAt = DateTime.UtcNow;

        var repositoryExists = await db.Set<Repository>()
            .IgnoreQueryFilters()
            .AnyAsync(r => r.Id == repositoryId && !r.IsDeleted, ct);

        if (!repositoryExists)
        {
            return new RepositoryRecoveryResultDto(
                RepositoryId: repositoryId,
                Action: "relink",
                Success: false,
                StartedAtUtc: startedAt,
                FinishedAtUtc: DateTime.UtcNow,
                AffectedRows: 0,
                Messages: ["Repository not found."],
                Summary: "Repository not found.");
        }

        var snapshots = await db.Set<RepositorySnapshot>()
            .IgnoreQueryFilters()
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
            .OrderBy(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .Select(s => new SnapshotState(s.Id, s.CreatedAt))
            .ToListAsync(ct);

        if (snapshots.Count == 0)
        {
            return new RepositoryRecoveryResultDto(
                RepositoryId: repositoryId,
                Action: "relink",
                Success: true,
                StartedAtUtc: startedAt,
                FinishedAtUtc: DateTime.UtcNow,
                AffectedRows: 0,
                Messages: ["No snapshots to relink."],
                Summary: "No snapshots to relink.");
        }

        var snapshotIds = snapshots.Select(s => s.SnapshotId).ToHashSet();

        var entries = await db.Set<RepositorySnapshotEntry>()
            .IgnoreQueryFilters()
            .Where(e => e.RepositoryId == repositoryId
                        && !e.IsDeleted
                        && !e.IsDirectory
                        && snapshotIds.Contains(e.SnapshotId))
            .Select(e => new SnapshotEntryState(
                e.SnapshotId,
                e.RelativePath,
                e.ContentHashSha256,
                e.SizeBytes))
            .ToListAsync(ct);

        var entriesBySnapshot = entries
            .GroupBy(e => e.SnapshotId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var identities = await db.Set<FileIdentity>()
            .IgnoreQueryFilters()
            .Where(i => i.RepositoryId == repositoryId && !i.IsDeleted)
            .Select(i => new IdentityState(i.Id, i.RelativePath))
            .ToListAsync(ct);

        var identityByPath = identities.ToDictionary(i => i.RelativePath, StringComparer.OrdinalIgnoreCase);

        var versions = await db.Set<FileVersion>()
            .IgnoreQueryFilters()
            .Where(v => !v.IsDeleted && v.FileIdentity.RepositoryId == repositoryId)
            .Select(v => new VersionState(
                v.Id,
                v.FileIdentityId,
                v.ContentHashSha256,
                v.SizeBytes,
                v.CreatedAt,
                v.IsDeletionMarker))
            .ToListAsync(ct);

        var versionsByIdentity = versions
            .GroupBy(v => v.FileIdentityId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(v => v.CreatedAtUtc)
                    .ThenByDescending(v => v.FileVersionId)
                    .ToList());

        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var removed = await db.Set<SnapshotFileLink>()
            .IgnoreQueryFilters()
            .Where(l => snapshotIds.Contains(l.SnapshotId))
            .ExecuteDeleteAsync(ct);

        var links = new List<SnapshotFileLink>(entries.Count);
        var missingIdentities = 0;
        var missingVersions = 0;

        foreach (var snapshot in snapshots)
        {
            if (!entriesBySnapshot.TryGetValue(snapshot.SnapshotId, out var snapshotEntries))
                continue;

            var linkedIdentityIds = new HashSet<long>();

            foreach (var entry in snapshotEntries)
            {
                if (!identityByPath.TryGetValue(entry.RelativePath, out var identity))
                {
                    missingIdentities++;
                    continue;
                }

                if (!versionsByIdentity.TryGetValue(identity.FileIdentityId, out var identityVersions)
                    || identityVersions.Count == 0)
                {
                    missingVersions++;
                    continue;
                }

                var selectedVersion = SelectBestVersion(identityVersions, snapshot.CreatedAtUtc, entry.ContentHashSha256, entry.SizeBytes);
                if (selectedVersion is null)
                {
                    missingVersions++;
                    continue;
                }

                if (!linkedIdentityIds.Add(identity.FileIdentityId))
                    continue;

                links.Add(new SnapshotFileLink
                {
                    SnapshotId = snapshot.SnapshotId,
                    FileIdentityId = identity.FileIdentityId,
                    FileVersionId = selectedVersion.FileVersionId,
                    CreatedAt = snapshot.CreatedAtUtc,
                    IsDeleted = false,
                    DeletedAt = null
                });
            }
        }

        var distinctLinks = links
            .GroupBy(x => new { x.SnapshotId, x.FileIdentityId })
            .Select(g => g.First())
            .ToList();
        var suppressedDuplicateLinks = links.Count - distinctLinks.Count;

        if (distinctLinks.Count > 0)
            db.AddRange(distinctLinks);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var messages = new List<string>
        {
            $"Removed stale links: {removed}.",
            $"Inserted links: {distinctLinks.Count}."
        };

        if (missingIdentities > 0)
            messages.Add($"Skipped entries without identity: {missingIdentities}.");

        if (missingVersions > 0)
            messages.Add($"Skipped entries without version: {missingVersions}.");

        if (suppressedDuplicateLinks > 0)
            messages.Add($"Suppressed duplicate links: {suppressedDuplicateLinks}.");

        var success = missingIdentities == 0 && missingVersions == 0;
        var summary = success
            ? $"Relink completed. Removed={removed}, Inserted={distinctLinks.Count}."
            : $"Relink completed with warnings. Removed={removed}, Inserted={distinctLinks.Count}, MissingIdentities={missingIdentities}, MissingVersions={missingVersions}.";

        var result = new RepositoryRecoveryResultDto(
            RepositoryId: repositoryId,
            Action: "relink",
            Success: success,
            StartedAtUtc: startedAt,
            FinishedAtUtc: DateTime.UtcNow,
            AffectedRows: removed + distinctLinks.Count,
            Messages: messages,
            Summary: summary);

        log.LogInformation(
            "Repository relink finished. RepositoryId {RepositoryId}. Success {Success}. AffectedRows {AffectedRows}. Summary {Summary}",
            repositoryId,
            result.Success,
            result.AffectedRows,
            result.Summary);

        return result;
    }

    public async Task<IReadOnlyList<RepositoryRecoveryResultDto>> RunStartupHealthCheckAsync(
        CancellationToken ct = default)
    {
        var repositoryIds = await db.Set<Repository>()
            .Where(r => !r.IsDeleted)
            .Select(r => r.Id)
            .ToListAsync(ct);

        var results = new List<RepositoryRecoveryResultDto>(repositoryIds.Count);

        foreach (var repositoryId in repositoryIds)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var needsRelink = await NeedsRelinkAsync(repositoryId, ct);
                if (!needsRelink)
                {
                    var now = DateTime.UtcNow;
                    results.Add(new RepositoryRecoveryResultDto(
                        RepositoryId: repositoryId,
                        Action: "startup_health_check",
                        Success: true,
                        StartedAtUtc: now,
                        FinishedAtUtc: now,
                        AffectedRows: 0,
                        Messages: ["Repository link graph is healthy."],
                        Summary: "Healthy."));
                    continue;
                }

                var relink = await RelinkRepositoryAsync(repositoryId, ct);
                results.Add(new RepositoryRecoveryResultDto(
                    RepositoryId: repositoryId,
                    Action: "startup_health_check",
                    Success: relink.Success,
                    StartedAtUtc: relink.StartedAtUtc,
                    FinishedAtUtc: relink.FinishedAtUtc,
                    AffectedRows: relink.AffectedRows,
                    Messages: relink.Messages,
                    Summary: "Startup health-check triggered relink."));
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Startup health-check failed for repository {RepositoryId}", repositoryId);
                var now = DateTime.UtcNow;
                results.Add(new RepositoryRecoveryResultDto(
                    RepositoryId: repositoryId,
                    Action: "startup_health_check",
                    Success: false,
                    StartedAtUtc: now,
                    FinishedAtUtc: now,
                    AffectedRows: 0,
                    Messages: [ex.Message],
                    Summary: "Startup health-check failed."));
            }
        }

        return results;
    }

    private async Task<bool> NeedsRelinkAsync(int repositoryId, CancellationToken ct)
    {
        var snapshotIds = await db.Set<RepositorySnapshot>()
            .IgnoreQueryFilters()
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
            .Select(s => s.Id)
            .ToListAsync(ct);

        if (snapshotIds.Count == 0)
            return false;

        var expectedLinks = await db.Set<RepositorySnapshotEntry>()
            .IgnoreQueryFilters()
            .Where(e => e.RepositoryId == repositoryId
                        && !e.IsDeleted
                        && !e.IsDirectory
                        && snapshotIds.Contains(e.SnapshotId))
            .CountAsync(ct);

        var actualLinks = await db.Set<SnapshotFileLink>()
            .IgnoreQueryFilters()
            .Where(l => !l.IsDeleted && snapshotIds.Contains(l.SnapshotId))
            .CountAsync(ct);

        if (expectedLinks != actualLinks)
            return true;

        var hasBrokenReferences = await db.Set<SnapshotFileLink>()
            .IgnoreQueryFilters()
            .Where(l => !l.IsDeleted && snapshotIds.Contains(l.SnapshotId))
            .AnyAsync(l => l.FileIdentity.IsDeleted
                           || l.FileVersion.IsDeleted
                           || l.FileIdentity.RepositoryId != repositoryId
                           || l.FileVersion.FileIdentity.RepositoryId != repositoryId,
                ct);

        return hasBrokenReferences;
    }

    private static VersionState? SelectBestVersion(
        IReadOnlyList<VersionState> versions,
        DateTime snapshotCreatedAtUtc,
        string? targetHash,
        long targetSizeBytes)
    {
        var nonDeletionVersions = versions
            .Where(v => !v.IsDeletionMarker)
            .ToList();

        if (!string.IsNullOrWhiteSpace(targetHash))
        {
            var exactBefore = nonDeletionVersions
                .FirstOrDefault(v => v.CreatedAtUtc <= snapshotCreatedAtUtc
                                     && string.Equals(v.ContentHashSha256, targetHash, StringComparison.OrdinalIgnoreCase)
                                     && (targetSizeBytes <= 0 || v.SizeBytes == targetSizeBytes));

            if (exactBefore is not null)
                return exactBefore;

            var exactAny = nonDeletionVersions
                .FirstOrDefault(v => string.Equals(v.ContentHashSha256, targetHash, StringComparison.OrdinalIgnoreCase)
                                     && (targetSizeBytes <= 0 || v.SizeBytes == targetSizeBytes));

            if (exactAny is not null)
                return exactAny;
        }

        var beforeSnapshot = nonDeletionVersions
            .FirstOrDefault(v => v.CreatedAtUtc <= snapshotCreatedAtUtc);

        if (beforeSnapshot is not null)
            return beforeSnapshot;

        var newestNonDeletion = nonDeletionVersions.FirstOrDefault();
        if (newestNonDeletion is not null)
            return newestNonDeletion;

        return versions.FirstOrDefault();
    }

    private static string NormalizeRelativePath(string relativePath)
        => relativePath.Trim().Replace('\\', '/');

    private static string? ResolvePathWithinRoot(string rootPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(relativePath))
            return null;

        var root = Path.GetFullPath(rootPath.Trim());
        var normalizedRelativePath = NormalizeRelativePath(relativePath)
            .Replace('/', Path.DirectorySeparatorChar);
        var targetPath = Path.GetFullPath(Path.Combine(root, normalizedRelativePath));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return targetPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            ? targetPath
            : null;
    }

    private static void TrySetLastWriteTimeUtc(string path, DateTime lastWriteUtc)
    {
        if (lastWriteUtc == default || !File.Exists(path))
            return;

        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.SpecifyKind(lastWriteUtc, DateTimeKind.Utc));
        }
        catch
        {
            // Best effort: content restore must not fail just because metadata cannot be written.
        }
    }

    private sealed record MissingFileRestoreResult(
        int RestoredFiles,
        int SkippedFiles,
        string Summary)
    {
        public static readonly MissingFileRestoreResult None = new(0, 0, string.Empty);
    }

}
