using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Abstractions.Sync;
using Veyra.Application.DTOs;
using Veyra.Domain.Entities;
using Veyra.Infrastructure.Data.Persistence;

namespace Veyra.Infrastructure.Data.Sync;

public sealed class CloudRepositoryManagementService(
    VeyraDbContext db,
    ICloudSyncService cloudSync,
    IAuthService auth,
    IUserProfileRepository userProfiles,
    IAccessTokenPolicyService tokenPolicy,
    ICloudAvailabilityService cloudAvailability,
    CloudRepositoryOperationTracker operations,
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<CloudRepositoryManagementService> log) : ICloudRepositoryManagementService
{
    private const string DeleteConfirmationPrefix = "DELETE ";
    private const string RemoveHistoryConfirmation = "I understand that deleted cloud history cannot be restored.";

    public async Task<CloudRepositoryManagerOverviewDto> GetOverviewAsync(CancellationToken ct = default)
    {
        var operationSnapshot = operations.Snapshot();
        var profile = await userProfiles.GetActiveProfileAsync(ct);
        var connectionState = cloudAvailability.Snapshot.State.ToString();

        var activeRepos = await db.Set<Repository>()
            .AsNoTracking()
            .Include(r => r.Directory)
            .Where(r => !r.IsDeleted && !r.Directory.IsDeleted)
            .ToListAsync(ct);

        var allRepos = await db.Set<Repository>()
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Include(r => r.Directory)
            .ToListAsync(ct);

        var queueStats = await db.Set<RepositorySyncQueueItem>()
            .AsNoTracking()
            .GroupBy(q => 1)
            .Select(g => new
            {
                Pending = g.Count(q => q.Status == RepositorySyncQueueItem.StatusPending || q.Status == RepositorySyncQueueItem.StatusRunning || q.Status == RepositorySyncQueueItem.StatusRetry),
                Failed = g.Count(q => q.Status == RepositorySyncQueueItem.StatusFailed || q.Status == RepositorySyncQueueItem.StatusDeadLetter)
            })
            .FirstOrDefaultAsync(ct);

        IReadOnlyList<CloudRepositoryHeaderDto> remoteRepositories = [];
        CloudStorageMetricsDto? metrics = null;
        if (profile is not null && !cloudAvailability.ShouldSkipCloudOperation(out _))
        {
            var token = await TryGetAccessTokenAsync(profile, ct);
            if (!string.IsNullOrWhiteSpace(token))
            {
                remoteRepositories = await cloudSync.GetRepositoriesAsync(token, ct);
                try
                {
                    metrics = await cloudSync.GetStorageMetricsAsync(token, ct);
                }
                catch (Exception ex)
                {
                    log.LogDebug(ex, "Cloud manager overview could not load storage metrics.");
                }
            }
        }

        var localById = activeRepos
            .GroupBy(GetCloudRepositoryId)
            .ToDictionary(g => g.Key, g => g.First());
        var allById = allRepos
            .GroupBy(GetCloudRepositoryId)
            .ToDictionary(g => g.Key, g => g.First());
        var repositories = remoteRepositories
            .Select(remote => BuildRepositoryRow(remote, localById, allById))
            .OrderBy(row => row.HasLocalLink)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CloudRepositoryManagerOverviewDto(
            profile?.Username,
            profile?.Email,
            connectionState,
            activeRepos.Select(r => r.CloudLastSyncedAt).Where(x => x.HasValue).OrderByDescending(x => x).FirstOrDefault(),
            metrics?.Summary.PhysicalPayloadBytes ?? 0,
            remoteRepositories.Count,
            remoteRepositories.Count(r => r.LatestSnapshotId.HasValue),
            (int)Math.Clamp(metrics?.Summary.LogicalBlockCount ?? 0, 0, int.MaxValue),
            (queueStats?.Pending ?? 0) + operationSnapshot.Count(o => o.Status is "running" or "queued"),
            (queueStats?.Failed ?? 0) + operationSnapshot.Count(o => o.Status == "failed"),
            repositories,
            operationSnapshot);
    }

    public async Task<CloudRepositoryRestorePlanDto> BuildRestorePlanAsync(
        CloudRepositoryRestoreOptionsDto options,
        CancellationToken ct = default)
    {
        var profile = await userProfiles.GetActiveProfileAsync(ct)
                      ?? throw new InvalidOperationException("Cloud account is not signed in.");
        var token = await TryGetAccessTokenAsync(profile, ct)
                    ?? throw new InvalidOperationException("Cloud access token is unavailable.");

        IReadOnlyList<CloudSnapshotPackageDto> packages = options.RestoreFullHistory
            ? (await cloudSync.GetRepositorySnapshotsAsync(token, options.CloudRepositoryId, ct))
                .OrderBy(p => p.Snapshot.CreatedAtUtc)
                .ThenBy(p => p.Snapshot.Id)
                .ToList()
            : [];
        CloudSnapshotPackageDto? package = options.RestoreFullHistory
            ? packages.LastOrDefault()
            : await cloudSync.GetLatestSnapshotAsync(token, options.CloudRepositoryId, ct);

        package ??= packages.LastOrDefault();
        if (package is null)
            throw new InvalidOperationException("Cloud snapshot package was not found.");

        IEnumerable<CloudSnapshotPackageDto> blockSource = options.RestoreFullHistory ? packages : new[] { package };
        var blocks = blockSource
            .SelectMany(p => p.FileVersions)
            .Where(v => !v.IsDeletionMarker)
            .SelectMany(v => v.Blocks)
            .Where(b => !string.IsNullOrWhiteSpace(b.BlockHash))
            .GroupBy(b => b.BlockHash, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var localCount = blocks.Count(b => LocalBlockExists(b.BlockHash));
        var bytesToDownload = blocks
            .Where(b => !LocalBlockExists(b.BlockHash))
            .Sum(b => Math.Max(0L, b.StoredSizeBytes));

        var originalPath = await ResolveKnownOriginalPathAsync(options.CloudRepositoryId, ct);
        var targetPath = ResolveTargetPath(options, package.Repository.Name, profile.Username, originalPath);
        var conflicts = await BuildLocalCloudConflictsAsync(package, targetPath, ct);
        var targetExists = Directory.Exists(targetPath);

        return new CloudRepositoryRestorePlanDto(
            options.CloudRepositoryId,
            package.Repository.Name,
            targetPath,
            Math.Max(1, options.RestoreFullHistory ? packages.Count : 1),
            blocks.Count,
            localCount,
            blocks.Count - localCount,
            bytesToDownload,
            targetExists,
            conflicts.Count > 0,
            conflicts,
            originalPath,
            WillCreateTargetFolder: !targetExists,
            WillRelinkExistingFolder: targetExists,
            WillRestoreFiles: !targetExists && !options.RestoreMetadataOnly,
            WillCreateRecoverySnapshot: targetExists && conflicts.Count > 0,
            RestorePlanMode: options.RestoreToAnotherFolder ? "custom_parent" : targetExists ? "relink_existing" : "original_path");
    }

    public Task<CloudRepositoryQueuedOperationDto> QueueRestoreAsync(
        CloudRepositoryRestoreOptionsDto options,
        CancellationToken ct = default)
    {
        var operationId = operations.Start("restore", cloudRepositoryId: options.CloudRepositoryId, phase: "queued");

        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var scopedManager = scope.ServiceProvider.GetRequiredService<ICloudRepositoryManagementService>();
                var orchestrator = scope.ServiceProvider.GetRequiredService<IRepositoryCloudSyncOrchestrator>();

                operations.Update(operationId, phase: "building_plan", percent: 5);
                var plan = await scopedManager.BuildRestorePlanAsync(options, CancellationToken.None);
                operations.Update(
                    operationId,
                    phase: plan.BlocksToDownload > 0 ? "downloading_blocks" : "linking_repository",
                    percent: plan.BlocksToDownload > 0 ? 20 : 35,
                    downloadedBlocks: plan.BlocksAlreadyLocal);

                var ok = await orchestrator.RestoreRepositoryFromCloudAsync(
                    options.CloudRepositoryId,
                    plan.TargetPath,
                    restoreFullHistory: options.RestoreFullHistory,
                    restoreToAnotherFolder: false,
                    restoreMetadataOnly: options.RestoreMetadataOnly,
                    ct: CancellationToken.None);

                if (ok)
                    operations.Complete(operationId, "restored");
                else
                    operations.Fail(operationId, "Cloud restore did not complete.", "failed");
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Queued cloud restore failed. CloudRepositoryId {CloudRepositoryId}", options.CloudRepositoryId);
                operations.Fail(operationId, ex.Message, "failed");
            }
        }, CancellationToken.None);

        return Task.FromResult(ToQueued(operationId, "restore", options.CloudRepositoryId));
    }

    public Task<CloudRepositoryQueuedOperationDto> QueueSyncNowAsync(int repositoryId, CancellationToken ct = default)
    {
        var operationId = operations.Start("sync_now", repositoryId, repositoryId, "queued");
        _ = Task.Run(async () =>
        {
            try
            {
                operations.Update(operationId, phase: "syncing", percent: 10);
                await using var scope = scopeFactory.CreateAsyncScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<IRepositoryCloudSyncOrchestrator>();
                await orchestrator.TryPushLatestSnapshotAsync(repositoryId, CancellationToken.None);
                operations.Complete(operationId, "queued_or_synced");
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Manual cloud sync failed. RepositoryId {RepositoryId}", repositoryId);
                operations.Fail(operationId, ex.Message, "failed");
            }
        }, CancellationToken.None);

        return Task.FromResult(ToQueued(operationId, "sync_now", repositoryId));
    }

    public async Task<CloudRepositoryQueuedOperationDto> QueueCompareWithLocalAsync(
        int cloudRepositoryId,
        string? localPath,
        CancellationToken ct = default)
    {
        var operationId = operations.Start("compare", cloudRepositoryId: cloudRepositoryId, phase: "comparing");
        try
        {
            var plan = await BuildRestorePlanAsync(
                new CloudRepositoryRestoreOptionsDto(
                    cloudRepositoryId,
                    localPath,
                    "latest",
                    RestoreFullHistory: false,
                    RestoreLatestSnapshotOnly: true,
                    RestoreMetadataOnly: false,
                    RelinkExistingLocalFolder: false,
                    ConflictStrategy: "compare_only"),
                ct);
            operations.Update(operationId, phase: plan.HasLocalConflicts ? "conflicts_found" : "no_conflicts", percent: 100);
            operations.Complete(operationId, plan.HasLocalConflicts ? "conflicts_found" : "no_conflicts");
        }
        catch (Exception ex)
        {
            operations.Fail(operationId, ex.Message, "failed");
        }

        return ToQueued(operationId, "compare", cloudRepositoryId);
    }

    public Task<CloudRepositoryQueuedOperationDto> QueueDeleteCloudRepositoryAsync(
        int cloudRepositoryId,
        string confirmationText,
        CancellationToken ct = default)
    {
        var operationId = operations.Start("delete_cloud_repository", cloudRepositoryId: cloudRepositoryId, phase: "queued");
        if (!string.Equals(confirmationText?.Trim(), DeleteConfirmationPrefix + cloudRepositoryId, StringComparison.OrdinalIgnoreCase))
        {
            operations.Fail(operationId, $"Confirmation must be: {DeleteConfirmationPrefix}{cloudRepositoryId}", "confirmation_required");
            return Task.FromResult(ToQueued(operationId, "delete_cloud_repository", cloudRepositoryId));
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var profile = await userProfiles.GetActiveProfileAsync(CancellationToken.None);
                if (profile is null)
                    throw new InvalidOperationException("Cloud account is not signed in.");

                var token = await TryGetAccessTokenAsync(profile, CancellationToken.None)
                            ?? throw new InvalidOperationException("Cloud access token is unavailable.");

                var ok = await cloudSync.DeleteRepositoryAsync(token, cloudRepositoryId, CancellationToken.None);
                if (ok)
                    operations.Complete(operationId, "deleted");
                else
                    operations.Fail(operationId, "Cloud repository was not found or could not be deleted.", "failed");
            }
            catch (Exception ex)
            {
                operations.Fail(operationId, ex.Message, "failed");
            }
        }, CancellationToken.None);

        return Task.FromResult(ToQueued(operationId, "delete_cloud_repository", cloudRepositoryId));
    }

    public Task<CloudRepositoryQueuedOperationDto> QueueRemoveCloudOnlyHistoryAsync(
        int cloudRepositoryId,
        string confirmationText,
        CancellationToken ct = default)
    {
        var operationId = operations.Start("remove_cloud_only_history", cloudRepositoryId: cloudRepositoryId, phase: "blocked");
        if (!string.Equals(confirmationText?.Trim(), RemoveHistoryConfirmation, StringComparison.Ordinal))
        {
            operations.Fail(operationId, $"Confirmation must be: {RemoveHistoryConfirmation}", "confirmation_required");
        }
        else
        {
            operations.Fail(operationId, "Cloud-only history pruning is not implemented yet. No cloud data was removed.", "not_implemented");
        }

        return Task.FromResult(ToQueued(operationId, "remove_cloud_only_history", cloudRepositoryId));
    }

    private CloudRepositoryManagerRepositoryDto BuildRepositoryRow(
        CloudRepositoryHeaderDto remote,
        IReadOnlyDictionary<int, Repository> activeLocalById,
        IReadOnlyDictionary<int, Repository> allLocalById)
    {
        activeLocalById.TryGetValue(remote.RepositoryId, out var active);
        allLocalById.TryGetValue(remote.RepositoryId, out var anyLocal);
        var path = active?.Directory.Path ?? anyLocal?.Directory.Path;
        var pathExists = !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
        var linked = active is not null;
        var restoreStatus = linked
            ? pathExists ? "linked" : "local_path_missing"
            : anyLocal is { IsDeleted: true } ? "missing_locally" : "cloud_only";
        var conflictStatus = active is not null
                             && remote.LatestSnapshotId.HasValue
                             && active.CloudLastRemoteSnapshotId.HasValue
                             && active.CloudLastRemoteSnapshotId.Value != remote.LatestSnapshotId.Value
            ? "diverged"
            : "none";

        return new CloudRepositoryManagerRepositoryDto(
            remote.RepositoryId,
            remote.Name,
            path,
            path,
            linked,
            pathExists,
            remote.LatestSnapshotId.HasValue ? 1 : 0,
            remote.LatestSnapshotCreatedAtUtc,
            0,
            active?.CloudSyncLastStatus ?? "not_linked",
            restoreStatus,
            conflictStatus);
    }

    private async Task<string?> TryGetAccessTokenAsync(UserProfileSessionDto profile, CancellationToken ct)
    {
        var evaluation = tokenPolicy.Evaluate(profile.AccessToken);
        if (evaluation.CanUseForSync)
            return profile.AccessToken;

        if (string.IsNullOrWhiteSpace(profile.RefreshToken))
            return null;

        var refreshed = await auth.RefreshAsync(profile.RefreshToken, ct);
        if (refreshed is null || string.IsNullOrWhiteSpace(refreshed.AccessToken))
            return null;

        await userProfiles.SaveOrUpdateProfileAsync(
            refreshed.Username,
            refreshed.CloudUserId,
            refreshed.AccessToken,
            refreshed.Email,
            refreshed.CloudSessionId,
            refreshed.RefreshToken,
            refreshed.AccessTokenExpiresAtUtc,
            refreshed.RefreshTokenExpiresAtUtc,
            ct);
        return refreshed.AccessToken;
    }

    private async Task<string?> ResolveKnownOriginalPathAsync(int cloudRepositoryId, CancellationToken ct)
    {
        var local = await db.Set<Repository>()
            .IgnoreQueryFilters()
            .Include(r => r.Directory)
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.CloudRepositoryId == cloudRepositoryId || r.Id == cloudRepositoryId, ct);

        return local?.Directory.Path;
    }

    private static int GetCloudRepositoryId(Repository repository)
        => repository.CloudRepositoryId ?? repository.Id;

    private string ResolveTargetPath(
        CloudRepositoryRestoreOptionsDto options,
        string repositoryName,
        string username,
        string? originalPath)
    {
        if (!string.IsNullOrWhiteSpace(options.TargetPath))
        {
            var selected = Path.GetFullPath(options.TargetPath.Trim());
            return options.RestoreToAnotherFolder
                ? Path.Combine(selected, SanitizeSegment(repositoryName))
                : selected;
        }

        if (options.RestoreToAnotherFolder)
        {
            var parent = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VeyraFlow",
                "cloud-restored",
                SanitizeSegment(username));
            return Path.Combine(parent, SanitizeSegment(repositoryName));
        }

        if (!string.IsNullOrWhiteSpace(originalPath))
            return Path.GetFullPath(originalPath);

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VeyraFlow",
            "cloud-restored",
            SanitizeSegment(username));
        return Path.Combine(root, SanitizeSegment(repositoryName));
    }

    private async Task<IReadOnlyList<CloudRepositoryConflictItemDto>> BuildLocalCloudConflictsAsync(
        CloudSnapshotPackageDto package,
        string targetPath,
        CancellationToken ct)
    {
        if (!Directory.Exists(targetPath))
            return [];

        var conflicts = new List<CloudRepositoryConflictItemDto>();
        foreach (var entry in package.Entries.Where(e => !e.IsDirectory).Take(500))
        {
            ct.ThrowIfCancellationRequested();
            var localPath = ResolveSafePath(targetPath, entry.RelativePath);
            if (localPath is null || !File.Exists(localPath))
            {
                conflicts.Add(new CloudRepositoryConflictItemDto(entry.RelativePath, "cloud_only", "missing", "exists", "Restore cloud file or keep local deletion."));
                continue;
            }

            var info = new FileInfo(localPath);
            var hash = await ComputeSha256Async(localPath, ct);
            if (info.Length != entry.SizeBytes || !string.Equals(hash, entry.ContentHashSha256 ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                conflicts.Add(new CloudRepositoryConflictItemDto(entry.RelativePath, "modified", $"{info.Length} B", $"{entry.SizeBytes} B", "Create recovery snapshot before restore."));
            }
        }

        return conflicts;
    }

    private bool LocalBlockExists(string blockHash)
    {
        var path = ResolveLocalBlockPath(blockHash);
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    private string? ResolveLocalBlockPath(string blockHash)
    {
        if (string.IsNullOrWhiteSpace(blockHash))
            return null;

        var root = configuration["Storage:BlockStorePath"];
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VeyraFlow",
                "blocks");
        }

        if (blockHash.StartsWith("sha256-", StringComparison.OrdinalIgnoreCase))
        {
            var hash = blockHash["sha256-".Length..].Trim().ToLowerInvariant();
            return hash.Length < 4 ? null : Path.Combine(root, "managed", "blocks", hash[..2], hash[2..4], hash + ".bin");
        }

        var safe = blockHash.Trim().ToLowerInvariant();
        return safe.Length < 4 ? null : Path.Combine(root, "native", safe[..2], safe[2..4], safe + ".bin");
    }

    private static bool TargetPathExists(string? path)
        => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);

    private static string? ResolveSafePath(string root, string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, normalized));
        return full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string SanitizeSegment(string value)
    {
        var cleaned = new string(value.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "repository" : cleaned;
    }

    private CloudRepositoryQueuedOperationDto ToQueued(string id, string kind, int? cloudRepositoryId)
    {
        var snapshot = operations.Snapshot().FirstOrDefault(x => x.OperationId == id);
        return new CloudRepositoryQueuedOperationDto(
            id,
            kind,
            snapshot?.RepositoryId,
            cloudRepositoryId,
            snapshot?.Status ?? "queued");
    }
}
