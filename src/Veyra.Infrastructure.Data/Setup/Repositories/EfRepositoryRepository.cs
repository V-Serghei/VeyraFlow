using Microsoft.EntityFrameworkCore;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Cloud;
using Veyra.Application.DTOs.Repository.Core;
using Veyra.Application.DTOs.Repository.Retention;
using Veyra.Domain.Entities;
using Veyra.Domain.Entities.Watched;
using Veyra.Infrastructure.Data.Persistence;

namespace Veyra.Infrastructure.Data.Setup;

public sealed class EfRepositoryRepository(VeyraDbContext db) : IRepositoryRepository
{
    public async Task<int> CreateRepositoryAsync(string name, string? description, int directoryId, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();

        var now = DateTime.UtcNow;

        var existing = await db.Set<Repository>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.DirectoryId == directoryId, ct);

        if (existing is not null)
        {
            existing.IsDeleted = false;
            existing.DeletedAt = null;
            existing.Name = name;
            existing.Description = description;
            existing.SyncConflictStrategy = Repository.DefaultSyncConflictStrategy;
            existing.SyncRetryMaxAttempts = 5;
            existing.SyncRetryBaseDelaySeconds = 30;
            existing.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            return existing.Id;
        }

        var entity = new Repository
        {
            Name = name,
            Description = description,
            DirectoryId = directoryId,
            FileCount = 0,
            VersionCount = 0,
            TotalSizeBytes = 0,
            LastScannedAt = null,
            AutoCaptureFileVersions = false,
            ProtectCloudMetadata = true,
            RetentionEnabled = false,
            RetentionRunIntervalMinutes = 60,
            RetentionMaintenanceWindowStartHour = null,
            RetentionMaintenanceWindowEndHour = null,
            RetentionStorageMode = RepositoryRetentionStorageModes.Delete,
            RetentionAllowManualSnapshotCleanup = false,
            RetentionAutomaticCompactionEnabled = false,
            RetentionAutomaticCompactionWindowHours = null,
            RetentionLastRunAt = null,
            RetentionLastStatus = null,
            SyncConflictStrategy = Repository.DefaultSyncConflictStrategy,
            SyncRetryMaxAttempts = 5,
            SyncRetryBaseDelaySeconds = 30,
            CloudLastSyncedAt = null,
            CloudLastLocalSnapshotId = null,
            CloudLastRemoteSnapshotId = null,
            CloudSyncLastStatus = null,
            CloudSyncLastError = null,
            CreatedAt = now,
            UpdatedAt = now,
            IsDeleted = false,
            DeletedAt = null
        };

        db.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity.Id;
    }

    public async Task UpdateRepositoryAsync(int id, string name, string? description, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();

        var entity = await db.Set<Repository>()
            .FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted, ct);

        if (entity is null)
            return;

        entity.Name = name;
        entity.Description = description;
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateRepositoryAsync(
        int id,
        string name,
        string? description,
        bool autoCaptureFileVersions,
        bool protectCloudMetadata,
        IReadOnlyCollection<string> excludedPatterns,
        RepositoryRetentionPolicyDto retentionPolicy,
        string syncConflictStrategy,
        int syncRetryMaxAttempts,
        int syncRetryBaseDelaySeconds,
        CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();

        var entity = await db.Set<Repository>()
            .FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted, ct);

        if (entity is null)
            return;

        entity.Name = name;
        entity.Description = description;
        entity.AutoCaptureFileVersions = autoCaptureFileVersions;
        entity.ProtectCloudMetadata = protectCloudMetadata;
        entity.ExclusionPatternsJson = SerializeExclusionPatterns(excludedPatterns);
        entity.RetentionPolicyOverrideEnabled = retentionPolicy.HasLocalOverride;
        entity.RetentionEnabled = retentionPolicy.Enabled;
        entity.RetentionMaxAgeDays = NormalizePositive(retentionPolicy.MaxAgeDays);
        entity.RetentionMaxSnapshots = NormalizePositive(retentionPolicy.MaxSnapshots);
        entity.RetentionMaxTotalSizeBytes = NormalizePositive(retentionPolicy.MaxTotalSizeBytes);
        entity.RetentionTriggerFilter = SerializeTriggerFilters(retentionPolicy.TriggerFilters);
        entity.RetentionRunIntervalMinutes = Math.Clamp(retentionPolicy.RunIntervalMinutes, 5, 7 * 24 * 60);
        entity.RetentionMaintenanceWindowStartHour = NormalizeHour(retentionPolicy.MaintenanceWindowStartHour);
        entity.RetentionMaintenanceWindowEndHour = NormalizeHour(retentionPolicy.MaintenanceWindowEndHour);
        entity.RetentionStorageMode = RepositoryRetentionStorageModes.Normalize(retentionPolicy.StorageMode);
        entity.RetentionAllowManualSnapshotCleanup = retentionPolicy.AllowManualSnapshotCleanup;
        entity.RetentionAutomaticCompactionEnabled = retentionPolicy.AutomaticCompactionEnabled;
        entity.RetentionAutomaticCompactionWindowHours = NormalizePositive(retentionPolicy.AutomaticCompactionWindowHours);
        entity.SyncConflictStrategy = RepositorySyncConflictStrategies.Normalize(syncConflictStrategy);
        entity.SyncRetryMaxAttempts = Math.Clamp(syncRetryMaxAttempts, 1, 20);
        entity.SyncRetryBaseDelaySeconds = Math.Clamp(syncRetryBaseDelaySeconds, 5, 600);
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteRepositoryAsync(int id, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();

        var now = DateTime.UtcNow;
        var repo = await db.Set<Repository>()
            .IgnoreQueryFilters()
            .Where(r => r.Id == id && !r.IsDeleted)
            .Select(r => new { r.Id, r.DirectoryId })
            .FirstOrDefaultAsync(ct);

        if (repo is null)
            return;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await db.Set<Repository>()
            .IgnoreQueryFilters()
            .Where(r => r.Id == repo.Id && !r.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.IsDeleted, true)
                .SetProperty(r => r.DeletedAt, now)
                .SetProperty(r => r.UpdatedAt, now), ct);

        await db.Set<WatchedDirectory>()
            .IgnoreQueryFilters()
            .Where(d => d.Id == repo.DirectoryId && !d.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.IsDeleted, true)
                .SetProperty(d => d.DeletedAt, now)
                .SetProperty(d => d.IsEnabled, false)
                .SetProperty(d => d.UpdatedAt, now), ct);

        await db.Set<WatchedDirectoryFormat>()
            .IgnoreQueryFilters()
            .Where(l => l.DirectoryId == repo.DirectoryId && !l.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.IsDeleted, true)
                .SetProperty(l => l.DeletedAt, now)
                .SetProperty(l => l.UpdatedAt, now), ct);

        await db.Set<FileIdentity>()
            .IgnoreQueryFilters()
            .Where(i => i.RepositoryId == repo.Id && !i.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.IsDeleted, true)
                .SetProperty(i => i.DeletedAt, now)
                .SetProperty(i => i.UpdatedAt, now), ct);

        await db.Set<FileVersion>()
            .IgnoreQueryFilters()
            .Where(v => !v.IsDeleted && v.FileIdentity.RepositoryId == repo.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.IsDeleted, true)
                .SetProperty(v => v.DeletedAt, now), ct);

        await db.Set<FileVersionBlock>()
            .IgnoreQueryFilters()
            .Where(b => !b.IsDeleted && b.FileVersion.FileIdentity.RepositoryId == repo.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.IsDeleted, true)
                .SetProperty(b => b.DeletedAt, now), ct);

        await db.Set<RepositorySnapshot>()
            .IgnoreQueryFilters()
            .Where(s => s.RepositoryId == repo.Id && !s.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, true)
                .SetProperty(x => x.DeletedAt, now), ct);

        await db.Set<RepositorySnapshotEntry>()
            .IgnoreQueryFilters()
            .Where(e => e.RepositoryId == repo.Id && !e.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, true)
                .SetProperty(x => x.DeletedAt, now), ct);

        await db.Set<SnapshotFileLink>()
            .IgnoreQueryFilters()
            .Where(l => !l.IsDeleted && l.Snapshot.RepositoryId == repo.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, true)
                .SetProperty(x => x.DeletedAt, now), ct);

        await db.Set<FileVersionTextDiff>()
            .IgnoreQueryFilters()
            .Where(d => !d.IsDeleted
                        && (db.Set<FileVersion>().IgnoreQueryFilters().Any(v => v.Id == d.LeftFileVersionId && v.FileIdentity.RepositoryId == repo.Id)
                            || db.Set<FileVersion>().IgnoreQueryFilters().Any(v => v.Id == d.RightFileVersionId && v.FileIdentity.RepositoryId == repo.Id)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, true)
                .SetProperty(x => x.DeletedAt, now)
                .SetProperty(x => x.UpdatedAt, now), ct);

        await db.Set<FileVersionTextDiffHunk>()
            .IgnoreQueryFilters()
            .Where(h => !h.IsDeleted && h.Diff.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, true)
                .SetProperty(x => x.DeletedAt, now), ct);

        await db.Set<FileVersionTextDiffLine>()
            .IgnoreQueryFilters()
            .Where(l => !l.IsDeleted && l.Diff.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, true)
                .SetProperty(x => x.DeletedAt, now), ct);

        await tx.CommitAsync(ct);
    }

    public async Task RestoreRepositoryAsync(int id, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();

        var now = DateTime.UtcNow;
        var repo = await db.Set<Repository>()
            .IgnoreQueryFilters()
            .Where(r => r.Id == id && r.IsDeleted)
            .Select(r => new { r.Id, r.DirectoryId })
            .FirstOrDefaultAsync(ct);

        if (repo is null)
            return;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await db.Set<Repository>()
            .IgnoreQueryFilters()
            .Where(r => r.Id == repo.Id && r.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.IsDeleted, false)
                .SetProperty(r => r.DeletedAt, (DateTime?)null)
                .SetProperty(r => r.UpdatedAt, now), ct);

        await db.Set<WatchedDirectory>()
            .IgnoreQueryFilters()
            .Where(d => d.Id == repo.DirectoryId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.IsDeleted, false)
                .SetProperty(d => d.DeletedAt, (DateTime?)null)
                .SetProperty(d => d.IsEnabled, true)
                .SetProperty(d => d.UpdatedAt, now), ct);

        await db.Set<WatchedDirectoryFormat>()
            .IgnoreQueryFilters()
            .Where(l => l.DirectoryId == repo.DirectoryId && l.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.IsDeleted, false)
                .SetProperty(l => l.DeletedAt, (DateTime?)null)
                .SetProperty(l => l.UpdatedAt, now), ct);

        await db.Set<FileIdentity>()
            .IgnoreQueryFilters()
            .Where(i => i.RepositoryId == repo.Id && i.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.IsDeleted, false)
                .SetProperty(i => i.DeletedAt, (DateTime?)null)
                .SetProperty(i => i.UpdatedAt, now), ct);

        await db.Set<FileVersion>()
            .IgnoreQueryFilters()
            .Where(v => v.IsDeleted && v.FileIdentity.RepositoryId == repo.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.IsDeleted, false)
                .SetProperty(v => v.DeletedAt, (DateTime?)null), ct);

        await db.Set<FileVersionBlock>()
            .IgnoreQueryFilters()
            .Where(b => b.IsDeleted && b.FileVersion.FileIdentity.RepositoryId == repo.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.IsDeleted, false)
                .SetProperty(b => b.DeletedAt, (DateTime?)null), ct);

        await db.Set<RepositorySnapshot>()
            .IgnoreQueryFilters()
            .Where(s => s.RepositoryId == repo.Id && s.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, false)
                .SetProperty(x => x.DeletedAt, (DateTime?)null), ct);

        await db.Set<RepositorySnapshotEntry>()
            .IgnoreQueryFilters()
            .Where(e => e.RepositoryId == repo.Id && e.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, false)
                .SetProperty(x => x.DeletedAt, (DateTime?)null), ct);

        await db.Set<SnapshotFileLink>()
            .IgnoreQueryFilters()
            .Where(l => l.IsDeleted && l.Snapshot.RepositoryId == repo.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, false)
                .SetProperty(x => x.DeletedAt, (DateTime?)null), ct);

        await db.Set<FileVersionTextDiff>()
            .IgnoreQueryFilters()
            .Where(d => d.IsDeleted
                        && (db.Set<FileVersion>().IgnoreQueryFilters().Any(v => v.Id == d.LeftFileVersionId && v.FileIdentity.RepositoryId == repo.Id)
                            || db.Set<FileVersion>().IgnoreQueryFilters().Any(v => v.Id == d.RightFileVersionId && v.FileIdentity.RepositoryId == repo.Id)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, false)
                .SetProperty(x => x.DeletedAt, (DateTime?)null)
                .SetProperty(x => x.UpdatedAt, now), ct);

        await db.Set<FileVersionTextDiffHunk>()
            .IgnoreQueryFilters()
            .Where(h => h.IsDeleted && !h.Diff.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, false)
                .SetProperty(x => x.DeletedAt, (DateTime?)null), ct);

        await db.Set<FileVersionTextDiffLine>()
            .IgnoreQueryFilters()
            .Where(l => l.IsDeleted && !l.Diff.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, false)
                .SetProperty(x => x.DeletedAt, (DateTime?)null), ct);

        await tx.CommitAsync(ct);
    }

    public async Task<RepositoryDto?> GetRepositoryByIdAsync(int id, CancellationToken ct = default)
    {
        var repo = await db.Set<Repository>()
            .AsNoTracking()
            .Include(r => r.Directory)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        if (repo is null)
            return null;

        var formats = await db.Set<WatchedDirectoryFormat>()
            .Where(l => !l.IsDeleted && l.DirectoryId == repo.DirectoryId && !l.Format.IsDeleted)
            .Select(l => l.Format.Pattern)
            .OrderBy(p => p)
            .ToListAsync(ct);

        var queueStats = await db.Set<RepositorySyncQueueItem>()
            .Where(q => q.RepositoryId == repo.Id)
            .GroupBy(q => q.RepositoryId)
            .Select(g => new
            {
                Pending = g.Count(q => q.Status == RepositorySyncQueueItem.StatusPending
                    || q.Status == RepositorySyncQueueItem.StatusRunning),
                Running = g.Count(q => q.Status == RepositorySyncQueueItem.StatusRunning),
                Retry = g.Count(q => q.Status == RepositorySyncQueueItem.StatusRetry),
                Conflict = g.Count(q => q.Status == RepositorySyncQueueItem.StatusConflict
                    || q.Status == RepositorySyncQueueItem.StatusFailed
                    || q.Status == RepositorySyncQueueItem.StatusDeadLetter),
                DeadLetter = g.Count(q => q.Status == RepositorySyncQueueItem.StatusDeadLetter),
                Failed = g.Count(q => q.Status == RepositorySyncQueueItem.StatusFailed),
                Completed = g.Count(q => q.Status == RepositorySyncQueueItem.StatusCompleted)
            })
            .FirstOrDefaultAsync(ct);

        var runningProgress = await db.Set<RepositorySyncQueueItem>()
            .Where(q => q.RepositoryId == repo.Id
                        && q.OperationType == RepositorySyncQueueItem.OperationPushSnapshot
                        && q.Status == RepositorySyncQueueItem.StatusRunning
                        && q.UploadCheckpointTotal > 0)
            .OrderByDescending(q => q.UpdatedAt)
            .Select(q => new
            {
                q.UploadCheckpointNextIndex,
                q.UploadCheckpointTotal,
                q.CreatedAt,
                q.UpdatedAt
            })
            .FirstOrDefaultAsync(ct);

        var changedVersionCounts = await BuildChangedVersionCountsByRepositoryAsync([repo.Id], ct);

        return new RepositoryDto(
            repo.Id,
            repo.Name,
            repo.Description,
            repo.DirectoryId,
            repo.Directory.Path,
            formats,
            ParseExclusionPatterns(repo.ExclusionPatternsJson),
            repo.IsDeleted,
            repo.FileCount,
            repo.VersionCount,
            repo.TotalSizeBytes,
            repo.LastScannedAt,
            MapRetentionPolicy(repo),
            MapCloudSyncStatus(
                repo,
                queueStats?.Pending ?? 0,
                queueStats?.Conflict ?? 0,
                queueStats?.Running ?? 0,
                queueStats?.Retry ?? 0,
                queueStats?.DeadLetter ?? 0,
                queueStats?.Failed ?? 0,
                queueStats?.Completed ?? 0,
                runningProgress?.UploadCheckpointNextIndex ?? 0,
                runningProgress?.UploadCheckpointTotal ?? 0,
                runningProgress?.CreatedAt,
                runningProgress?.UpdatedAt),
            repo.AutoCaptureFileVersions,
            repo.ProtectCloudMetadata,
            changedVersionCounts.GetValueOrDefault(repo.Id));
    }

    public async Task<IReadOnlyList<RepositoryDto>> GetAllRepositoriesAsync(CancellationToken ct = default)
    {
        var repos = await db.Set<Repository>()
            .AsNoTracking()
            .Include(r => r.Directory)
            .Where(r => !r.IsDeleted && !r.Directory.IsDeleted)
            .OrderBy(r => r.Name)
            .ToListAsync(ct);

        var dirIds = repos.Select(r => r.DirectoryId).ToList();

        var links = await db.Set<WatchedDirectoryFormat>()
            .Where(l => !l.IsDeleted && dirIds.Contains(l.DirectoryId) && !l.Format.IsDeleted)
            .Select(l => new { l.DirectoryId, l.Format.Pattern })
            .ToListAsync(ct);

        var formatsByDir = links
            .GroupBy(l => l.DirectoryId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Pattern).OrderBy(p => p).ToList());

        var repoIds = repos.Select(r => r.Id).ToList();
        var changedVersionCounts = await BuildChangedVersionCountsByRepositoryAsync(repoIds, ct);
        var queueStatsByRepo = repoIds.Count == 0
            ? new Dictionary<int, (int Pending, int Conflict, int Running, int Retry, int DeadLetter, int Failed, int Completed)>()
            : await db.Set<RepositorySyncQueueItem>()
                .Where(q => repoIds.Contains(q.RepositoryId))
                .GroupBy(q => q.RepositoryId)
                .Select(g => new
                {
                    RepositoryId = g.Key,
                    Pending = g.Count(q => q.Status == RepositorySyncQueueItem.StatusPending
                        || q.Status == RepositorySyncQueueItem.StatusRunning),
                    Running = g.Count(q => q.Status == RepositorySyncQueueItem.StatusRunning),
                    Retry = g.Count(q => q.Status == RepositorySyncQueueItem.StatusRetry),
                    Conflict = g.Count(q => q.Status == RepositorySyncQueueItem.StatusConflict
                        || q.Status == RepositorySyncQueueItem.StatusFailed
                        || q.Status == RepositorySyncQueueItem.StatusDeadLetter),
                    DeadLetter = g.Count(q => q.Status == RepositorySyncQueueItem.StatusDeadLetter),
                    Failed = g.Count(q => q.Status == RepositorySyncQueueItem.StatusFailed),
                    Completed = g.Count(q => q.Status == RepositorySyncQueueItem.StatusCompleted)
                })
                .ToDictionaryAsync(
                    x => x.RepositoryId,
                    x => (x.Pending, x.Conflict, x.Running, x.Retry, x.DeadLetter, x.Failed, x.Completed),
                    ct);

        var runningProgressByRepo = repoIds.Count == 0
            ? new Dictionary<int, (int Current, int Total, DateTime CreatedAt, DateTime UpdatedAt)>()
            : (await db.Set<RepositorySyncQueueItem>()
                    .Where(q => repoIds.Contains(q.RepositoryId)
                                && q.OperationType == RepositorySyncQueueItem.OperationPushSnapshot
                                && q.Status == RepositorySyncQueueItem.StatusRunning
                                && q.UploadCheckpointTotal > 0)
                    .OrderByDescending(q => q.UpdatedAt)
                    .Select(q => new
                    {
                        q.RepositoryId,
                        q.UploadCheckpointNextIndex,
                        q.UploadCheckpointTotal,
                        q.CreatedAt,
                        q.UpdatedAt
                    })
                    .ToListAsync(ct))
                .GroupBy(x => x.RepositoryId)
                .ToDictionary(
                    g => g.Key,
                    g =>
                    {
                        var first = g.First();
                        return (
                            Current: first.UploadCheckpointNextIndex,
                            Total: first.UploadCheckpointTotal,
                            CreatedAt: first.CreatedAt,
                            UpdatedAt: first.UpdatedAt);
                    });

        return repos.Select(r =>
        {
            if (!queueStatsByRepo.TryGetValue(r.Id, out var queue))
                queue = (0, 0, 0, 0, 0, 0, 0);

            runningProgressByRepo.TryGetValue(r.Id, out var progress);

            var pending = queue.Pending;
            var conflict = queue.Conflict;

            return new RepositoryDto(
                r.Id,
                r.Name,
                r.Description,
                r.DirectoryId,
                r.Directory.Path,
                formatsByDir.GetValueOrDefault(r.DirectoryId, Array.Empty<string>()),
                ParseExclusionPatterns(r.ExclusionPatternsJson),
                r.IsDeleted,
                r.FileCount,
                r.VersionCount,
                r.TotalSizeBytes,
                r.LastScannedAt,
                MapRetentionPolicy(r),
                MapCloudSyncStatus(
                    r,
                    pending,
                    conflict,
                    queue.Running,
                    queue.Retry,
                    queue.DeadLetter,
                    queue.Failed,
                    queue.Completed,
                    progress.Current,
                    progress.Total,
                    progress == default ? null : progress.CreatedAt,
                    progress == default ? null : progress.UpdatedAt),
                r.AutoCaptureFileVersions,
                r.ProtectCloudMetadata,
                changedVersionCounts.GetValueOrDefault(r.Id));
        }).ToList();
    }

    private async Task<Dictionary<int, int>> BuildChangedVersionCountsByRepositoryAsync(
        IReadOnlyCollection<int> repositoryIds,
        CancellationToken ct)
    {
        var normalizedRepositoryIds = repositoryIds
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        if (normalizedRepositoryIds.Count == 0)
            return [];

        var snapshots = await db.Set<RepositorySnapshot>()
            .AsNoTracking()
            .Where(s => normalizedRepositoryIds.Contains(s.RepositoryId) && !s.IsDeleted)
            .Where(s => db.Set<SnapshotFileLink>().Any(l => l.SnapshotId == s.Id && !l.IsDeleted))
            .Select(s => new
            {
                s.Id,
                s.RepositoryId,
                s.CreatedAt
            })
            .ToListAsync(ct);

        var baselineSnapshotsByRepository = snapshots
            .GroupBy(s => s.RepositoryId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(s => s.CreatedAt).ThenBy(s => s.Id).First());

        if (baselineSnapshotsByRepository.Count == 0)
            return normalizedRepositoryIds.ToDictionary(id => id, _ => 0);

        var baselineSnapshotIds = baselineSnapshotsByRepository.Values
            .Select(s => s.Id)
            .ToList();

        var baselineVersionIdsByRepository = (await db.Set<SnapshotFileLink>()
                .AsNoTracking()
                .Where(l => baselineSnapshotIds.Contains(l.SnapshotId)
                            && !l.IsDeleted
                            && !l.FileVersion.IsDeleted
                            && !l.Snapshot.IsDeleted)
                .Select(l => new
                {
                    l.Snapshot.RepositoryId,
                    l.FileVersionId
                })
                .ToListAsync(ct))
            .GroupBy(x => x.RepositoryId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.FileVersionId).ToHashSet());

        var changedVersionRows = await db.Set<SnapshotFileLink>()
            .AsNoTracking()
            .Where(l => normalizedRepositoryIds.Contains(l.Snapshot.RepositoryId)
                        && !l.IsDeleted
                        && !l.FileVersion.IsDeleted
                        && !l.Snapshot.IsDeleted)
            .Select(l => new
            {
                l.Snapshot.RepositoryId,
                SnapshotId = l.SnapshotId,
                SnapshotCreatedAt = l.Snapshot.CreatedAt,
                l.FileVersionId
            })
            .ToListAsync(ct);

        var result = normalizedRepositoryIds.ToDictionary(id => id, _ => 0);

        foreach (var group in changedVersionRows.GroupBy(x => x.RepositoryId))
        {
            if (!baselineSnapshotsByRepository.TryGetValue(group.Key, out var baseline))
                continue;

            baselineVersionIdsByRepository.TryGetValue(group.Key, out var baselineVersionIds);
            baselineVersionIds ??= [];

            result[group.Key] = group
                .Where(x => x.SnapshotCreatedAt > baseline.CreatedAt
                            || (x.SnapshotCreatedAt == baseline.CreatedAt && x.SnapshotId > baseline.Id))
                .Select(x => x.FileVersionId)
                .Distinct()
                .Count(versionId => !baselineVersionIds.Contains(versionId));
        }

        return result;
    }

    public async Task EnsureRepositoriesForAllDirectoriesAsync(CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();

        var now = DateTime.UtcNow;

        var activeDirs = await db.Set<WatchedDirectory>()
            .Where(d => !d.IsDeleted && d.IsEnabled)
            .ToListAsync(ct);

        var existingRepos = await db.Set<Repository>()
            .IgnoreQueryFilters()
            .ToListAsync(ct);

        var repoByDirId = existingRepos.ToDictionary(r => r.DirectoryId);

        foreach (var dir in activeDirs)
        {
            if (repoByDirId.TryGetValue(dir.Id, out var existing))
            {
                if (existing.IsDeleted)
                {
                    existing.IsDeleted = false;
                    existing.DeletedAt = null;
                    existing.UpdatedAt = now;
                }

                if (string.IsNullOrWhiteSpace(existing.SyncConflictStrategy))
                    existing.SyncConflictStrategy = Repository.DefaultSyncConflictStrategy;

                if (existing.SyncRetryMaxAttempts <= 0)
                    existing.SyncRetryMaxAttempts = 5;

                if (existing.SyncRetryBaseDelaySeconds <= 0)
                    existing.SyncRetryBaseDelaySeconds = 30;

            }
            else
            {
                var folderName = Path.GetFileName(dir.Path.TrimEnd('\\', '/'));
                if (string.IsNullOrWhiteSpace(folderName))
                    folderName = dir.Path;

                db.Add(new Repository
                {
                    Name = folderName,
                    DirectoryId = dir.Id,
                    FileCount = 0,
                    VersionCount = 0,
                    TotalSizeBytes = 0,
                    LastScannedAt = null,
                    AutoCaptureFileVersions = false,
                    ProtectCloudMetadata = true,
                    RetentionEnabled = false,
                    RetentionRunIntervalMinutes = 60,
            RetentionMaintenanceWindowStartHour = null,
            RetentionMaintenanceWindowEndHour = null,
            RetentionStorageMode = RepositoryRetentionStorageModes.Delete,
            RetentionAllowManualSnapshotCleanup = false,
                    RetentionAutomaticCompactionEnabled = false,
                    RetentionAutomaticCompactionWindowHours = null,
                    RetentionLastRunAt = null,
                    RetentionLastStatus = null,
                    SyncConflictStrategy = Repository.DefaultSyncConflictStrategy,
                    SyncRetryMaxAttempts = 5,
                    SyncRetryBaseDelaySeconds = 30,
                    CloudLastSyncedAt = null,
                    CloudLastLocalSnapshotId = null,
                    CloudLastRemoteSnapshotId = null,
                    CloudSyncLastStatus = null,
                    CloudSyncLastError = null,
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsDeleted = false,
                    DeletedAt = null
                });
            }
        }

        foreach (var repo in existingRepos)
        {
            if (!repo.IsDeleted && activeDirs.All(d => d.Id != repo.DirectoryId))
            {
                repo.IsDeleted = true;
                repo.DeletedAt = now;
                repo.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static RepositoryRetentionPolicyDto MapRetentionPolicy(Repository repository)
    {
        return new RepositoryRetentionPolicyDto(
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
            repository.RetentionAutomaticCompactionWindowHours,
            repository.RetentionPolicyOverrideEnabled,
            repository.RetentionPolicyOverrideEnabled
                ? RepositoryRetentionPolicySources.Repository
                : RepositoryRetentionPolicySources.None,
            repository.RetentionPolicyOverrideEnabled ? repository.Id : null);
    }

    private static RepositoryCloudSyncStatusDto MapCloudSyncStatus(
        Repository repository,
        int pendingCount,
        int conflictCount,
        int runningCount,
        int retryCount,
        int deadLetterCount,
        int failedCount,
        int completedCount,
        int uploadProgressCurrent = 0,
        int uploadProgressTotal = 0,
        DateTime? uploadProgressStartedAtUtc = null,
        DateTime? uploadProgressUpdatedAtUtc = null)
    {
        return new RepositoryCloudSyncStatusDto(
            ConflictStrategy: RepositorySyncConflictStrategies.Normalize(repository.SyncConflictStrategy),
            RetryMaxAttempts: Math.Clamp(repository.SyncRetryMaxAttempts, 1, 20),
            RetryBaseDelaySeconds: Math.Clamp(repository.SyncRetryBaseDelaySeconds, 5, 600),
            LastSyncedAtUtc: repository.CloudLastSyncedAt,
            LastLocalSnapshotId: repository.CloudLastLocalSnapshotId,
            LastRemoteSnapshotId: repository.CloudLastRemoteSnapshotId,
            LastStatus: repository.CloudSyncLastStatus,
            LastError: repository.CloudSyncLastError,
            PendingQueueCount: pendingCount,
            ConflictQueueCount: conflictCount,
            RunningQueueCount: runningCount,
            RetryQueueCount: retryCount,
            DeadLetterQueueCount: deadLetterCount,
            FailedQueueCount: failedCount,
            CompletedQueueCount: completedCount,
            UploadProgressCurrent: Math.Max(0, uploadProgressCurrent),
            UploadProgressTotal: Math.Max(0, uploadProgressTotal),
            UploadProgressStartedAtUtc: uploadProgressStartedAtUtc,
            UploadProgressUpdatedAtUtc: uploadProgressUpdatedAtUtc);
    }

    private static IReadOnlyList<string> ParseTriggerFilters(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return Array.Empty<string>();

        return csv
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? SerializeTriggerFilters(IReadOnlyList<string> triggerFilters)
    {
        if (triggerFilters.Count == 0)
            return null;

        var normalized = triggerFilters
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return normalized.Count == 0 ? null : string.Join(',', normalized);
    }

    private static int? NormalizePositive(int? value)
        => value is > 0 ? value : null;

    private static long? NormalizePositive(long? value)
        => value is > 0 ? value : null;

    private static int? NormalizeHour(int? value)
        => value is >= 0 and <= 23 ? value : null;

    private static IReadOnlyList<string> ParseExclusionPatterns(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

        return value
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(v => v.Replace('\\', '/').Trim('/'))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? SerializeExclusionPatterns(IReadOnlyCollection<string> patterns)
    {
        var normalized = patterns
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim().Replace('\\', '/'))
            .Select(v => v.Trim('/'))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return normalized.Count == 0 ? null : string.Join(';', normalized);
    }
}
