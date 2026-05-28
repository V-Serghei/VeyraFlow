using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Abstractions.Sync;
using Veyra.Application.Commands.Repository;
using Veyra.Application.Common.Files;
using Veyra.Application.DTOs;
using Veyra.Domain.Entities;
using Veyra.Infrastructure.Data.Persistence;

namespace Veyra.Infrastructure.Data.Sync;

public sealed class RepositoryCloudSyncOrchestrator(
    VeyraDbContext db,
    ICloudSyncService cloudSync,
    IAuthService authService,
    IUserProfileRepository userProfiles,
    IAccessTokenPolicyService tokenPolicy,
    IRepositoryRepository repositories,
    IFileContentStore fileContentStore,
    IMediator mediator,
    IConfiguration configuration,
    ICloudAvailabilityService cloudAvailability,
    ICloudSyncRuntimeControlService runtimeControl,
    ILogger<RepositoryCloudSyncOrchestrator> log)
    : IRepositoryCloudSyncOrchestrator
{
    private const string ManagedHashPrefix = "sha256-";
    private const string PushOperationType = RepositorySyncQueueItem.OperationPushSnapshot;
    private static readonly TimeSpan RunningLeaseTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UploadCheckpointPersistInterval = TimeSpan.FromSeconds(2);
    private const int UploadCheckpointPersistEveryBlocks = 128;
    private const int UploadBatchMaxBlocks = 8;
    private const long UploadBatchMaxBytes = 2L * 1024 * 1024;
    private const int UploadBatchProgressLogEveryBlocks = 1024;
    private const int RepairProbeMaxConcurrency = 8;
    private const int RestoreDownloadMaxConcurrency = 8;
    private const int RestoreRepositoryMaxConcurrency = 2;
    private static readonly TimeSpan RemoteRepositoryCacheTtl = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim _queueGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly string _clientInstanceId = ResolveClientInstanceId(configuration);
    private readonly object _activeQueueItemStateGate = new();
    private CancellationTokenSource? _activeQueueItemCts;
    private long? _activeQueueItemId;
    private int? _activeQueueRepositoryId;

    public async Task TryPushLatestSnapshotAsync(int repositoryId, CancellationToken ct = default)
    {
        if (await TryQueueLatestSnapshotWhilePausedAsync(repositoryId, ct))
            return;

        var repository = await db.Repositories
            .FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted, ct);

        if (repository is null)
            return;

        var cloudRepositoryId = await EnsureRepositoryCloudIdAsync(repository, ct);

        var snapshots = await db.RepositorySnapshots
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
            .Where(s => db.SnapshotFileLinks.Any(l => l.SnapshotId == s.Id && !l.IsDeleted))
            .OrderBy(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .Select(s => new { s.Id, s.CreatedAt })
            .ToListAsync(ct);

        if (snapshots.Count == 0)
            return;

        var latestSnapshot = snapshots[^1];
        foreach (var snapshot in snapshots)
        {
            var remoteSnapshotId = BuildRemoteSnapshotId(cloudRepositoryId, snapshot.Id, snapshot.CreatedAt);
            await EnqueueSnapshotPushAsync(
                repository,
                snapshot.Id,
                remoteSnapshotId,
                ct,
                forceRequeueExisting: snapshot.Id == latestSnapshot.Id);
        }

        if (cloudAvailability.ShouldSkipCloudOperation(out var skipReason))
        {
            await ApplyCloudUnavailableStatusToQueuedRepositoriesAsync(skipReason, ct);
            log.LogInformation(
                "Queued repository snapshot history for later cloud sync because cloud is unavailable. RepositoryId {RepositoryId}. SnapshotCount {SnapshotCount}. LatestSnapshotId {SnapshotId}. Reason {Reason}",
                repositoryId,
                snapshots.Count,
                latestSnapshot.Id,
                skipReason);
            return;
        }

        await ProcessPendingQueueAsync(ct);
    }

    public async Task ProcessPendingQueueAsync(CancellationToken ct = default)
    {
        if (runtimeControl.IsPaused)
        {
            await ApplyPausedStatusToQueuedRepositoriesAsync(ct);
            return;
        }

        void OnRuntimeStateChanged(CloudSyncRuntimeSnapshot snapshot)
        {
            if (snapshot.IsPaused)
                CancelActiveQueueItemForPause();
        }

        runtimeControl.StateChanged += OnRuntimeStateChanged;
        try
        {
            await RecoverStaleRunningQueueItemsAsync(ct);

            if (cloudAvailability.ShouldSkipCloudOperation(out var skipReason))
            {
                await ApplyCloudUnavailableStatusToQueuedRepositoriesAsync(skipReason, ct);
                log.LogInformation(
                    "Cloud sync queue processing skipped before HTTP work because cloud is unavailable. Reason {Reason}",
                    skipReason);
                return;
            }

            var accessToken = await TryGetSyncAccessTokenAsync("process sync queue", ct);
            if (string.IsNullOrWhiteSpace(accessToken))
                return;

            var remoteSnapshotCache = new RemoteRepositorySnapshotCache();
            await _queueGate.WaitAsync(ct);
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    if (cloudAvailability.ShouldSkipCloudOperation(out var loopSkipReason))
                    {
                        await ApplyCloudUnavailableStatusToQueuedRepositoriesAsync(loopSkipReason, CancellationToken.None);
                        log.LogInformation(
                            "Cloud sync queue processing stopped before the next queued item because cloud is unavailable. Reason {Reason}",
                            loopSkipReason);
                        break;
                    }

                    if (runtimeControl.IsPaused)
                    {
                        await ApplyPausedStatusToQueuedRepositoriesAsync(CancellationToken.None);
                        log.LogInformation("Cloud sync queue processing paused before the next queued item.");
                        break;
                    }

                    var next = await GetNextQueueItemAsync(ct);
                    if (next is null)
                        break;

                    await ProcessQueueItemAsync(accessToken, next.Id, remoteSnapshotCache, ct);
                }
            }
            finally
            {
                _queueGate.Release();
            }
        }
        finally
        {
            runtimeControl.StateChanged -= OnRuntimeStateChanged;
        }
    }

    public async Task<bool> CancelRepositorySyncAsync(int repositoryId, CancellationToken ct = default)
    {
        var activeCancelled = CancelActiveQueueItemIfMatches(repositoryId);
        if (activeCancelled)
        {
            log.LogInformation(
                "Requested cancellation for active repository sync. RepositoryId {RepositoryId}",
                repositoryId);
            return true;
        }

        await _queueGate.WaitAsync(ct);
        try
        {
            var affected = await MarkRepositoryQueueCancelledAsync(
                repositoryId,
                "Cloud sync cancelled by user.",
                ct);

            log.LogInformation(
                "Repository sync cancellation completed without active upload. RepositoryId {RepositoryId}. AffectedQueueItems {AffectedQueueItems}",
                repositoryId,
                affected);

            return affected > 0;
        }
        finally
        {
            _queueGate.Release();
        }
    }

    public async Task<RepositoryCloudRepairResultDto> RepairRepositoryCloudDataAsync(int repositoryId, CancellationToken ct = default)
    {
        if (runtimeControl.IsPaused)
        {
            return new RepositoryCloudRepairResultDto(
                Success: false,
                ReferencedBlocks: 0,
                AlreadyPresentBlocks: 0,
                UploadedBlocks: 0,
                MissingLocalBlocks: 0,
                FailedUploads: 0,
                ErrorMessage: "Cloud actions are paused.");
        }

        var accessToken = await TryGetSyncAccessTokenAsync("repair repository cloud data", ct);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new RepositoryCloudRepairResultDto(
                Success: false,
                ReferencedBlocks: 0,
                AlreadyPresentBlocks: 0,
                UploadedBlocks: 0,
                MissingLocalBlocks: 0,
                FailedUploads: 0,
                ErrorMessage: "Cloud access token is unavailable.");
        }

        var repository = await db.Repositories
            .FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted, ct);

        if (repository is null)
        {
            return new RepositoryCloudRepairResultDto(
                Success: false,
                ReferencedBlocks: 0,
                AlreadyPresentBlocks: 0,
                UploadedBlocks: 0,
                MissingLocalBlocks: 0,
                FailedUploads: 0,
                ErrorMessage: $"Repository {repositoryId} was not found.");
        }

        var blockHashes = await db.Set<FileVersionBlock>()
            .Where(b => !b.IsDeleted)
            .Where(b => !b.FileVersion.IsDeleted)
            .Where(b => !b.FileVersion.FileIdentity.IsDeleted)
            .Where(b => b.FileVersion.FileIdentity.RepositoryId == repositoryId)
            .Select(b => b.BlockStorageKey)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct()
            .OrderBy(h => h)
            .ToListAsync(ct);

        if (blockHashes.Count == 0)
        {
            await TryPushLatestSnapshotAsync(repositoryId, ct);
            await ProcessPendingQueueAsync(ct);

            return new RepositoryCloudRepairResultDto(
                Success: true,
                ReferencedBlocks: 0,
                AlreadyPresentBlocks: 0,
                UploadedBlocks: 0,
                MissingLocalBlocks: 0,
                FailedUploads: 0);
        }

        var (missingHashes, alreadyPresent, failedProbeCount) = await ProbeRemoteBlocksForRepairAsync(
            accessToken,
            repositoryId,
            blockHashes,
            ct);

        var (uploaded, missingLocal, failedUploadCount) = await UploadMissingBlocksForRepairAsync(
            accessToken,
            repositoryId,
            missingHashes,
            ct);

        var failedUploads = failedProbeCount + failedUploadCount;

        await TryPushLatestSnapshotAsync(repositoryId, ct);
        await ProcessPendingQueueAsync(ct);

        log.LogInformation(
            "Repository cloud repair completed. RepositoryId {RepositoryId}. Referenced {Referenced}. Present {Present}. Uploaded {Uploaded}. MissingLocal {MissingLocal}. Failed {Failed}",
            repositoryId,
            blockHashes.Count,
            alreadyPresent,
            uploaded,
            missingLocal,
            failedUploads);

        return new RepositoryCloudRepairResultDto(
            Success: failedUploads == 0,
            ReferencedBlocks: blockHashes.Count,
            AlreadyPresentBlocks: alreadyPresent,
            UploadedBlocks: uploaded,
            MissingLocalBlocks: missingLocal,
            FailedUploads: failedUploads,
            ErrorMessage: failedUploads == 0
                ? null
                : $"Failed to reconcile {failedUploads} block(s) during repair.");
    }

    public async Task<int> RestoreRepositoriesFromCloudAsync(string? targetRootDirectory = null, CancellationToken ct = default)
    {
        if (runtimeControl.IsPaused)
            return 0;

        var profile = await userProfiles.GetActiveProfileAsync(ct);
        if (profile is null)
            return 0;

        var accessToken = await TryGetSyncAccessTokenAsync("restore repositories from cloud", ct, profile);
        if (string.IsNullOrWhiteSpace(accessToken))
            return 0;

        var remoteRepositories = await cloudSync.GetRepositoriesAsync(accessToken, ct);
        if (remoteRepositories.Count == 0)
            return 0;

        var localRepositories = await repositories.GetAllRepositoriesAsync(ct);
        var localNames = localRepositories
            .Select(r => r.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var restoreCandidates = remoteRepositories
            .Where(remote => !string.IsNullOrWhiteSpace(remote.Name))
            .GroupBy(remote => remote.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(remote => !localNames.Contains(remote.Name))
            .Select((remote, index) => (Remote: remote, SortOrder: index))
            .ToList();

        if (restoreCandidates.Count == 0)
            return 0;

        var preparedRestores = new ConcurrentBag<PreparedCloudRepositoryRestore>();
        var restoreParallelism = Math.Max(1, Math.Min(RestoreRepositoryMaxConcurrency, restoreCandidates.Count));

        log.LogInformation(
            "Cloud restore preparation started. Candidates {Count}. MaxConcurrency {MaxConcurrency}",
            restoreCandidates.Count,
            restoreParallelism);

        await Parallel.ForEachAsync(
            restoreCandidates,
            new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = restoreParallelism
            },
            async (candidate, itemCt) =>
            {
                try
                {
                    var prepared = await PrepareCloudRepositoryRestoreAsync(
                        accessToken,
                        profile.Username,
                        candidate.Remote,
                        targetRootDirectory,
                        candidate.SortOrder,
                        itemCt);

                    if (prepared is not null)
                        preparedRestores.Add(prepared);
                }
                catch (OperationCanceledException) when (itemCt.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    log.LogWarning(
                        ex,
                        "Cloud restore preparation failed. RepositoryId {RepositoryId}. Name {Name}",
                        candidate.Remote.RepositoryId,
                        candidate.Remote.Name);
                }
            });

        var restoredCount = 0;

        foreach (var prepared in preparedRestores.OrderBy(x => x.SortOrder))
        {
            var createResult = await mediator.Send(
                new CreateRepositoryWithFormatsCommand(
                    prepared.RepositoryName,
                    prepared.RepositoryDescription,
                    prepared.RestorePath,
                    prepared.Formats),
                ct);

            if (!createResult.Success || createResult.Value?.RepositoryId <= 0)
            {
                log.LogWarning(
                    "Cloud restore created files but repository bootstrap failed. Name {Name}. Path {Path}. Error {Error}",
                    prepared.RepositoryName,
                    prepared.RestorePath,
                    createResult.Error ?? "(none)");
                continue;
            }

            var restoredRepositoryId = createResult.Value!.RepositoryId;

            await mediator.Send(
                new ScanRepositoryCommand(
                    restoredRepositoryId,
                    Progress: null,
                    new RepositoryScanOptionsDto(
                        SaveFileVersions: true,
                        TriggerOverride: "cloud_restore_sync",
                        SnapshotTitle: $"cloud_restore_{DateTime.Now:yyyyMMdd_HHmmss}")),
                ct);

            await MarkRepositoryCloudLinkedAsync(
                restoredRepositoryId,
                prepared.CloudRepositoryId,
                prepared.LatestRemoteSnapshotId,
                ct);

            localNames.Add(prepared.RepositoryName);
            restoredCount++;
        }

        log.LogInformation(
            "Cloud restore completed. Candidates {Candidates}. Prepared {Prepared}. Restored {Restored}",
            restoreCandidates.Count,
            preparedRestores.Count,
            restoredCount);

        return restoredCount;
    }

    public async Task<bool> RestoreRepositoryFromCloudAsync(
        int cloudRepositoryId,
        string? targetRootDirectory = null,
        bool restoreFullHistory = false,
        bool restoreToAnotherFolder = false,
        bool restoreMetadataOnly = false,
        CancellationToken ct = default)
    {
        if (runtimeControl.IsPaused)
            return false;

        var profile = await userProfiles.GetActiveProfileAsync(ct);
        if (profile is null)
            return false;

        var accessToken = await TryGetSyncAccessTokenAsync("restore one repository from cloud", ct, profile);
        if (string.IsNullOrWhiteSpace(accessToken))
            return false;

        var remoteRepositories = await cloudSync.GetRepositoriesAsync(accessToken, ct);
        var remote = remoteRepositories.FirstOrDefault(r => r.RepositoryId == cloudRepositoryId);
        if (remote is null)
            return false;

        List<CloudSnapshotPackageDto> packages = restoreFullHistory
            ? (await cloudSync.GetRepositorySnapshotsAsync(accessToken, remote.RepositoryId, ct))
                .OrderBy(p => p.Snapshot.CreatedAtUtc)
                .ThenBy(p => p.Snapshot.Id)
                .ToList()
            : [];

        var package = restoreFullHistory
            ? packages.LastOrDefault()
            : await cloudSync.GetLatestSnapshotAsync(accessToken, remote.RepositoryId, ct);
        if (package is null)
            return false;

        var requestedRestorePath = ResolveSingleRepositoryRestorePath(
            targetRootDirectory,
            package.Repository.Name,
            originalPath: null,
            restoreToAnotherFolder);
        var existing = await FindExistingRepositoryForCloudRestoreAsync(cloudRepositoryId, requestedRestorePath, ct);

        if (restoreFullHistory && !restoreMetadataOnly)
        {
            var missingBlocks = await EnsureLocalBlocksForRestoreAsync(
                accessToken,
                package.Repository.Name,
                GetRequiredRestoreBlockHashes(packages),
                ct);
            if (missingBlocks.Count > 0)
            {
                log.LogWarning(
                    "Full cloud history restore blocked because required blocks are missing. RepositoryId {RepositoryId}. MissingBlocks {MissingBlocks}",
                    cloudRepositoryId,
                    missingBlocks.Count);
                return false;
            }
        }

        if (existing is not null)
        {
            var restorePath = ResolveSingleRepositoryRestorePath(
                targetRootDirectory,
                package.Repository.Name,
                existing.Directory.Path,
                restoreToAnotherFolder);
            var targetExists = Directory.Exists(restorePath);

            if (!targetExists)
            {
                Directory.CreateDirectory(restorePath);
                if (!restoreMetadataOnly)
                    await RestoreFilesFromPackageAsync(accessToken, package, restorePath, ct);
            }
            else if (!restoreMetadataOnly)
            {
                await EnsureLocalBlocksForRestoreAsync(
                    accessToken,
                    package.Repository.Name,
                    restoreFullHistory ? GetRequiredRestoreBlockHashes(packages) : GetRequiredRestoreBlockHashes(package),
                    ct);
            }

            if (existing.IsDeleted)
                await repositories.RestoreRepositoryAsync(existing.Id, ct);

            await RelinkExistingRepositoryAsync(existing.Id, cloudRepositoryId, restorePath, package, remote.LatestSnapshotId, ct);
            if (restoreFullHistory || restoreMetadataOnly)
            {
                IReadOnlyList<CloudSnapshotPackageDto> packagesToImport = restoreFullHistory ? packages : [package];
                await ImportCloudSnapshotHistoryAsync(existing.Id, packagesToImport, replaceExistingHistory: false, ct);
            }

            var differsFromCloudHead = await LocalFolderDiffersFromCloudHeadAsync(package, restorePath, ct);
            if (!restoreMetadataOnly && differsFromCloudHead)
            {
                await mediator.Send(
                    new ScanRepositoryCommand(
                        existing.Id,
                        Progress: null,
                        new RepositoryScanOptionsDto(
                            SaveFileVersions: true,
                            TriggerOverride: "cloud_recovery_snapshot",
                            SnapshotTitle: "Recovery after cloud restore")),
                    ct);
            }

            await MarkRepositoryCloudLinkedAsync(existing.Id, cloudRepositoryId, remote.LatestSnapshotId, ct);

            return true;
        }

        var restorePathForNew = requestedRestorePath;
        var targetPathExists = Directory.Exists(restorePathForNew);
        if (!targetPathExists)
        {
            Directory.CreateDirectory(restorePathForNew);
            if (!restoreMetadataOnly)
                await RestoreFilesFromPackageAsync(accessToken, package, restorePathForNew, ct);
        }
        else if (!restoreMetadataOnly)
        {
            await EnsureLocalBlocksForRestoreAsync(
                accessToken,
                package.Repository.Name,
                restoreFullHistory ? GetRequiredRestoreBlockHashes(packages) : GetRequiredRestoreBlockHashes(package),
                ct);
        }

        var formats = ResolveFormats(package);

        var createResult = await mediator.Send(
            new CreateRepositoryWithFormatsCommand(
                package.Repository.Name,
                package.Repository.Description,
                restorePathForNew,
                formats),
            ct);

        if (!createResult.Success || createResult.Value is null || createResult.Value.RepositoryId <= 0)
            return false;

        var createdRepositoryId = createResult.Value.RepositoryId;
        if (restoreFullHistory || restoreMetadataOnly)
        {
            IReadOnlyList<CloudSnapshotPackageDto> packagesToImport = restoreFullHistory ? packages : [package];
            await ImportCloudSnapshotHistoryAsync(createdRepositoryId, packagesToImport, replaceExistingHistory: true, ct);
        }

        await MarkRepositoryCloudLinkedAsync(createdRepositoryId, cloudRepositoryId, remote.LatestSnapshotId, ct);

        if (!restoreMetadataOnly && targetPathExists && await LocalFolderDiffersFromCloudHeadAsync(package, restorePathForNew, ct))
        {
            await mediator.Send(
                new ScanRepositoryCommand(
                    createdRepositoryId,
                    Progress: null,
                    new RepositoryScanOptionsDto(
                        SaveFileVersions: true,
                        TriggerOverride: "cloud_recovery_snapshot",
                        SnapshotTitle: "Recovery after cloud restore")),
                ct);
        }

        return true;
    }

    private async Task<PreparedCloudRepositoryRestore?> PrepareCloudRepositoryRestoreAsync(
        string accessToken,
        string username,
        CloudRepositoryHeaderDto remote,
        string? targetRootDirectory,
        int sortOrder,
        CancellationToken ct)
    {
        var package = await cloudSync.GetLatestSnapshotAsync(accessToken, remote.RepositoryId, ct);
        if (package is null)
        {
            log.LogWarning(
                "Cloud restore skipped repository because latest snapshot package is missing. RepositoryId {RepositoryId}. Name {Name}",
                remote.RepositoryId,
                remote.Name);
            return null;
        }

        var restorePath = BuildRestorePath(targetRootDirectory, username, remote.Name);
        Directory.CreateDirectory(restorePath);

        await RestoreFilesFromPackageAsync(accessToken, package, restorePath, ct);

        var formats = package.Entries
            .Where(e => !e.IsDirectory)
            .Select(e => NormalizeFormat(e.Extension))
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (formats.Count == 0)
            formats = [".txt"];

        return new PreparedCloudRepositoryRestore(
            sortOrder,
            remote.RepositoryId,
            remote.LatestSnapshotId,
            package.Repository.Name,
            package.Repository.Description,
            restorePath,
            formats);
    }

    private async Task RelinkExistingRepositoryAsync(
        int repositoryId,
        int cloudRepositoryId,
        string restorePath,
        CloudSnapshotPackageDto package,
        long? remoteSnapshotId,
        CancellationToken ct)
    {
        var repository = await db.Set<Repository>()
            .IgnoreQueryFilters()
            .Include(r => r.Directory)
            .FirstOrDefaultAsync(r => r.Id == repositoryId, ct);

        if (repository is null)
            return;

        var fullPath = Path.GetFullPath(restorePath);
        if (!string.Equals(repository.Directory.Path, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            repository.Directory.Path = fullPath;
            repository.Directory.IsDeleted = false;
            repository.Directory.DeletedAt = null;
            repository.Directory.IsEnabled = true;
            repository.Directory.UpdatedAt = DateTime.UtcNow;
        }

        repository.Name = string.IsNullOrWhiteSpace(package.Repository.Name)
            ? repository.Name
            : package.Repository.Name;
        repository.Description = package.Repository.Description;
        repository.IsDeleted = false;
        repository.DeletedAt = null;
        repository.CloudRepositoryId = cloudRepositoryId;
        repository.CloudLastRemoteSnapshotId = remoteSnapshotId;
        repository.CloudLastSyncedAt = DateTime.UtcNow;
        repository.CloudSyncLastStatus = "linked";
        repository.CloudSyncLastError = null;
        repository.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    private async Task MarkRepositoryCloudLinkedAsync(
        int repositoryId,
        int cloudRepositoryId,
        long? remoteSnapshotId,
        CancellationToken ct)
    {
        var repository = await db.Set<Repository>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == repositoryId, ct);

        if (repository is null)
            return;

        var latestLocalSnapshotId = await db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => (long?)s.Id)
            .FirstOrDefaultAsync(ct);

        repository.CloudRepositoryId = cloudRepositoryId;
        repository.CloudLastSyncedAt = DateTime.UtcNow;
        repository.CloudLastLocalSnapshotId = latestLocalSnapshotId;
        repository.CloudLastRemoteSnapshotId = remoteSnapshotId;
        repository.CloudSyncLastStatus = "linked";
        repository.CloudSyncLastError = null;
        repository.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    private async Task<Repository?> FindExistingRepositoryForCloudRestoreAsync(
        int cloudRepositoryId,
        string? requestedRestorePath,
        CancellationToken ct)
    {
        var candidates = await db.Set<Repository>()
            .IgnoreQueryFilters()
            .Include(r => r.Directory)
            .Where(r => r.CloudRepositoryId == cloudRepositoryId || r.Id == cloudRepositoryId)
            .ToListAsync(ct);

        var byCloudId = candidates.FirstOrDefault(r => r.CloudRepositoryId == cloudRepositoryId);
        if (byCloudId is not null)
            return byCloudId;

        var byLegacyId = candidates.FirstOrDefault(r => r.Id == cloudRepositoryId);
        if (byLegacyId is not null)
            return byLegacyId;

        if (string.IsNullOrWhiteSpace(requestedRestorePath))
            return null;

        var normalizedRequestedPath = Path.GetFullPath(requestedRestorePath.Trim());
        var pathCandidates = await db.Set<Repository>()
            .IgnoreQueryFilters()
            .Include(r => r.Directory)
            .Where(r => !r.Directory.IsDeleted)
            .ToListAsync(ct);

        return pathCandidates.FirstOrDefault(r =>
            string.Equals(
                Path.GetFullPath(r.Directory.Path),
                normalizedRequestedPath,
                StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> LocalFolderDiffersFromCloudHeadAsync(
        CloudSnapshotPackageDto package,
        string rootPath,
        CancellationToken ct)
    {
        if (!Directory.Exists(rootPath))
            return true;

        var expectedFiles = package.Entries
            .Where(static e => !e.IsDirectory)
            .ToDictionary(static e => NormalizeRelativePath(e.RelativePath), StringComparer.OrdinalIgnoreCase);

        foreach (var entry in expectedFiles.Values)
        {
            ct.ThrowIfCancellationRequested();

            var fullPath = ResolvePath(rootPath, entry.RelativePath);
            if (fullPath is null || !File.Exists(fullPath))
                return true;

            var info = new FileInfo(fullPath);
            if (info.Length != entry.SizeBytes)
                return true;

            if (!string.IsNullOrWhiteSpace(entry.ContentHashSha256))
            {
                var hash = await ComputeSha256Async(fullPath, ct);
                if (!string.Equals(hash, entry.ContentHashSha256, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        foreach (var filePath in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
            if (!expectedFiles.ContainsKey(NormalizeRelativePath(relative)))
                return true;
        }

        return false;
    }

    private static IReadOnlyList<string> GetRequiredRestoreBlockHashes(CloudSnapshotPackageDto package)
        => package.FileVersions
            .Where(static v => !v.IsDeletionMarker)
            .SelectMany(static v => v.Blocks)
            .Select(static b => b.BlockHash)
            .Where(static h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<string> GetRequiredRestoreBlockHashes(IEnumerable<CloudSnapshotPackageDto> packages)
        => packages
            .SelectMany(static p => p.FileVersions)
            .Where(static v => !v.IsDeletionMarker)
            .SelectMany(static v => v.Blocks)
            .Select(static b => b.BlockHash)
            .Where(static h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<string> ResolveFormats(CloudSnapshotPackageDto package)
    {
        var formats = package.Entries
            .Where(e => !e.IsDirectory)
            .Select(e => NormalizeFormat(e.Extension))
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return formats.Count == 0 ? [".txt"] : formats;
    }

    private async Task<int> EnsureRepositoryCloudIdAsync(Repository repository, CancellationToken ct)
    {
        if (repository.CloudRepositoryId is > 0)
            return repository.CloudRepositoryId.Value;

        repository.CloudRepositoryId = repository.Id;
        repository.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return repository.Id;
    }

    private async Task<CloudDivergenceMergeResult> ApplyCloudDivergenceMergeAsync(
        string accessToken,
        Repository repository,
        int cloudRepositoryId,
        long observedRemoteSnapshotId,
        CancellationToken ct)
    {
        var remotePackages = (await cloudSync.GetRepositorySnapshotsAsync(accessToken, cloudRepositoryId, ct))
            .OrderBy(p => p.Snapshot.CreatedAtUtc)
            .ThenBy(p => p.Snapshot.Id)
            .ToList();
        var remotePackage = remotePackages.LastOrDefault()
                            ?? await cloudSync.GetLatestSnapshotAsync(accessToken, cloudRepositoryId, ct);
        if (remotePackage is null)
            return new CloudDivergenceMergeResult(0, null);

        IReadOnlyList<CloudSnapshotPackageDto> packagesToImport = remotePackages.Count > 0
            ? remotePackages
            : [remotePackage];
        await ImportCloudSnapshotHistoryAsync(repository.Id, packagesToImport, replaceExistingHistory: false, ct);

        var restoreRoot = repository.Directory?.Path;
        if (string.IsNullOrWhiteSpace(restoreRoot))
        {
            var loaded = await db.Set<Repository>()
                .Include(r => r.Directory)
                .FirstOrDefaultAsync(r => r.Id == repository.Id, ct);
            restoreRoot = loaded?.Directory.Path;
        }

        if (string.IsNullOrWhiteSpace(restoreRoot) || !Directory.Exists(restoreRoot))
        {
            log.LogWarning(
                "Cloud divergence merge skipped file copies because local repository path is unavailable. RepositoryId {RepositoryId}. CloudRepositoryId {CloudRepositoryId}. RemoteSnapshotId {RemoteSnapshotId}",
                repository.Id,
                cloudRepositoryId,
                observedRemoteSnapshotId);
            return new CloudDivergenceMergeResult(0, null);
        }

        var missingBlocks = await EnsureLocalBlocksForRestoreAsync(
            accessToken,
            remotePackage.Repository.Name,
            GetRequiredRestoreBlockHashes(remotePackage),
            ct);

        var copiedFiles = await RestoreCloudMergeCopiesAsync(remotePackage, restoreRoot, missingBlocks, ct);
        long? mergeSnapshotId = null;
        if (copiedFiles > 0)
        {
            var scanResult = await mediator.Send(
                new ScanRepositoryCommand(
                    repository.Id,
                    Progress: null,
                    new RepositoryScanOptionsDto(
                        SaveFileVersions: true,
                        TriggerOverride: "cloud_merge_snapshot",
                        SnapshotTitle: $"Cloud merge from snapshot {remotePackage.Snapshot.Id}")),
                ct);

            if (scanResult.Success && scanResult.Value?.SnapshotCreated == true)
                mergeSnapshotId = await GetLatestLocalSnapshotIdAsync(repository.Id, ct);
        }

        log.LogInformation(
            "Cloud divergence merged into local repository. RepositoryId {RepositoryId}. CloudRepositoryId {CloudRepositoryId}. RemoteSnapshotId {RemoteSnapshotId}. CopiedFiles {CopiedFiles}. MergeSnapshotId {MergeSnapshotId}",
            repository.Id,
            cloudRepositoryId,
            observedRemoteSnapshotId,
            copiedFiles,
            mergeSnapshotId);

        return new CloudDivergenceMergeResult(copiedFiles, mergeSnapshotId);
    }

    private async Task<int> RestoreCloudMergeCopiesAsync(
        CloudSnapshotPackageDto package,
        string restoreRoot,
        IReadOnlySet<string> missingBlocks,
        CancellationToken ct)
    {
        var copied = 0;
        var latestByPath = package.FileVersions
            .GroupBy(v => NormalizeRelativePath(v.RelativePath), StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(v => v.CreatedAtUtc)
                .ThenByDescending(v => v.FileVersionId)
                .First())
            .Where(v => !v.IsDeletionMarker)
            .ToList();

        foreach (var version in latestByPath)
        {
            ct.ThrowIfCancellationRequested();

            var originalPath = ResolvePath(restoreRoot, version.RelativePath);
            if (originalPath is null)
                continue;

            var failedBlock = version.Blocks.FirstOrDefault(b => missingBlocks.Contains(b.BlockHash));
            if (failedBlock is not null)
            {
                log.LogWarning(
                    "Cloud merge copy skipped because a block is missing. File {File}. Block {BlockHash}",
                    version.RelativePath,
                    failedBlock.BlockHash);
                continue;
            }

            if (File.Exists(originalPath) &&
                !string.IsNullOrWhiteSpace(version.ContentHashSha256))
            {
                var localHash = await ComputeSha256Async(originalPath, ct);
                if (string.Equals(localHash, version.ContentHashSha256, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            var copyPath = BuildCloudMergeCopyPath(originalPath, package.Snapshot.Id);
            var parent = Path.GetDirectoryName(copyPath);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);

            var blocks = version.Blocks
                .OrderBy(b => b.Sequence)
                .Select(block => new StoredFileBlockDto(
                    block.Sequence,
                    block.BlockHash,
                    block.LengthBytes,
                    block.StoredSizeBytes))
                .ToList();

            await fileContentStore.RestoreFileAsync(
                blocks,
                copyPath,
                overwriteExisting: false,
                expectedContentHash: string.IsNullOrWhiteSpace(version.ContentHashSha256) ? null : version.ContentHashSha256,
                ct);
            copied++;
        }

        return copied;
    }

    private async Task<long?> GetLatestLocalSnapshotIdAsync(int repositoryId, CancellationToken ct)
        => await db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => (long?)s.Id)
            .FirstOrDefaultAsync(ct);

    private async Task ImportCloudSnapshotHistoryAsync(
        int repositoryId,
        IReadOnlyList<CloudSnapshotPackageDto> packages,
        bool replaceExistingHistory,
        CancellationToken ct)
    {
        if (packages.Count == 0)
            return;

        var now = DateTime.UtcNow;
        if (replaceExistingHistory)
        {
            var snapshotIds = await db.Set<RepositorySnapshot>()
                .IgnoreQueryFilters()
                .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
                .Select(s => s.Id)
                .ToListAsync(ct);

            if (snapshotIds.Count > 0)
            {
                await db.Set<RepositorySnapshot>()
                    .Where(s => snapshotIds.Contains(s.Id))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.IsDeleted, true)
                        .SetProperty(x => x.DeletedAt, now), ct);
                await db.Set<RepositorySnapshotEntry>()
                    .Where(e => snapshotIds.Contains(e.SnapshotId))
                    .ExecuteUpdateAsync(e => e
                        .SetProperty(x => x.IsDeleted, true)
                        .SetProperty(x => x.DeletedAt, now), ct);
                await db.Set<SnapshotFileLink>()
                    .Where(l => snapshotIds.Contains(l.SnapshotId))
                    .ExecuteUpdateAsync(l => l
                        .SetProperty(x => x.IsDeleted, true)
                        .SetProperty(x => x.DeletedAt, now), ct);
            }
        }

        var identityRows = await db.Set<FileIdentity>()
            .IgnoreQueryFilters()
            .Where(i => i.RepositoryId == repositoryId)
            .ToListAsync(ct);
        var identities = identityRows
            .GroupBy(i => NormalizeRelativePath(i.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var remoteVersionMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages
                     .OrderBy(p => p.Snapshot.CreatedAtUtc)
                     .ThenBy(p => p.Snapshot.Id))
        {
            ct.ThrowIfCancellationRequested();

            var title = package.Snapshot.Title;
            var trigger = string.IsNullOrWhiteSpace(package.Snapshot.Trigger)
                ? "cloud_history_restore"
                : package.Snapshot.Trigger;
            var alreadyImported = await db.Set<RepositorySnapshot>()
                .AsNoTracking()
                .AnyAsync(s =>
                    s.RepositoryId == repositoryId
                    && !s.IsDeleted
                    && s.CreatedAt == package.Snapshot.CreatedAtUtc
                    && s.Trigger == trigger
                    && s.Title == title, ct);
            if (alreadyImported)
                continue;

            var snapshot = new RepositorySnapshot
            {
                RepositoryId = repositoryId,
                CreatedAt = package.Snapshot.CreatedAtUtc,
                Trigger = trigger,
                Title = title,
                TotalEntries = package.Snapshot.TotalEntries,
                FileEntries = package.Snapshot.FileEntries,
                DirectoryEntries = package.Snapshot.DirectoryEntries,
                TotalFileBytes = package.Snapshot.TotalFileBytes
            };
            db.Set<RepositorySnapshot>().Add(snapshot);
            await db.SaveChangesAsync(ct);

            var entries = package.Entries.Select(e => new RepositorySnapshotEntry
            {
                SnapshotId = snapshot.Id,
                RepositoryId = repositoryId,
                RelativePath = NormalizeRelativePath(e.RelativePath),
                ParentRelativePath = string.IsNullOrWhiteSpace(e.ParentRelativePath) ? null : NormalizeRelativePath(e.ParentRelativePath),
                Name = string.IsNullOrWhiteSpace(e.Name) ? Path.GetFileName(e.RelativePath) : e.Name,
                IsDirectory = e.IsDirectory,
                Extension = e.IsDirectory ? null : NormalizeFormat(e.Extension),
                SizeBytes = e.SizeBytes,
                LastWriteUtc = e.LastWriteUtc,
                ContentHashSha256 = e.ContentHashSha256,
                CreatedAt = snapshot.CreatedAt
            }).ToList();
            db.Set<RepositorySnapshotEntry>().AddRange(entries);
            await db.SaveChangesAsync(ct);

            var entryByPath = package.Entries
                .Where(e => !e.IsDirectory)
                .GroupBy(e => NormalizeRelativePath(e.RelativePath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var remoteVersion in package.FileVersions)
            {
                var relativePath = NormalizeRelativePath(remoteVersion.RelativePath);
                if (string.IsNullOrWhiteSpace(relativePath))
                    continue;

                if (!identities.TryGetValue(relativePath, out var identity))
                {
                    identity = new FileIdentity
                    {
                        RepositoryId = repositoryId,
                        RelativePath = relativePath,
                        Name = Path.GetFileName(relativePath),
                        Extension = NormalizeFormat(Path.GetExtension(relativePath)),
                        CreatedAt = remoteVersion.CreatedAtUtc,
                        UpdatedAt = remoteVersion.CreatedAtUtc
                    };
                    db.Set<FileIdentity>().Add(identity);
                    await db.SaveChangesAsync(ct);
                    identities[relativePath] = identity;
                }
                else if (identity.IsDeleted)
                {
                    identity.IsDeleted = false;
                    identity.DeletedAt = null;
                    identity.UpdatedAt = now;
                    await db.SaveChangesAsync(ct);
                }

                var versionKey = $"{relativePath}|{remoteVersion.FileVersionId}";
                if (!remoteVersionMap.TryGetValue(versionKey, out var localVersionId))
                {
                    entryByPath.TryGetValue(relativePath, out var sourceEntry);
                    var version = new FileVersion
                    {
                        FileIdentityId = identity.Id,
                        ContentHashSha256 = remoteVersion.ContentHashSha256 ?? string.Empty,
                        SizeBytes = remoteVersion.SizeBytes,
                        LastWriteUtc = sourceEntry?.LastWriteUtc ?? remoteVersion.CreatedAtUtc,
                        IsDeletionMarker = remoteVersion.IsDeletionMarker,
                        CreatedAt = remoteVersion.CreatedAtUtc
                    };
                    db.Set<FileVersion>().Add(version);
                    await db.SaveChangesAsync(ct);

                    localVersionId = version.Id;
                    remoteVersionMap[versionKey] = localVersionId;

                    if (!remoteVersion.IsDeletionMarker && remoteVersion.Blocks.Count > 0)
                    {
                        var blocks = remoteVersion.Blocks
                            .OrderBy(b => b.Sequence)
                            .Select(b => new FileVersionBlock
                            {
                                FileVersionId = localVersionId,
                                Sequence = b.Sequence,
                                BlockStorageKey = b.BlockHash,
                                LengthBytes = b.LengthBytes,
                                StoredSizeBytes = b.StoredSizeBytes,
                                CreatedAt = remoteVersion.CreatedAtUtc
                            });
                        db.Set<FileVersionBlock>().AddRange(blocks);
                        await db.SaveChangesAsync(ct);
                    }
                }

                var hasLink = await db.Set<SnapshotFileLink>()
                    .IgnoreQueryFilters()
                    .AnyAsync(l => l.SnapshotId == snapshot.Id && l.FileIdentityId == identity.Id, ct);
                if (!hasLink)
                {
                    db.Set<SnapshotFileLink>().Add(new SnapshotFileLink
                    {
                        SnapshotId = snapshot.Id,
                        FileIdentityId = identity.Id,
                        FileVersionId = localVersionId,
                        CreatedAt = snapshot.CreatedAt
                    });
                    await db.SaveChangesAsync(ct);
                }
            }
        }

        log.LogInformation(
            "Imported cloud snapshot history. RepositoryId {RepositoryId}. Packages {Packages}. ReplaceExistingHistory {ReplaceExistingHistory}",
            repositoryId,
            packages.Count,
            replaceExistingHistory);
    }

    private async Task<string?> TryGetSyncAccessTokenAsync(
        string operationName,
        CancellationToken ct,
        UserProfileSessionDto? profile = null)
    {
        if (cloudAvailability.ShouldSkipCloudOperation(out var skipReason))
        {
            log.LogInformation(
                "Skipping cloud sync operation '{Operation}' before token refresh because cloud is unavailable. Reason {Reason}",
                operationName,
                skipReason);
            return null;
        }

        var activeProfile = profile ?? await userProfiles.GetActiveProfileAsync(ct);
        if (activeProfile is null)
            return null;

        var tokenState = tokenPolicy.Evaluate(activeProfile.AccessToken);
        if (tokenState.CanUseForSync)
        {
            if (tokenState.State == AccessTokenValidityState.ExpiringSoon)
            {
                log.LogInformation(
                    "Cloud sync operation '{Operation}' for user {Username} uses token that is expiring soon. {Reason}",
                    operationName,
                    activeProfile.Username,
                    tokenState.Description);
            }

            return activeProfile.AccessToken;
        }

        if (string.IsNullOrWhiteSpace(activeProfile.RefreshToken))
        {
            log.LogWarning(
                "Skipping cloud sync operation '{Operation}'. User {Username}. TokenState {TokenState}. Reason {Reason}",
                operationName,
                activeProfile.Username,
                tokenState.State,
                tokenState.Description);
            return null;
        }

        await _refreshGate.WaitAsync(ct);
        try
        {
            // Token might have been refreshed by another concurrent operation while we waited.
            var refreshProfile = activeProfile;
            var latestProfile = await userProfiles.GetActiveProfileAsync(ct);
            if (latestProfile is not null &&
                string.Equals(latestProfile.Username, activeProfile.Username, StringComparison.OrdinalIgnoreCase))
            {
                refreshProfile = latestProfile;

                var latestState = tokenPolicy.Evaluate(latestProfile.AccessToken);
                if (latestState.CanUseForSync)
                    return latestProfile.AccessToken;
            }

            if (string.IsNullOrWhiteSpace(refreshProfile.RefreshToken))
            {
                log.LogWarning(
                    "Skipping cloud sync operation '{Operation}'. User {Username}. Latest refresh token is unavailable.",
                    operationName,
                    refreshProfile.Username);
                return null;
            }

            var refreshed = await authService.RefreshAsync(refreshProfile.RefreshToken, ct);
            if (refreshed is null || string.IsNullOrWhiteSpace(refreshed.AccessToken))
            {
                log.LogWarning(
                    "Cloud token refresh failed for operation '{Operation}'. User {Username}.",
                    operationName,
                    refreshProfile.Username);
                return null;
            }

            var persistedEmail = !string.IsNullOrWhiteSpace(refreshed.Email)
                ? refreshed.Email
                : refreshProfile.Email;

            await userProfiles.SaveOrUpdateProfileAsync(
                refreshProfile.Username,
                refreshed.CloudUserId,
                refreshed.AccessToken,
                persistedEmail,
                refreshed.CloudSessionId ?? refreshProfile.CloudSessionId,
                refreshed.RefreshToken ?? refreshProfile.RefreshToken,
                refreshed.AccessTokenExpiresAtUtc,
                refreshed.RefreshTokenExpiresAtUtc,
                ct);

            log.LogInformation(
                "Cloud token refreshed for user {Username}. NewExpiry {ExpiryUtc}",
                refreshProfile.Username,
                refreshed.AccessTokenExpiresAtUtc);

            return refreshed.AccessToken;
        }
        catch (CloudAuthRefreshRejectedException ex)
        {
            cloudAvailability.ReportCloudFailure(ex);
            log.LogWarning(
                ex,
                "Cloud refresh session was rejected for operation '{Operation}'. User {Username}. Signing out local cloud session.",
                operationName,
                activeProfile.Username);

            await SignOutActiveProfileIfStillCurrentAsync(activeProfile.Username, ct);
            return null;
        }
        catch (Exception ex)
        {
            cloudAvailability.ReportCloudFailure(ex);
            log.LogWarning(
                ex,
                "Cloud token refresh failed for operation '{Operation}'. User {Username}.",
                operationName,
                activeProfile.Username);
            return null;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task SignOutActiveProfileIfStillCurrentAsync(string username, CancellationToken ct)
    {
        try
        {
            var latestProfile = await userProfiles.GetActiveProfileAsync(ct);
            if (!string.Equals(latestProfile?.Username, username, StringComparison.OrdinalIgnoreCase))
                return;

            await userProfiles.SignOutActiveAsync(ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(
                ex,
                "Failed to sign out local cloud session after refresh rejection. User {Username}.",
                username);
        }
    }

    private async Task RecoverStaleRunningQueueItemsAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - RunningLeaseTimeout;

        var staleRunning = await db.Set<RepositorySyncQueueItem>()
            .Include(q => q.Repository)
            .Where(q => q.OperationType == PushOperationType)
            .Where(q => q.Status == RepositorySyncQueueItem.StatusRunning)
            .Where(q => q.UpdatedAt < cutoff)
            .Where(q => !q.Repository.IsDeleted)
            .ToListAsync(ct);

        if (staleRunning.Count == 0)
            return;

        foreach (var item in staleRunning)
        {
            var repository = item.Repository;
            var maxAttempts = Math.Clamp(item.MaxAttempts, 1, 20);
            var canRetry = item.AttemptCount < maxAttempts;

            item.Status = canRetry
                ? RepositorySyncQueueItem.StatusRetry
                : RepositorySyncQueueItem.StatusDeadLetter;
            item.NextAttemptAtUtc = DateTime.UtcNow;
            item.LastError = TruncateForColumn("Recovered stale running sync job after lease timeout.", 2048);
            item.UpdatedAt = DateTime.UtcNow;

            repository.CloudSyncLastStatus = canRetry ? "retrying" : "dead_letter";
            repository.CloudSyncLastError = item.LastError;
            repository.UpdatedAt = item.UpdatedAt;
        }

        await db.SaveChangesAsync(ct);

        log.LogWarning(
            "Recovered stale running sync jobs. Count {Count}. TimeoutMinutes {TimeoutMinutes}",
            staleRunning.Count,
            RunningLeaseTimeout.TotalMinutes);
    }

    private async Task EnqueueSnapshotPushAsync(
        Repository repository,
        long snapshotId,
        long remoteSnapshotId,
        CancellationToken ct,
        bool forceRequeueExisting = true)
    {
        var now = DateTime.UtcNow;
        var normalizedStrategy = RepositorySyncConflictStrategies.Normalize(repository.SyncConflictStrategy);
        var maxAttempts = Math.Clamp(repository.SyncRetryMaxAttempts, 1, 20);

        var existing = await db.Set<RepositorySyncQueueItem>()
            .Where(q => q.RepositoryId == repository.Id
                        && q.OperationType == PushOperationType
                        && q.SnapshotId == snapshotId)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            if (!forceRequeueExisting && existing.Status == RepositorySyncQueueItem.StatusCompleted)
                return;

            // "Sync now" should re-queue existing work for the same snapshot (including completed rows).
            existing.RemoteSnapshotId = remoteSnapshotId;
            existing.ConflictStrategy = normalizedStrategy;
            existing.MaxAttempts = maxAttempts;
            existing.AttemptCount = 0;
            existing.Status = RepositorySyncQueueItem.StatusPending;
            existing.NextAttemptAtUtc = now;
            existing.ObservedRemoteSnapshotId = null;
            existing.LastError = null;
            existing.UploadCheckpointSignature = null;
            existing.UploadCheckpointTotal = 0;
            existing.UploadCheckpointNextIndex = 0;
            existing.UpdatedAt = now;

            repository.CloudSyncLastStatus = "queued";
            repository.CloudSyncLastError = null;
            repository.UpdatedAt = now;

            await db.SaveChangesAsync(ct);
            return;
        }

        var queueItem = new RepositorySyncQueueItem
        {
            RepositoryId = repository.Id,
            SnapshotId = snapshotId,
            RemoteSnapshotId = remoteSnapshotId,
            OperationType = PushOperationType,
            Status = RepositorySyncQueueItem.StatusPending,
            ConflictStrategy = normalizedStrategy,
            AttemptCount = 0,
            MaxAttempts = maxAttempts,
            NextAttemptAtUtc = now,
            UploadCheckpointSignature = null,
            UploadCheckpointTotal = 0,
            UploadCheckpointNextIndex = 0,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Add(queueItem);

        repository.CloudSyncLastStatus = "queued";
        repository.CloudSyncLastError = null;
        repository.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
    }

    private async Task<bool> TryQueueLatestSnapshotWhilePausedAsync(int repositoryId, CancellationToken ct)
    {
        if (!runtimeControl.IsPaused)
            return false;

        var profile = await userProfiles.GetActiveProfileAsync(ct);
        if (profile is null)
            return true;

        var repository = await db.Repositories
            .FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted, ct);

        if (repository is null)
            return true;

        var cloudRepositoryId = await EnsureRepositoryCloudIdAsync(repository, ct);

        var snapshots = await db.RepositorySnapshots
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
            .Where(s => db.SnapshotFileLinks.Any(l => l.SnapshotId == s.Id && !l.IsDeleted))
            .OrderBy(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .Select(s => new { s.Id, s.CreatedAt })
            .ToListAsync(ct);

        if (snapshots.Count == 0)
            return true;

        var latestSnapshot = snapshots[^1];
        foreach (var snapshot in snapshots)
        {
            var remoteSnapshotId = BuildRemoteSnapshotId(cloudRepositoryId, snapshot.Id, snapshot.CreatedAt);
            await EnqueueSnapshotPushAsync(
                repository,
                snapshot.Id,
                remoteSnapshotId,
                ct,
                forceRequeueExisting: snapshot.Id == latestSnapshot.Id);
        }

        repository.CloudSyncLastStatus = "paused";
        repository.CloudSyncLastError = null;
        repository.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        log.LogInformation(
            "Queued repository snapshot history for later cloud sync because cloud actions are paused. RepositoryId {RepositoryId}. SnapshotCount {SnapshotCount}. LatestSnapshotId {SnapshotId}",
            repositoryId,
            snapshots.Count,
            latestSnapshot.Id);

        return true;
    }

    private async Task<RepositorySyncQueueItem?> GetNextQueueItemAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        return await db.Set<RepositorySyncQueueItem>()
            .Include(q => q.Repository)
            .Where(q => q.OperationType == PushOperationType)
            .Where(q => !q.Repository.IsDeleted)
            .Where(q =>
                (q.Status == RepositorySyncQueueItem.StatusPending && q.NextAttemptAtUtc <= now)
                || (q.Status == RepositorySyncQueueItem.StatusRetry && q.AttemptCount < q.MaxAttempts && q.NextAttemptAtUtc <= now)
                || (q.Status == RepositorySyncQueueItem.StatusConflict
                    && q.Repository.SyncConflictStrategy != RepositorySyncConflictStrategies.ManualMerge))
            .OrderBy(q => q.NextAttemptAtUtc)
            .ThenBy(q => q.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    private async Task ProcessQueueItemAsync(
        string accessToken,
        long queueItemId,
        RemoteRepositorySnapshotCache remoteSnapshotCache,
        CancellationToken ct)
    {
        var overallTimer = Stopwatch.StartNew();
        var queueItem = await db.Set<RepositorySyncQueueItem>()
            .Include(q => q.Repository)
            .ThenInclude(r => r.Directory)
            .FirstOrDefaultAsync(q => q.Id == queueItemId, ct);

        if (queueItem is null)
            return;

        var repository = queueItem.Repository;
        var now = DateTime.UtcNow;
        using var queueItemCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var itemCt = queueItemCts.Token;

        RegisterActiveQueueItem(queueItemId, repository.Id, queueItemCts);

        queueItem.Status = RepositorySyncQueueItem.StatusRunning;
        queueItem.LastError = null;
        queueItem.UpdatedAt = now;

        repository.CloudSyncLastStatus = "syncing_prepare";
        repository.CloudSyncLastError = null;
        repository.UpdatedAt = now;

        await db.SaveChangesAsync(itemCt);
        log.LogInformation(
            "Cloud queue item started. QueueItemId {QueueItemId}. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}. RemoteSnapshotId {RemoteSnapshotId}. Attempt {Attempt}/{MaxAttempts}. ConflictStrategy {ConflictStrategy}",
            queueItem.Id,
            repository.Id,
            queueItem.SnapshotId,
            queueItem.RemoteSnapshotId,
            Math.Max(0, queueItem.AttemptCount) + 1,
            Math.Clamp(queueItem.MaxAttempts, 1, 20),
            queueItem.ConflictStrategy);

        try
        {
            var cloudRepositoryId = await EnsureRepositoryCloudIdAsync(repository, itemCt);
            var remoteLatestSnapshotId = await GetRemoteLatestSnapshotIdAsync(
                accessToken,
                cloudRepositoryId,
                remoteSnapshotCache,
                itemCt);
            var strategy = RepositorySyncConflictStrategies.Normalize(repository.SyncConflictStrategy);
            var latestLocalSnapshotId = await db.RepositorySnapshots
                .Where(s => s.RepositoryId == repository.Id && !s.IsDeleted)
                .Where(s => db.SnapshotFileLinks.Any(l => l.SnapshotId == s.Id && !l.IsDeleted))
                .OrderByDescending(s => s.CreatedAt)
                .ThenByDescending(s => s.Id)
                .Select(s => (long?)s.Id)
                .FirstOrDefaultAsync(itemCt);
            var isHistoricalBackfill = latestLocalSnapshotId.HasValue && queueItem.SnapshotId != latestLocalSnapshotId.Value;

            var hasConflict = !isHistoricalBackfill
                              && repository.CloudLastRemoteSnapshotId.HasValue
                              && remoteLatestSnapshotId.HasValue
                              && remoteLatestSnapshotId.Value != repository.CloudLastRemoteSnapshotId.Value
                              && remoteLatestSnapshotId.Value != queueItem.RemoteSnapshotId;
            var expectedRemoteSnapshotId = repository.CloudLastRemoteSnapshotId.GetValueOrDefault();
            log.LogInformation(
                "Cloud queue item resolved remote head. QueueItemId {QueueItemId}. RepositoryId {RepositoryId}. ExpectedRemoteSnapshotId {ExpectedRemoteSnapshotId}. ObservedRemoteSnapshotId {ObservedRemoteSnapshotId}. PendingRemoteSnapshotId {PendingRemoteSnapshotId}. HistoricalBackfill {HistoricalBackfill}. HasConflict {HasConflict}. Strategy {Strategy}",
                queueItem.Id,
                repository.Id,
                repository.CloudLastRemoteSnapshotId,
                remoteLatestSnapshotId,
                queueItem.RemoteSnapshotId,
                isHistoricalBackfill,
                hasConflict,
                strategy);

            if (hasConflict && strategy == RepositorySyncConflictStrategies.ManualMerge)
            {
                queueItem.Status = RepositorySyncQueueItem.StatusConflict;
                queueItem.ObservedRemoteSnapshotId = remoteLatestSnapshotId;
                queueItem.LastError = $"Cloud conflict detected. Remote latest snapshot is {remoteLatestSnapshotId.Value}, expected {expectedRemoteSnapshotId}.";
                queueItem.UpdatedAt = DateTime.UtcNow;

                repository.CloudSyncLastStatus = "conflict";
                repository.CloudSyncLastError = queueItem.LastError;
                repository.UpdatedAt = DateTime.UtcNow;
                remoteSnapshotCache.Set(cloudRepositoryId, remoteLatestSnapshotId);

                await db.SaveChangesAsync(itemCt);
                return;
            }

            string? titleSuffix = null;
            if (hasConflict)
            {
                var mergeResult = await ApplyCloudDivergenceMergeAsync(
                    accessToken,
                    repository,
                    cloudRepositoryId,
                    remoteLatestSnapshotId ?? 0,
                    itemCt);

                if (mergeResult.MergeSnapshotId is > 0)
                    queueItem.SnapshotId = mergeResult.MergeSnapshotId.Value;

                queueItem.RemoteSnapshotId = BuildConflictRemoteSnapshotId(
                    cloudRepositoryId,
                    queueItem.SnapshotId,
                    queueItem.RemoteSnapshotId,
                    remoteLatestSnapshotId ?? 0);
                queueItem.ObservedRemoteSnapshotId = remoteLatestSnapshotId;
                titleSuffix = mergeResult.CopiedFiles > 0
                    ? $"cloud_merge_{DateTime.UtcNow:yyyyMMdd_HHmmss}"
                    : $"cloud_rebase_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
                repository.CloudSyncLastStatus = mergeResult.CopiedFiles > 0
                    ? "syncing_merge"
                    : "syncing_rebase";
                repository.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(itemCt);
            }

            var package = await BuildCloudSnapshotPackageAsync(
                repository.Id,
                cloudRepositoryId,
                queueItem.SnapshotId,
                queueItem.RemoteSnapshotId,
                titleSuffix,
                itemCt);

            if (package is null)
            {
                queueItem.Status = RepositorySyncQueueItem.StatusCompleted;
                queueItem.LastError = null;
                queueItem.UpdatedAt = DateTime.UtcNow;

                repository.CloudSyncLastStatus = "skipped";
                repository.CloudSyncLastError = null;
                repository.UpdatedAt = DateTime.UtcNow;

                await db.SaveChangesAsync(itemCt);
                log.LogInformation(
                    "Cloud queue item skipped because snapshot package is empty. QueueItemId {QueueItemId}. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}",
                    queueItem.Id,
                    repository.Id,
                    queueItem.SnapshotId);
                return;
            }

            var runAttempt = Math.Max(0, queueItem.AttemptCount) + 1;
            var initialIdempotencyKey = BuildPushIdempotencyKey(
                cloudRepositoryId,
                queueItem.SnapshotId,
                queueItem.RemoteSnapshotId,
                package.Snapshot.PayloadSha256,
                phase: 0,
                attempt: runAttempt);
            repository.CloudSyncLastStatus = "syncing_snapshot";
            repository.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(itemCt);
            var initialPushTimer = Stopwatch.StartNew();
            var pushResult = await cloudSync.PushSnapshotAsync(accessToken, cloudRepositoryId, package, initialIdempotencyKey, itemCt);
            initialPushTimer.Stop();
            if (pushResult is null)
                throw new InvalidOperationException("Cloud rejected snapshot push (unauthorized or invalid session).");

            var uploaded = 0;
            var missingHashes = pushResult.MissingBlockHashes
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Select(h => h.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
                .ToList();
            log.LogInformation(
                "Cloud queue item initial push completed. QueueItemId {QueueItemId}. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}. MissingBlocks {MissingBlocks}. IdempotencySeed {IdempotencySeed}. DurationMs {DurationMs}",
                queueItem.Id,
                repository.Id,
                queueItem.SnapshotId,
                missingHashes.Count,
                initialIdempotencyKey,
                initialPushTimer.ElapsedMilliseconds);

            if (missingHashes.Count > 0)
            {
                var uploadPhaseTimer = Stopwatch.StartNew();
                var checkpointSignature = BuildMissingCheckpointSignature(package.Snapshot.PayloadSha256, missingHashes);
                var resumeIndex = 0;
                var lastCheckpointPersistedIndex = 0;
                var lastCheckpointPersistedAtUtc = DateTime.UtcNow;

                if (string.Equals(queueItem.UploadCheckpointSignature, checkpointSignature, StringComparison.OrdinalIgnoreCase)
                    && queueItem.UploadCheckpointTotal == missingHashes.Count)
                {
                    resumeIndex = Math.Clamp(queueItem.UploadCheckpointNextIndex, 0, missingHashes.Count);
                    lastCheckpointPersistedIndex = resumeIndex;
                    lastCheckpointPersistedAtUtc = queueItem.UpdatedAt;
                }
                else
                {
                    await PersistUploadCheckpointAsync(
                        queueItem,
                        repository,
                        checkpointSignature,
                        missingHashes.Count,
                        0,
                        itemCt);
                    lastCheckpointPersistedIndex = 0;
                    lastCheckpointPersistedAtUtc = queueItem.UpdatedAt;
                }

                if (resumeIndex > 0)
                {
                    log.LogInformation(
                        "Resuming cloud block upload checkpoint. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}. Progress {Done}/{Total}",
                        repository.Id,
                        queueItem.SnapshotId,
                        resumeIndex,
                        missingHashes.Count);
                }

                var batchUploadSupported = true;
                for (var i = resumeIndex; i < missingHashes.Count;)
                {
                    if (batchUploadSupported)
                    {
                        var batch = BuildUploadBatch(missingHashes, i);
                        if (batch.Count > 1)
                        {
                            try
                            {
                                var uploadItems = batch
                                    .Select(item => new CloudUploadBlockItemDto(item.BlockHash, item.SourcePath, item.ContentLength))
                                    .ToList();
                                var batchResult = await cloudSync.UploadBlockBatchAsync(accessToken, uploadItems, itemCt)
                                                  ?? throw new InvalidOperationException("Cloud batch block upload returned no result.");

                                uploaded += Math.Max(0, batchResult.StoredBlocks + batchResult.SkippedBlocks);
                                var uploadedThroughIndex = batch[^1].Index + 1;
                                if (ShouldLogUploadProgress(uploadedThroughIndex, missingHashes.Count, batch.Count))
                                {
                                    log.LogInformation(
                                        "Cloud queue item uploaded batch of missing blocks. QueueItemId {QueueItemId}. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}. BatchBlocks {BatchBlocks}. Stored {Stored}. Skipped {Skipped}. Progress {Done}/{Total}",
                                        queueItem.Id,
                                        repository.Id,
                                        queueItem.SnapshotId,
                                        batch.Count,
                                        batchResult.StoredBlocks,
                                        batchResult.SkippedBlocks,
                                        uploadedThroughIndex,
                                        missingHashes.Count);
                                }

                                SetUploadCheckpointProgress(
                                    queueItem,
                                    repository,
                                    checkpointSignature,
                                    missingHashes.Count,
                                    uploadedThroughIndex);

                                if (ShouldPersistUploadCheckpoint(
                                        queueItem.UploadCheckpointNextIndex,
                                        missingHashes.Count,
                                        lastCheckpointPersistedIndex,
                                        lastCheckpointPersistedAtUtc,
                                        queueItem.UpdatedAt))
                                {
                                    await db.SaveChangesAsync(itemCt);
                                    lastCheckpointPersistedIndex = queueItem.UploadCheckpointNextIndex;
                                    lastCheckpointPersistedAtUtc = queueItem.UpdatedAt;
                                }

                                i = uploadedThroughIndex;
                                continue;
                            }
                            catch (Exception ex) when (!itemCt.IsCancellationRequested && ShouldFallbackToSingleBlockUpload(ex))
                            {
                                batchUploadSupported = false;
                                log.LogInformation(
                                    ex,
                                    "Cloud batch block upload is unavailable. Falling back to single-block upload. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}. RemainingBlocks {RemainingBlocks}",
                                    repository.Id,
                                    queueItem.SnapshotId,
                                    missingHashes.Count - i);
                            }
                        }
                    }

                    var missingHash = missingHashes[i];
                    if (await TryUploadMissingBlockAsync(accessToken, missingHash, itemCt))
                        uploaded++;
                    log.LogDebug(
                        "Cloud queue item uploaded missing block. QueueItemId {QueueItemId}. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}. BlockIndex {BlockIndex}/{TotalBlocks}. BlockHash {BlockHash}",
                        queueItem.Id,
                        repository.Id,
                        queueItem.SnapshotId,
                        i + 1,
                        missingHashes.Count,
                        missingHash);

                    SetUploadCheckpointProgress(
                        queueItem,
                        repository,
                        checkpointSignature,
                        missingHashes.Count,
                        i + 1);

                    if (ShouldPersistUploadCheckpoint(
                            queueItem.UploadCheckpointNextIndex,
                            missingHashes.Count,
                            lastCheckpointPersistedIndex,
                            lastCheckpointPersistedAtUtc,
                            queueItem.UpdatedAt))
                    {
                        await db.SaveChangesAsync(itemCt);
                        lastCheckpointPersistedIndex = queueItem.UploadCheckpointNextIndex;
                        lastCheckpointPersistedAtUtc = queueItem.UpdatedAt;
                    }

                    i++;
                }

                if (queueItem.UploadCheckpointNextIndex != lastCheckpointPersistedIndex)
                {
                    await db.SaveChangesAsync(itemCt);
                    lastCheckpointPersistedIndex = queueItem.UploadCheckpointNextIndex;
                    lastCheckpointPersistedAtUtc = queueItem.UpdatedAt;
                }

                var confirmIdempotencyKey = BuildPushIdempotencyKey(
                    cloudRepositoryId,
                    queueItem.SnapshotId,
                    queueItem.RemoteSnapshotId,
                    package.Snapshot.PayloadSha256,
                    phase: 1,
                    attempt: runAttempt);

                repository.CloudSyncLastStatus = "syncing_snapshot";
                repository.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(itemCt);
                var confirmPushTimer = Stopwatch.StartNew();
                var confirm = await cloudSync.PushSnapshotAsync(accessToken, cloudRepositoryId, package, confirmIdempotencyKey, itemCt);
                confirmPushTimer.Stop();
                if (confirm is null)
                    throw new InvalidOperationException("Cloud rejected follow-up snapshot push after block upload.");

                var stillMissing = confirm.MissingBlockHashes
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .Select(h => h.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (stillMissing.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Cloud still reports {stillMissing.Count} missing block(s) after upload.");
                }
                log.LogInformation(
                    "Cloud queue item follow-up push confirmed uploaded blocks. QueueItemId {QueueItemId}. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}. UploadedBlocks {UploadedBlocks}. IdempotencySeed {IdempotencySeed}. UploadDurationMs {UploadDurationMs}. ConfirmDurationMs {ConfirmDurationMs}",
                    queueItem.Id,
                    repository.Id,
                    queueItem.SnapshotId,
                    uploaded,
                    confirmIdempotencyKey,
                    uploadPhaseTimer.ElapsedMilliseconds,
                    confirmPushTimer.ElapsedMilliseconds);
            }

            repository.CloudSyncLastStatus = "syncing_finalize";
            repository.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(itemCt);

            queueItem.Status = RepositorySyncQueueItem.StatusCompleted;
            queueItem.LastError = null;
            queueItem.ObservedRemoteSnapshotId = null;
            queueItem.UploadCheckpointSignature = null;
            queueItem.UploadCheckpointTotal = 0;
            queueItem.UploadCheckpointNextIndex = 0;
            queueItem.UpdatedAt = DateTime.UtcNow;

            repository.CloudLastSyncedAt = DateTime.UtcNow;
            repository.CloudLastLocalSnapshotId = queueItem.SnapshotId;
            repository.CloudLastRemoteSnapshotId = queueItem.RemoteSnapshotId;
            repository.CloudSyncLastStatus = hasConflict
                ? $"synced ({RepositorySyncConflictStrategies.ToDisplay(strategy).ToLowerInvariant()})"
                : "synced";
            repository.CloudSyncLastError = null;
            repository.UpdatedAt = DateTime.UtcNow;
            remoteSnapshotCache.Set(cloudRepositoryId, queueItem.RemoteSnapshotId);

            await db.SaveChangesAsync(itemCt);

            log.LogInformation(
                "Cloud queue item completed. RepositoryId {RepositoryId}. LocalSnapshotId {SnapshotId}. RemoteSnapshotId {RemoteSnapshotId}. Missing {Missing}. Uploaded {Uploaded}. TotalDurationMs {TotalDurationMs}",
                repository.Id,
                queueItem.SnapshotId,
                queueItem.RemoteSnapshotId,
                missingHashes.Count,
                uploaded,
                overallTimer.ElapsedMilliseconds);
            cloudAvailability.ReportCloudSuccess();
        }
        catch (OperationCanceledException) when (queueItemCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            if (runtimeControl.IsPaused)
            {
                var affected = await PauseRepositoryQueueAsync(repository.Id, CancellationToken.None);

                log.LogInformation(
                    "Cloud queue item paused. QueueItemId {QueueItemId}. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}. AffectedQueueItems {AffectedQueueItems}",
                    queueItem.Id,
                    repository.Id,
                    queueItem.SnapshotId,
                    affected);
            }
            else
            {
                var affected = await MarkRepositoryQueueCancelledAsync(
                    repository.Id,
                    "Cloud sync cancelled by user.",
                    CancellationToken.None);

                log.LogInformation(
                    "Cloud queue item cancelled by user. QueueItemId {QueueItemId}. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}. AffectedQueueItems {AffectedQueueItems}",
                    queueItem.Id,
                    repository.Id,
                    queueItem.SnapshotId,
                    affected);
            }
        }
        catch (Exception ex)
        {
            cloudAvailability.ReportCloudFailure(ex);
            await MarkQueueFailureAsync(queueItemId, ex, ct);
        }
        finally
        {
            ClearActiveQueueItem(queueItemId);
        }
    }

    private static bool ShouldPersistUploadCheckpoint(
        int currentIndex,
        int totalCount,
        int lastPersistedIndex,
        DateTime lastPersistedAtUtc,
        DateTime currentUpdatedAtUtc)
    {
        if (currentIndex <= lastPersistedIndex)
            return false;

        if (currentIndex >= totalCount)
            return false;

        if (currentIndex - lastPersistedIndex >= UploadCheckpointPersistEveryBlocks)
            return true;

        return currentUpdatedAtUtc - lastPersistedAtUtc >= UploadCheckpointPersistInterval;
    }

    private async Task PersistUploadCheckpointAsync(
        RepositorySyncQueueItem queueItem,
        Repository repository,
        string checkpointSignature,
        int totalCount,
        int nextIndex,
        CancellationToken ct)
    {
        SetUploadCheckpointProgress(queueItem, repository, checkpointSignature, totalCount, nextIndex);

        await db.SaveChangesAsync(ct);
    }

    private void RegisterActiveQueueItem(long queueItemId, int repositoryId, CancellationTokenSource cts)
    {
        lock (_activeQueueItemStateGate)
        {
            _activeQueueItemId = queueItemId;
            _activeQueueRepositoryId = repositoryId;
            _activeQueueItemCts = cts;
        }
    }

    private void ClearActiveQueueItem(long queueItemId)
    {
        lock (_activeQueueItemStateGate)
        {
            if (_activeQueueItemId != queueItemId)
                return;

            _activeQueueItemId = null;
            _activeQueueRepositoryId = null;
            _activeQueueItemCts = null;
        }
    }

    private bool CancelActiveQueueItemIfMatches(int repositoryId)
    {
        lock (_activeQueueItemStateGate)
        {
            if (_activeQueueRepositoryId != repositoryId || _activeQueueItemCts is null)
                return false;

            _activeQueueItemCts.Cancel();
            return true;
        }
    }

    private async Task<int> MarkRepositoryQueueCancelledAsync(
        int repositoryId,
        string reason,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var affected = 0;

        var queueItems = await db.Set<RepositorySyncQueueItem>()
            .Include(q => q.Repository)
            .Where(q => q.RepositoryId == repositoryId
                        && q.OperationType == PushOperationType
                        && (q.Status == RepositorySyncQueueItem.StatusPending
                            || q.Status == RepositorySyncQueueItem.StatusRunning
                            || q.Status == RepositorySyncQueueItem.StatusRetry))
            .ToListAsync(ct);

        foreach (var queueItem in queueItems)
        {
            queueItem.Status = RepositorySyncQueueItem.StatusCancelled;
            queueItem.LastError = TruncateForColumn(reason, 2048);
            queueItem.NextAttemptAtUtc = now;
            queueItem.ObservedRemoteSnapshotId = null;
            queueItem.UploadCheckpointSignature = null;
            queueItem.UploadCheckpointTotal = 0;
            queueItem.UploadCheckpointNextIndex = 0;
            queueItem.UpdatedAt = now;
            affected++;
        }

        var repository = await db.Repositories
            .FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted, ct);

        if (repository is not null)
        {
            repository.CloudSyncLastStatus = RepositorySyncQueueItem.StatusCancelled;
            repository.CloudSyncLastError = null;
            repository.UpdatedAt = now;
        }

        if (affected > 0 || repository is not null)
            await db.SaveChangesAsync(ct);

        return affected;
    }

    private async Task<int> PauseRepositoryQueueAsync(
        int repositoryId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var affected = 0;

        var queueItems = await db.Set<RepositorySyncQueueItem>()
            .Include(q => q.Repository)
            .Where(q => q.RepositoryId == repositoryId
                        && q.OperationType == PushOperationType
                        && (q.Status == RepositorySyncQueueItem.StatusPending
                            || q.Status == RepositorySyncQueueItem.StatusRunning
                            || q.Status == RepositorySyncQueueItem.StatusRetry))
            .ToListAsync(ct);

        foreach (var queueItem in queueItems)
        {
            if (queueItem.Status == RepositorySyncQueueItem.StatusRunning)
                queueItem.Status = RepositorySyncQueueItem.StatusRetry;

            queueItem.LastError = null;
            queueItem.NextAttemptAtUtc = now;
            queueItem.UpdatedAt = now;
            affected++;
        }

        var repository = queueItems.Select(q => q.Repository).FirstOrDefault()
                         ?? await db.Repositories.FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted, ct);

        if (repository is not null)
        {
            repository.CloudSyncLastStatus = "paused";
            repository.CloudSyncLastError = null;
            repository.UpdatedAt = now;
        }

        if (affected > 0 || repository is not null)
            await db.SaveChangesAsync(ct);

        return affected;
    }

    private async Task ApplyPausedStatusToQueuedRepositoriesAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var repositoryIds = await db.Set<RepositorySyncQueueItem>()
            .Where(q => q.OperationType == PushOperationType)
            .Where(q => q.Status == RepositorySyncQueueItem.StatusPending
                        || q.Status == RepositorySyncQueueItem.StatusRunning
                        || q.Status == RepositorySyncQueueItem.StatusRetry)
            .Select(q => q.RepositoryId)
            .Distinct()
            .ToListAsync(ct);

        if (repositoryIds.Count == 0)
            return;

        var repositoriesToUpdate = await db.Repositories
            .Where(r => !r.IsDeleted && repositoryIds.Contains(r.Id))
            .ToListAsync(ct);

        foreach (var repository in repositoriesToUpdate)
        {
            repository.CloudSyncLastStatus = "paused";
            repository.CloudSyncLastError = null;
            repository.UpdatedAt = now;
        }

        if (repositoriesToUpdate.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    private async Task ApplyCloudUnavailableStatusToQueuedRepositoriesAsync(string reason, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var repositoryIds = await db.Set<RepositorySyncQueueItem>()
            .Where(q => q.OperationType == PushOperationType)
            .Where(q => q.Status == RepositorySyncQueueItem.StatusPending
                        || q.Status == RepositorySyncQueueItem.StatusRunning
                        || q.Status == RepositorySyncQueueItem.StatusRetry)
            .Select(q => q.RepositoryId)
            .Distinct()
            .ToListAsync(ct);

        if (repositoryIds.Count == 0)
            return;

        var repositoriesToUpdate = await db.Repositories
            .Where(r => !r.IsDeleted && repositoryIds.Contains(r.Id))
            .ToListAsync(ct);

        foreach (var repository in repositoriesToUpdate)
        {
            repository.CloudSyncLastStatus = "offline_retry";
            repository.CloudSyncLastError = TruncateForColumn(reason, 2048);
            repository.UpdatedAt = now;
        }

        if (repositoriesToUpdate.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    private void CancelActiveQueueItemForPause()
    {
        lock (_activeQueueItemStateGate)
        {
            _activeQueueItemCts?.Cancel();
        }
    }

    private async Task MarkQueueFailureAsync(long queueItemId, Exception ex, CancellationToken ct)
    {
        var queueItem = await db.Set<RepositorySyncQueueItem>()
            .Include(q => q.Repository)
            .FirstOrDefaultAsync(q => q.Id == queueItemId, ct);

        if (queueItem is null)
            return;

        if (queueItem.Status == RepositorySyncQueueItem.StatusCancelled)
        {
            log.LogInformation(
                "Cloud queue item failure handler observed an already-cancelled item. QueueItemId {QueueItemId}. RepositoryId {RepositoryId}",
                queueItem.Id,
                queueItem.RepositoryId);
            return;
        }

        var repository = queueItem.Repository;
        var message = TruncateForColumn(GetInnermostMessage(ex), 2048);
        var authFailure = IsAuthFailure(ex, message);
        var connectivityFailure = IsConnectivityFailure(ex);
        var retryable = !authFailure;

        var maxAttempts = Math.Clamp(queueItem.MaxAttempts, 1, 20);
        if (retryable)
            queueItem.AttemptCount = Math.Max(0, queueItem.AttemptCount) + 1;

        var hasAttemptsLeft = queueItem.AttemptCount < maxAttempts;
        if (retryable && hasAttemptsLeft)
        {
            queueItem.Status = RepositorySyncQueueItem.StatusRetry;
            queueItem.NextAttemptAtUtc = DateTime.UtcNow + ComputeRetryDelay(repository, queueItem.AttemptCount);
            repository.CloudSyncLastStatus = connectivityFailure ? "offline_retry" : "retrying";
        }
        else
        {
            queueItem.Status = authFailure
                ? RepositorySyncQueueItem.StatusFailed
                : RepositorySyncQueueItem.StatusDeadLetter;
            queueItem.NextAttemptAtUtc = DateTime.UtcNow;
            repository.CloudSyncLastStatus = authFailure ? "auth_required" : "dead_letter";
        }

        queueItem.LastError = message;
        queueItem.UpdatedAt = DateTime.UtcNow;

        repository.CloudSyncLastError = queueItem.LastError;
        repository.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        log.LogWarning(
            ex,
            "Cloud queue item failed. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}. Attempt {Attempt}/{MaxAttempts}. Status {Status}",
            repository.Id,
            queueItem.SnapshotId,
            queueItem.AttemptCount,
            maxAttempts,
            queueItem.Status);
    }

    private static bool IsConnectivityFailure(Exception ex)
    {
        if (ex is IOException or TimeoutException or TaskCanceledException)
            return true;

        if (ex.GetType().Name.Contains("HttpRequestException", StringComparison.OrdinalIgnoreCase))
            return true;

        return ex.InnerException is not null && IsConnectivityFailure(ex.InnerException);
    }

    private static bool IsAuthFailure(Exception ex, string message)
    {
        if (message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || message.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
            || message.Contains("invalid session", StringComparison.OrdinalIgnoreCase)
            || message.Contains("token", StringComparison.OrdinalIgnoreCase))
            return true;

        return ex.InnerException is not null && IsAuthFailure(ex.InnerException, ex.InnerException.Message);
    }

    private static string GetInnermostMessage(Exception ex)
    {
        var current = ex;
        while (current.InnerException is not null)
            current = current.InnerException;

        return string.IsNullOrWhiteSpace(current.Message) ? ex.Message : current.Message;
    }

    private async Task<long?> GetRemoteLatestSnapshotIdAsync(
        string accessToken,
        int repositoryId,
        RemoteRepositorySnapshotCache cache,
        CancellationToken ct)
    {
        if (cache.TryGet(repositoryId, out var cachedSnapshotId))
            return cachedSnapshotId;

        var remoteRepositories = await cloudSync.GetRepositoriesAsync(accessToken, ct);
        cache.Refresh(remoteRepositories);

        return cache.TryGet(repositoryId, out var refreshedSnapshotId)
            ? refreshedSnapshotId
            : null;
    }

    private async Task<CloudSnapshotPackageDto?> BuildCloudSnapshotPackageAsync(
        int repositoryId,
        int cloudRepositoryId,
        long localSnapshotId,
        long remoteSnapshotId,
        string? titleSuffix,
        CancellationToken ct)
    {
        var totalTimer = Stopwatch.StartNew();

        var headerTimer = Stopwatch.StartNew();
        var repository = await db.Repositories
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted, ct);

        if (repository is null)
        {
            log.LogWarning(
                "Cloud snapshot package build skipped because repository was not found. RepositoryId {RepositoryId}. LocalSnapshotId {LocalSnapshotId}",
                repositoryId,
                localSnapshotId);
            return null;
        }

        var snapshot = await db.RepositorySnapshots
            .AsNoTracking()
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted && s.Id == localSnapshotId)
            .FirstOrDefaultAsync(ct);
        headerTimer.Stop();

        if (snapshot is null)
        {
            log.LogWarning(
                "Cloud snapshot package build skipped because snapshot was not found. RepositoryId {RepositoryId}. LocalSnapshotId {LocalSnapshotId}. HeaderLoadMs {HeaderLoadMs}",
                repositoryId,
                localSnapshotId,
                headerTimer.ElapsedMilliseconds);
            return null;
        }

        var entriesTimer = Stopwatch.StartNew();
        var entries = await db.RepositorySnapshotEntries
            .AsNoTracking()
            .Where(e => e.RepositoryId == repositoryId && e.SnapshotId == snapshot.Id && !e.IsDeleted)
            .OrderBy(e => e.RelativePath)
            .Select(e => new CloudSnapshotEntryDto(
                e.RelativePath,
                e.ParentRelativePath,
                e.Name,
                e.IsDirectory,
                e.Extension,
                e.SizeBytes,
                e.LastWriteUtc,
                e.ContentHashSha256))
            .ToListAsync(ct);
        entriesTimer.Stop();

        var fileVersionMetadataTimer = Stopwatch.StartNew();
        var fileVersionRows = await db.SnapshotFileLinks
            .AsNoTracking()
            .Where(l => l.SnapshotId == snapshot.Id && !l.IsDeleted)
            .Where(l => !l.FileIdentity.IsDeleted && !l.FileVersion.IsDeleted)
            .OrderBy(l => l.FileIdentity.RelativePath)
            .Select(l => new
            {
                RelativePath = l.FileIdentity.RelativePath,
                l.FileVersionId,
                l.FileVersion.ContentHashSha256,
                l.FileVersion.SizeBytes,
                l.FileVersion.IsDeletionMarker,
                CreatedAtUtc = l.FileVersion.CreatedAt
            })
            .ToListAsync(ct);
        fileVersionMetadataTimer.Stop();

        if (fileVersionRows.Count == 0)
        {
            log.LogInformation(
                "Cloud snapshot package build finished without file versions. RepositoryId {RepositoryId}. LocalSnapshotId {LocalSnapshotId}. Entries {Entries}. HeaderLoadMs {HeaderLoadMs}. EntryLoadMs {EntryLoadMs}. FileVersionLoadMs {FileVersionLoadMs}. TotalMs {TotalMs}",
                repositoryId,
                localSnapshotId,
                entries.Count,
                headerTimer.ElapsedMilliseconds,
                entriesTimer.ElapsedMilliseconds,
                fileVersionMetadataTimer.ElapsedMilliseconds,
                totalTimer.ElapsedMilliseconds);
            return null;
        }

        var fileVersionIds = fileVersionRows
            .Select(row => row.FileVersionId)
            .Distinct()
            .ToList();

        var blockLoadTimer = Stopwatch.StartNew();
        var blockRows = await db.Set<FileVersionBlock>()
            .AsNoTracking()
            .Where(b => !b.IsDeleted && fileVersionIds.Contains(b.FileVersionId))
            .OrderBy(b => b.FileVersionId)
            .ThenBy(b => b.Sequence)
            .Select(b => new
            {
                b.FileVersionId,
                Block = new CloudBlockRefDto(
                    b.Sequence,
                    b.BlockStorageKey,
                    b.LengthBytes,
                    b.StoredSizeBytes)
            })
            .ToListAsync(ct);
        blockLoadTimer.Stop();

        var assemblyTimer = Stopwatch.StartNew();
        var blockLookup = blockRows
            .GroupBy(row => row.FileVersionId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CloudBlockRefDto>)group
                    .Select(row => row.Block)
                    .ToList());

        var fileVersions = fileVersionRows
            .Select(row => new CloudFileVersionDto(
                row.RelativePath,
                row.FileVersionId,
                row.ContentHashSha256,
                row.SizeBytes,
                row.IsDeletionMarker,
                row.CreatedAtUtc,
                blockLookup.TryGetValue(row.FileVersionId, out var blocks)
                    ? blocks
                    : Array.Empty<CloudBlockRefDto>()))
            .ToList();
        assemblyTimer.Stop();

        var snapshotTitle = AppendTitleSuffix(snapshot.Title, titleSuffix);
        var blockCount = blockRows.Count;
        log.LogInformation(
            "Built cloud snapshot package. RepositoryId {RepositoryId}. LocalSnapshotId {LocalSnapshotId}. RemoteSnapshotId {RemoteSnapshotId}. Entries {Entries}. FileVersions {FileVersions}. Blocks {Blocks}. TitleSuffix {TitleSuffix}. HeaderLoadMs {HeaderLoadMs}. EntryLoadMs {EntryLoadMs}. FileVersionLoadMs {FileVersionLoadMs}. BlockLoadMs {BlockLoadMs}. AssemblyMs {AssemblyMs}. TotalMs {TotalMs}",
            repositoryId,
            localSnapshotId,
            remoteSnapshotId,
            entries.Count,
            fileVersions.Count,
            blockCount,
            titleSuffix ?? "(none)",
            headerTimer.ElapsedMilliseconds,
            entriesTimer.ElapsedMilliseconds,
            fileVersionMetadataTimer.ElapsedMilliseconds,
            blockLoadTimer.ElapsedMilliseconds,
            assemblyTimer.ElapsedMilliseconds,
            totalTimer.ElapsedMilliseconds);

        return new CloudSnapshotPackageDto(
            new CloudRepositoryMetadataDto(cloudRepositoryId, repository.Name, repository.Description),
            new CloudSnapshotMetadataDto(
                remoteSnapshotId,
                snapshotTitle,
                snapshot.Trigger,
                snapshot.CreatedAt,
                snapshot.TotalEntries,
                snapshot.FileEntries,
                snapshot.DirectoryEntries,
                snapshot.TotalFileBytes,
                BuildPayloadSha(snapshot, entries.Count, fileVersions.Count, remoteSnapshotId)),
            entries,
            fileVersions,
            repository.ProtectCloudMetadata);
    }

    private async Task RestoreFilesFromPackageAsync(
        string accessToken,
        CloudSnapshotPackageDto package,
        string restoreRoot,
        CancellationToken ct)
    {
        foreach (var entry in package.Entries.Where(e => e.IsDirectory))
        {
            var dirPath = ResolvePath(restoreRoot, entry.RelativePath);
            if (dirPath is null)
                continue;

            Directory.CreateDirectory(dirPath);
        }

        var latestByPath = package.FileVersions
            .GroupBy(v => v.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(v => v.CreatedAtUtc)
                .ThenByDescending(v => v.FileVersionId)
                .First())
            .ToList();

        var requiredBlockHashes = latestByPath
            .Where(v => !v.IsDeletionMarker)
            .SelectMany(v => v.Blocks)
            .Select(b => b.BlockHash)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var failedRestoreBlocks = await EnsureLocalBlocksForRestoreAsync(
            accessToken,
            package.Repository.Name,
            requiredBlockHashes,
            ct);

        foreach (var version in latestByPath)
        {
            var targetPath = ResolvePath(restoreRoot, version.RelativePath);
            if (targetPath is null)
                continue;

            if (version.IsDeletionMarker)
            {
                if (File.Exists(targetPath))
                    File.Delete(targetPath);
                continue;
            }

            var parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);

            var missingBlock = version.Blocks
                .OrderBy(b => b.Sequence)
                .FirstOrDefault(b => failedRestoreBlocks.Contains(b.BlockHash));

            if (missingBlock is not null)
            {
                log.LogWarning(
                    "Missing block during cloud restore. Repo {Repository}. File {File}. Block {BlockHash}",
                    package.Repository.Name,
                    version.RelativePath,
                    missingBlock.BlockHash);
                continue;
            }

            var blocks = version.Blocks
                .OrderBy(b => b.Sequence)
                .Select(block => new StoredFileBlockDto(
                    block.Sequence,
                    block.BlockHash,
                    block.LengthBytes,
                    block.StoredSizeBytes))
                .ToList();

            await fileContentStore.RestoreFileAsync(
                blocks,
                targetPath,
                overwriteExisting: true,
                ct: ct);
        }
    }

    private async Task<HashSet<string>> EnsureLocalBlocksForRestoreAsync(
        string accessToken,
        string repositoryName,
        IReadOnlyList<string> blockHashes,
        CancellationToken ct)
    {
        if (blockHashes.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var failedBlocks = new ConcurrentBag<string>();
        var parallelism = Math.Max(1, Math.Min(RestoreDownloadMaxConcurrency, blockHashes.Count));

        await Parallel.ForEachAsync(
            blockHashes,
            new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = parallelism
            },
            async (blockHash, itemCt) =>
            {
                try
                {
                    var localOk = await EnsureLocalBlockAsync(accessToken, blockHash, itemCt);
                    if (localOk)
                        return;

                    failedBlocks.Add(blockHash);
                }
                catch (Exception ex)
                {
                    log.LogWarning(
                        ex,
                        "Failed to download block during cloud restore prefetch. Repository {Repository}. Block {BlockHash}",
                        repositoryName,
                        blockHash);
                    failedBlocks.Add(blockHash);
                }
            });

        var failedSet = failedBlocks.ToHashSet(StringComparer.OrdinalIgnoreCase);
        log.LogInformation(
            "Cloud restore block prefetch completed. Repository {Repository}. RequiredBlocks {RequiredBlocks}. FailedBlocks {FailedBlocks}. Parallelism {Parallelism}",
            repositoryName,
            blockHashes.Count,
            failedSet.Count,
            parallelism);

        return failedSet;
    }

    private async Task<bool> EnsureLocalBlockAsync(string accessToken, string blockHash, CancellationToken ct)
    {
        var localPath = ResolveLocalBlockPath(blockHash);
        if (string.IsNullOrWhiteSpace(localPath))
            return false;

        if (File.Exists(localPath))
            return true;

        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        return await cloudSync.DownloadBlockToFileAsync(accessToken, blockHash, localPath, ct);
    }

    private async Task<bool> TryUploadMissingBlockAsync(string accessToken, string blockHash, CancellationToken ct)
    {
        var path = ResolveLocalBlockPath(blockHash);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        await cloudSync.UploadBlockAsync(accessToken, blockHash, stream, stream.Length, ct);
        return true;
    }

    private async Task<(List<string> MissingHashes, int AlreadyPresent, int FailedProbes)> ProbeRemoteBlocksForRepairAsync(
        string accessToken,
        int repositoryId,
        IReadOnlyList<string> blockHashes,
        CancellationToken ct)
    {
        if (blockHashes.Count == 0)
            return ([], 0, 0);

        var missingHashes = new ConcurrentBag<string>();
        var alreadyPresent = 0;
        var failedProbes = 0;
        var parallelism = Math.Max(1, Math.Min(RepairProbeMaxConcurrency, blockHashes.Count));

        await Parallel.ForEachAsync(
            blockHashes,
            new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = parallelism
            },
            async (blockHash, itemCt) =>
            {
                try
                {
                    if (await cloudSync.BlockExistsAsync(accessToken, blockHash, itemCt))
                    {
                        Interlocked.Increment(ref alreadyPresent);
                        return;
                    }

                    missingHashes.Add(blockHash);
                }
                catch (Exception ex)
                {
                    log.LogWarning(
                        ex,
                        "Failed to probe remote block presence during repository repair. RepositoryId {RepositoryId}. Block {BlockHash}",
                        repositoryId,
                        blockHash);
                    Interlocked.Increment(ref failedProbes);
                }
            });

        var orderedMissing = missingHashes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
            .ToList();

        log.LogInformation(
            "Repository cloud repair remote probe completed. RepositoryId {RepositoryId}. Referenced {Referenced}. Present {Present}. Missing {Missing}. ProbeFailures {ProbeFailures}. Parallelism {Parallelism}",
            repositoryId,
            blockHashes.Count,
            alreadyPresent,
            orderedMissing.Count,
            failedProbes,
            parallelism);

        return (orderedMissing, alreadyPresent, failedProbes);
    }

    private async Task<(int Uploaded, int MissingLocal, int FailedUploads)> UploadMissingBlocksForRepairAsync(
        string accessToken,
        int repositoryId,
        IReadOnlyList<string> missingHashes,
        CancellationToken ct)
    {
        if (missingHashes.Count == 0)
            return (0, 0, 0);

        var uploaded = 0;
        var missingLocal = 0;
        var failedUploads = 0;
        var batchUploadSupported = true;

        for (var i = 0; i < missingHashes.Count;)
        {
            ct.ThrowIfCancellationRequested();

            if (batchUploadSupported)
            {
                var batch = BuildUploadBatch(missingHashes, i);
                if (batch.Count > 1)
                {
                    try
                    {
                        var uploadItems = batch
                            .Select(item => new CloudUploadBlockItemDto(item.BlockHash, item.SourcePath, item.ContentLength))
                            .ToList();
                        var batchResult = await cloudSync.UploadBlockBatchAsync(accessToken, uploadItems, ct)
                                          ?? throw new InvalidOperationException("Cloud batch block upload returned no result.");

                        uploaded += Math.Max(0, batchResult.StoredBlocks + batchResult.SkippedBlocks);
                        var uploadedThroughIndex = batch[^1].Index + 1;
                        if (ShouldLogUploadProgress(uploadedThroughIndex, missingHashes.Count, batch.Count))
                        {
                            log.LogInformation(
                                "Repository cloud repair uploaded batch of missing blocks. RepositoryId {RepositoryId}. BatchBlocks {BatchBlocks}. Stored {Stored}. Skipped {Skipped}. Progress {Done}/{Total}",
                                repositoryId,
                                batch.Count,
                                batchResult.StoredBlocks,
                                batchResult.SkippedBlocks,
                                uploadedThroughIndex,
                                missingHashes.Count);
                        }

                        i = uploadedThroughIndex;
                        continue;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested && ShouldFallbackToSingleBlockUpload(ex))
                    {
                        batchUploadSupported = false;
                        log.LogInformation(
                            ex,
                            "Cloud batch block upload is unavailable during repository repair. Falling back to single-block upload. RepositoryId {RepositoryId}. RemainingBlocks {RemainingBlocks}",
                            repositoryId,
                            missingHashes.Count - i);
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(
                            ex,
                            "Cloud batch block upload failed during repository repair. Falling back to single-block uploads for the current batch. RepositoryId {RepositoryId}. RemainingBlocks {RemainingBlocks}",
                            repositoryId,
                            missingHashes.Count - i);
                    }
                }
            }

            var missingHash = missingHashes[i];
            try
            {
                var uploadedNow = await TryUploadMissingBlockAsync(accessToken, missingHash, ct);
                if (uploadedNow)
                    uploaded++;
                else
                    missingLocal++;
            }
            catch (Exception ex)
            {
                log.LogWarning(
                    ex,
                    "Failed to upload missing block during repository repair. RepositoryId {RepositoryId}. Block {BlockHash}",
                    repositoryId,
                    missingHash);
                failedUploads++;
            }

            i++;
        }

        return (uploaded, missingLocal, failedUploads);
    }

    private static bool ShouldFallbackToSingleBlockUpload(Exception ex)
    {
        if (ex is NotSupportedException)
            return true;

        if (ex is IOException or TimeoutException or TaskCanceledException)
            return true;

        if (ex is HttpRequestException http)
        {
            if (http.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
                return true;

            if (http.StatusCode is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout &&
                ex.Message.Contains("upload_block_batch", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (ex.Message.Contains("copying content", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("transport connection", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("failed to persist block payload", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("failed to read block payload", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("failed to read multipart block batch", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return ex.InnerException is not null && ShouldFallbackToSingleBlockUpload(ex.InnerException);
    }

    private static bool ShouldLogUploadProgress(int uploadedThroughIndex, int totalCount, int batchCount)
    {
        if (uploadedThroughIndex >= totalCount)
            return true;

        if (uploadedThroughIndex <= batchCount)
            return true;

        return uploadedThroughIndex % UploadBatchProgressLogEveryBlocks < batchCount;
    }

    private static void SetUploadCheckpointProgress(
        RepositorySyncQueueItem queueItem,
        Repository repository,
        string checkpointSignature,
        int totalCount,
        int nextIndex)
    {
        queueItem.UploadCheckpointSignature = checkpointSignature;
        queueItem.UploadCheckpointTotal = totalCount;
        queueItem.UploadCheckpointNextIndex = Math.Clamp(nextIndex, 0, totalCount);
        queueItem.UpdatedAt = DateTime.UtcNow;
        repository.CloudSyncLastStatus = nextIndex > 0
            ? $"syncing_upload {queueItem.UploadCheckpointNextIndex}/{totalCount}"
            : "syncing_upload";
        repository.UpdatedAt = queueItem.UpdatedAt;
    }

    private List<PendingBlockUploadItem> BuildUploadBatch(IReadOnlyList<string> missingHashes, int startIndex)
    {
        var items = new List<PendingBlockUploadItem>(Math.Min(UploadBatchMaxBlocks, Math.Max(0, missingHashes.Count - startIndex)));
        long totalBytes = 0;

        for (var i = startIndex; i < missingHashes.Count && items.Count < UploadBatchMaxBlocks; i++)
        {
            var blockHash = missingHashes[i];
            var path = ResolveLocalBlockPath(blockHash);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                break;
            }

            var contentLength = new FileInfo(path).Length;
            if (items.Count > 0 && totalBytes + contentLength > UploadBatchMaxBytes)
                break;

            items.Add(new PendingBlockUploadItem(i, blockHash, path, contentLength));
            totalBytes += contentLength;
        }

        return items;
    }

    private string? ResolveLocalBlockPath(string blockHash)
    {
        var root = ResolveBlockStoreRoot();

        if (blockHash.StartsWith(ManagedHashPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var hash = blockHash[ManagedHashPrefix.Length..].Trim().ToLowerInvariant();
            if (hash.Length < 4)
                return null;

            var p1 = hash[..2];
            var p2 = hash[2..4];
            return Path.Combine(root, "managed", "blocks", p1, p2, hash + ".bin");
        }

        var safe = blockHash.Trim().ToLowerInvariant()
            .Replace('/', '_')
            .Replace('\\', '_');

        if (safe.Length < 4)
            return null;

        var nativeCandidate = Path.Combine(root, "blocks", safe[..2], safe[2..4], safe + ".zst");
        if (File.Exists(nativeCandidate))
            return nativeCandidate;

        var managedCandidate = Path.Combine(root, "managed", "blocks", safe[..2], safe[2..4], safe + ".bin");
        if (File.Exists(managedCandidate))
            return managedCandidate;

        var legacyCandidate = Path.Combine(root, safe[..2], safe[2..4], safe + ".bin");
        if (File.Exists(legacyCandidate))
            return legacyCandidate;

        return nativeCandidate;
    }

    private string ResolveBlockStoreRoot()
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

    private long BuildRemoteSnapshotId(int repositoryId, long localSnapshotId, DateTime createdAtUtc)
    {
        var payload = $"{_clientInstanceId}|{repositoryId}|{localSnapshotId}|{createdAtUtc:O}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        var raw = BitConverter.ToInt64(hash, 0) & long.MaxValue;
        return raw == 0 ? Math.Max(1, localSnapshotId) : raw;
    }

    private long BuildConflictRemoteSnapshotId(
        int repositoryId,
        long localSnapshotId,
        long baseRemoteSnapshotId,
        long observedRemoteSnapshotId)
    {
        var payload = $"{_clientInstanceId}|conflict|{repositoryId}|{localSnapshotId}|{baseRemoteSnapshotId}|{observedRemoteSnapshotId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        var raw = BitConverter.ToInt64(hash, 0) & long.MaxValue;
        return raw == 0 ? Math.Max(1, baseRemoteSnapshotId) : raw;
    }

    private static string BuildPushIdempotencyKey(
        int repositoryId,
        long localSnapshotId,
        long remoteSnapshotId,
        string? payloadSha,
        int phase,
        int attempt)
    {
        var payload = $"push|{repositoryId}|{localSnapshotId}|{remoteSnapshotId}|{payloadSha ?? "none"}|{phase}|a{attempt}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        var compact = Convert.ToHexString(digest).ToLowerInvariant();
        return $"push-{repositoryId}-{localSnapshotId}-p{phase}-{compact[..24]}";
    }

    private static string BuildMissingCheckpointSignature(
        string? payloadSha,
        IReadOnlyList<string> missingHashes)
    {
        var normalizedPayload = string.IsNullOrWhiteSpace(payloadSha) ? "none" : payloadSha.Trim().ToLowerInvariant();
        var normalizedHashes = missingHashes
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim().ToLowerInvariant())
            .OrderBy(h => h, StringComparer.Ordinal)
            .ToArray();

        var materialized = $"{normalizedPayload}|{string.Join("|", normalizedHashes)}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(materialized));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string BuildPayloadSha(RepositorySnapshot snapshot, int entries, int fileVersions, long remoteSnapshotId)
    {
        var raw = $"{snapshot.Id}|{remoteSnapshotId}|{snapshot.CreatedAt:O}|{snapshot.Trigger}|{entries}|{fileVersions}|{snapshot.TotalFileBytes}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string? AppendTitleSuffix(string? title, string? suffix)
    {
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        if (string.IsNullOrWhiteSpace(suffix))
            return normalizedTitle;

        var combined = string.IsNullOrWhiteSpace(normalizedTitle)
            ? suffix.Trim()
            : $"{normalizedTitle} [{suffix.Trim()}]";

        return combined.Length <= 256 ? combined : combined[..256];
    }

    private static TimeSpan ComputeRetryDelay(Repository repository, int attempt)
    {
        var baseDelay = Math.Clamp(repository.SyncRetryBaseDelaySeconds, 5, 600);
        var exponent = Math.Clamp(attempt - 1, 0, 6);
        var seconds = baseDelay * Math.Pow(2, exponent);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 7200));
    }

    private static string ResolveClientInstanceId(IConfiguration cfg)
    {
        var explicitId = cfg["CloudSync:ClientInstanceId"];
        if (!string.IsNullOrWhiteSpace(explicitId))
            return explicitId.Trim();

        var machine = Environment.MachineName;
        var user = Environment.UserName;
        var payload = $"{machine}|{user}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string TruncateForColumn(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private sealed record PreparedCloudRepositoryRestore(
        int SortOrder,
        int CloudRepositoryId,
        long? LatestRemoteSnapshotId,
        string RepositoryName,
        string? RepositoryDescription,
        string RestorePath,
        IReadOnlyList<string> Formats);

    private sealed record CloudDivergenceMergeResult(
        int CopiedFiles,
        long? MergeSnapshotId);

    private static string BuildRestorePath(string? targetRootDirectory, string username, string repositoryName)
    {
        var safeRepo = SanitizeSegment(repositoryName);
        var safeUser = SanitizeSegment(username);

        var root = string.IsNullOrWhiteSpace(targetRootDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VeyraFlow",
                "cloud-restored")
            : Path.GetFullPath(targetRootDirectory.Trim());

        return Path.Combine(root, safeUser, safeRepo);
    }

    private static string BuildCloudMergeCopyPath(string originalPath, long remoteSnapshotId)
    {
        var directory = Path.GetDirectoryName(originalPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(originalPath);
        if (string.IsNullOrWhiteSpace(name))
            name = Path.GetFileName(originalPath);

        var extension = Path.GetExtension(originalPath);
        var suffix = $".cloud-merge-s{remoteSnapshotId}";
        var candidate = Path.Combine(directory, $"{name}{suffix}{extension}");
        if (!File.Exists(candidate))
            return candidate;

        for (var i = 2; i < 10_000; i++)
        {
            candidate = Path.Combine(directory, $"{name}{suffix}-{i}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(directory, $"{name}{suffix}-{Guid.NewGuid():N}{extension}");
    }

    private static string ResolveSingleRepositoryRestorePath(
        string? requestedPath,
        string repositoryName,
        string? originalPath,
        bool restoreToAnotherFolder)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            var selected = Path.GetFullPath(requestedPath.Trim());
            if (!restoreToAnotherFolder)
                return selected;

            var safeRepo = SanitizeSegment(repositoryName);
            var selectedName = Path.GetFileName(selected.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.Equals(selectedName, safeRepo, StringComparison.OrdinalIgnoreCase)
                ? selected
                : Path.Combine(selected, safeRepo);
        }

        if (restoreToAnotherFolder)
            return BuildRestorePath(null, Environment.UserName, repositoryName);

        if (!string.IsNullOrWhiteSpace(originalPath))
            return Path.GetFullPath(originalPath);

        return BuildRestorePath(null, Environment.UserName, repositoryName);
    }

    private static string SanitizeSegment(string value)
    {
        var cleaned = new string(value
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)
            .ToArray())
            .Trim();

        return string.IsNullOrWhiteSpace(cleaned) ? "repository" : cleaned;
    }

    private static string NormalizeFormat(string? extension)
    {
        return KnownFileExtensions.NormalizeTrackedFileFormat(extension);
    }

    private static string? ResolvePath(string root, string relativePath)
    {
        var normalized = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var full = Path.GetFullPath(Path.Combine(root, normalized));
        if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            return null;

        return full;
    }

    private static string NormalizeRelativePath(string value)
        => value.Trim().Replace('\\', '/').Trim('/');

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024,
            useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class RemoteRepositorySnapshotCache
    {
        private readonly Dictionary<int, long?> _latestSnapshotIds = new();
        private DateTime _lastRefreshUtc = DateTime.MinValue;

        public bool TryGet(int repositoryId, out long? latestSnapshotId)
        {
            if (DateTime.UtcNow - _lastRefreshUtc > RemoteRepositoryCacheTtl)
            {
                latestSnapshotId = null;
                return false;
            }

            if (_latestSnapshotIds.TryGetValue(repositoryId, out latestSnapshotId))
                return true;

            latestSnapshotId = null;
            return true;
        }

        public void Refresh(IReadOnlyList<CloudRepositoryHeaderDto> repositories)
        {
            _latestSnapshotIds.Clear();
            foreach (var repository in repositories)
                _latestSnapshotIds[repository.RepositoryId] = repository.LatestSnapshotId;

            _lastRefreshUtc = DateTime.UtcNow;
        }

        public void Set(int repositoryId, long? latestSnapshotId)
        {
            _latestSnapshotIds[repositoryId] = latestSnapshotId;
            _lastRefreshUtc = DateTime.UtcNow;
        }
    }

}
