using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.DTOs;
using Veyra.Domain.Entities;
using Veyra.Infrastructure.Data.Persistence;

namespace Veyra.Infrastructure.Data.Setup;

public sealed class EfRepositoryRecoveryService(
    VeyraDbContext db,
    IRepositoryIntegrityService integrity,
    IRepositoryScanner scanner,
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

        var relink = await RelinkRepositoryAsync(repositoryId, ct);
        messages.AddRange(relink.Messages);

        var success = integrityRun.UnresolvedIssueCount == 0 && relink.Success;
        var affectedRows = integrityRun.RepairedBlockCount + Math.Max(0, relink.AffectedRows);
        var summary =
            $"Repair {(success ? "completed" : "finished with warnings")}. RepairedBlocks={integrityRun.RepairedBlockCount}, Unresolved={integrityRun.UnresolvedIssueCount}, RelinkAffected={relink.AffectedRows}.";

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

        if (links.Count > 0)
            db.AddRange(links);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var messages = new List<string>
        {
            $"Removed stale links: {removed}.",
            $"Inserted links: {links.Count}."
        };

        if (missingIdentities > 0)
            messages.Add($"Skipped entries without identity: {missingIdentities}.");

        if (missingVersions > 0)
            messages.Add($"Skipped entries without version: {missingVersions}.");

        var success = missingIdentities == 0 && missingVersions == 0;
        var summary = success
            ? $"Relink completed. Removed={removed}, Inserted={links.Count}."
            : $"Relink completed with warnings. Removed={removed}, Inserted={links.Count}, MissingIdentities={missingIdentities}, MissingVersions={missingVersions}.";

        var result = new RepositoryRecoveryResultDto(
            RepositoryId: repositoryId,
            Action: "relink",
            Success: success,
            StartedAtUtc: startedAt,
            FinishedAtUtc: DateTime.UtcNow,
            AffectedRows: removed + links.Count,
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

    private sealed record SnapshotState(long SnapshotId, DateTime CreatedAtUtc);

    private sealed record SnapshotEntryState(
        long SnapshotId,
        string RelativePath,
        string? ContentHashSha256,
        long SizeBytes);

    private sealed record IdentityState(long FileIdentityId, string RelativePath);

    private sealed record VersionState(
        long FileVersionId,
        long FileIdentityId,
        string ContentHashSha256,
        long SizeBytes,
        DateTime CreatedAtUtc,
        bool IsDeletionMarker);
}

