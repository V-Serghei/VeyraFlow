using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using Veyra.Application.DTOs;
using Veyra.Domain.Entities;
using Veyra.Infrastructure.Data.Persistence;

namespace Veyra.Desktop.Services.Sync;

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
    ILogger<RepositoryCloudSyncOrchestrator> log)
    : IRepositoryCloudSyncOrchestrator
{
    private const string ManagedHashPrefix = "sha256-";
    private const string PushOperationType = RepositorySyncQueueItem.OperationPushSnapshot;
    private static readonly TimeSpan RunningLeaseTimeout = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _queueGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly string _clientInstanceId = ResolveClientInstanceId(configuration);

    public async Task TryPushLatestSnapshotAsync(int repositoryId, CancellationToken ct = default)
    {
        var accessToken = await TryGetSyncAccessTokenAsync("push latest snapshot", ct);
        if (string.IsNullOrWhiteSpace(accessToken))
            return;

        var repository = await db.Repositories
            .FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted, ct);

        if (repository is null)
            return;

        var latestSnapshot = await db.RepositorySnapshots
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
            .Where(s => db.SnapshotFileLinks.Any(l => l.SnapshotId == s.Id && !l.IsDeleted))
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => new { s.Id, s.CreatedAt })
            .FirstOrDefaultAsync(ct);

        if (latestSnapshot is null)
            return;

        var remoteSnapshotId = BuildRemoteSnapshotId(repositoryId, latestSnapshot.Id, latestSnapshot.CreatedAt);

        await EnqueueSnapshotPushAsync(
            repository,
            latestSnapshot.Id,
            remoteSnapshotId,
            ct);

        await ProcessPendingQueueAsync(ct);
    }

    public async Task ProcessPendingQueueAsync(CancellationToken ct = default)
    {
        var accessToken = await TryGetSyncAccessTokenAsync("process sync queue", ct);
        if (string.IsNullOrWhiteSpace(accessToken))
            return;

        await _queueGate.WaitAsync(ct);
        try
        {
            await RecoverStaleRunningQueueItemsAsync(ct);

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var next = await GetNextQueueItemAsync(ct);
                if (next is null)
                    break;

                await ProcessQueueItemAsync(accessToken, next.Id, ct);
            }
        }
        finally
        {
            _queueGate.Release();
        }
    }

    public async Task<RepositoryCloudRepairResultDto> RepairRepositoryCloudDataAsync(int repositoryId, CancellationToken ct = default)
    {
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
            .Select(b => b.BlockHashBlake3)
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

        var alreadyPresent = 0;
        var uploaded = 0;
        var missingLocal = 0;
        var failedUploads = 0;

        foreach (var blockHash in blockHashes)
        {
            ct.ThrowIfCancellationRequested();

            bool existsRemotely;
            try
            {
                existsRemotely = await cloudSync.BlockExistsAsync(accessToken, blockHash, ct);
            }
            catch (Exception ex)
            {
                log.LogWarning(
                    ex,
                    "Failed to probe remote block presence during repository repair. RepositoryId {RepositoryId}. Block {BlockHash}",
                    repositoryId,
                    blockHash);
                failedUploads++;
                continue;
            }

            if (existsRemotely)
            {
                alreadyPresent++;
                continue;
            }

            try
            {
                var uploadedNow = await TryUploadMissingBlockAsync(accessToken, blockHash, ct);
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
                    blockHash);
                failedUploads++;
            }
        }

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
                : $"Failed to upload {failedUploads} block(s) during repair.");
    }

    public async Task<int> RestoreRepositoriesFromCloudAsync(CancellationToken ct = default)
    {
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

        var restoredCount = 0;

        foreach (var remote in remoteRepositories)
        {
            if (string.IsNullOrWhiteSpace(remote.Name))
                continue;

            if (localNames.Contains(remote.Name))
                continue;

            var package = await cloudSync.GetLatestSnapshotAsync(accessToken, remote.RepositoryId, ct);
            if (package is null)
                continue;

            var restorePath = BuildRestorePath(profile.Username, remote.Name);
            Directory.CreateDirectory(restorePath);

            await RestoreFilesFromPackageAsync(accessToken, package, restorePath, ct);

            var formats = package.Entries
                .Where(e => !e.IsDirectory)
                .Select(e => NormalizeFormat(e.Extension))
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (formats.Count == 0)
            {
                formats = [".txt"];
            }

            var createResult = await mediator.Send(
                new CreateRepositoryWithFormatsCommand(
                    package.Repository.Name,
                    package.Repository.Description,
                    restorePath,
                    formats),
                ct);

            if (!createResult.Success || createResult.Value <= 0)
            {
                log.LogWarning(
                    "Cloud restore created files but repository bootstrap failed. Name {Name}. Path {Path}. Error {Error}",
                    package.Repository.Name,
                    restorePath,
                    createResult.Error ?? "(none)");
                continue;
            }

            await mediator.Send(
                new ScanRepositoryCommand(
                    createResult.Value,
                    Progress: null,
                    new RepositoryScanOptionsDto(
                        SaveFileVersions: true,
                        TriggerOverride: "cloud_restore_sync",
                        SnapshotTitle: $"cloud_restore_{DateTime.Now:yyyyMMdd_HHmmss}")),
                ct);

            localNames.Add(remote.Name);
            restoredCount++;
        }

        return restoredCount;
    }

    private async Task<string?> TryGetSyncAccessTokenAsync(
        string operationName,
        CancellationToken ct,
        UserProfileSessionDto? profile = null)
    {
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
            var latestProfile = await userProfiles.GetActiveProfileAsync(ct);
            if (latestProfile is not null &&
                string.Equals(latestProfile.Username, activeProfile.Username, StringComparison.OrdinalIgnoreCase))
            {
                var latestState = tokenPolicy.Evaluate(latestProfile.AccessToken);
                if (latestState.CanUseForSync)
                    return latestProfile.AccessToken;
            }

            var refreshed = await authService.RefreshAsync(activeProfile.RefreshToken, ct);
            if (refreshed is null || string.IsNullOrWhiteSpace(refreshed.AccessToken))
            {
                log.LogWarning(
                    "Cloud token refresh failed for operation '{Operation}'. User {Username}.",
                    operationName,
                    activeProfile.Username);
                return null;
            }

            var persistedEmail = !string.IsNullOrWhiteSpace(refreshed.Email)
                ? refreshed.Email
                : activeProfile.Email;

            await userProfiles.SaveOrUpdateProfileAsync(
                activeProfile.Username,
                refreshed.CloudUserId,
                refreshed.AccessToken,
                persistedEmail,
                refreshed.CloudSessionId ?? activeProfile.CloudSessionId,
                refreshed.RefreshToken ?? activeProfile.RefreshToken,
                refreshed.AccessTokenExpiresAtUtc,
                refreshed.RefreshTokenExpiresAtUtc,
                ct);

            log.LogInformation(
                "Cloud token refreshed for user {Username}. NewExpiry {ExpiryUtc}",
                activeProfile.Username,
                refreshed.AccessTokenExpiresAtUtc);

            return refreshed.AccessToken;
        }
        catch (Exception ex)
        {
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
        CancellationToken ct)
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

    private async Task ProcessQueueItemAsync(string accessToken, long queueItemId, CancellationToken ct)
    {
        var queueItem = await db.Set<RepositorySyncQueueItem>()
            .Include(q => q.Repository)
            .FirstOrDefaultAsync(q => q.Id == queueItemId, ct);

        if (queueItem is null)
            return;

        var repository = queueItem.Repository;
        var now = DateTime.UtcNow;

        queueItem.Status = RepositorySyncQueueItem.StatusRunning;
        queueItem.LastError = null;
        queueItem.UpdatedAt = now;

        repository.CloudSyncLastStatus = "syncing";
        repository.CloudSyncLastError = null;
        repository.UpdatedAt = now;

        await db.SaveChangesAsync(ct);

        try
        {
            var remoteLatestSnapshotId = await GetRemoteLatestSnapshotIdAsync(accessToken, repository.Id, ct);
            var strategy = RepositorySyncConflictStrategies.Normalize(repository.SyncConflictStrategy);

            var hasConflict = repository.CloudLastRemoteSnapshotId.HasValue
                              && remoteLatestSnapshotId.HasValue
                              && remoteLatestSnapshotId.Value != repository.CloudLastRemoteSnapshotId.Value
                              && remoteLatestSnapshotId.Value != queueItem.RemoteSnapshotId;
            var expectedRemoteSnapshotId = repository.CloudLastRemoteSnapshotId.GetValueOrDefault();

            if (hasConflict && strategy == RepositorySyncConflictStrategies.ManualMerge)
            {
                queueItem.Status = RepositorySyncQueueItem.StatusConflict;
                queueItem.ObservedRemoteSnapshotId = remoteLatestSnapshotId;
                queueItem.LastError = $"Cloud conflict detected. Remote latest snapshot is {remoteLatestSnapshotId.Value}, expected {expectedRemoteSnapshotId}.";
                queueItem.UpdatedAt = DateTime.UtcNow;

                repository.CloudSyncLastStatus = "conflict";
                repository.CloudSyncLastError = queueItem.LastError;
                repository.UpdatedAt = DateTime.UtcNow;

                await db.SaveChangesAsync(ct);
                return;
            }

            string? titleSuffix = null;
            if (hasConflict && strategy == RepositorySyncConflictStrategies.PreserveBoth)
            {
                var preserveBothRemoteSnapshotId = BuildConflictRemoteSnapshotId(
                    repository.Id,
                    queueItem.SnapshotId,
                    queueItem.RemoteSnapshotId,
                    remoteLatestSnapshotId ?? 0);

                queueItem.RemoteSnapshotId = preserveBothRemoteSnapshotId;
                titleSuffix = $"preserve_both_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
            }

            var package = await BuildCloudSnapshotPackageAsync(
                repository.Id,
                queueItem.SnapshotId,
                queueItem.RemoteSnapshotId,
                titleSuffix,
                ct);

            if (package is null)
            {
                queueItem.Status = RepositorySyncQueueItem.StatusCompleted;
                queueItem.LastError = null;
                queueItem.UpdatedAt = DateTime.UtcNow;

                repository.CloudSyncLastStatus = "skipped";
                repository.CloudSyncLastError = null;
                repository.UpdatedAt = DateTime.UtcNow;

                await db.SaveChangesAsync(ct);
                return;
            }

            var runAttempt = Math.Max(0, queueItem.AttemptCount) + 1;
            var initialIdempotencyKey = BuildPushIdempotencyKey(
                repository.Id,
                queueItem.SnapshotId,
                queueItem.RemoteSnapshotId,
                package.Snapshot.PayloadSha256,
                phase: 0,
                attempt: runAttempt);
            var pushResult = await cloudSync.PushSnapshotAsync(accessToken, repository.Id, package, initialIdempotencyKey, ct);
            if (pushResult is null)
                throw new InvalidOperationException("Cloud rejected snapshot push (unauthorized or invalid session).");

            var uploaded = 0;
            var missingHashes = pushResult.MissingBlockHashes
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Select(h => h.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (missingHashes.Count > 0)
            {
                var checkpointSignature = BuildMissingCheckpointSignature(package.Snapshot.PayloadSha256, missingHashes);
                var resumeIndex = 0;

                if (string.Equals(queueItem.UploadCheckpointSignature, checkpointSignature, StringComparison.OrdinalIgnoreCase)
                    && queueItem.UploadCheckpointTotal == missingHashes.Count)
                {
                    resumeIndex = Math.Clamp(queueItem.UploadCheckpointNextIndex, 0, missingHashes.Count);
                }
                else
                {
                    queueItem.UploadCheckpointSignature = checkpointSignature;
                    queueItem.UploadCheckpointTotal = missingHashes.Count;
                    queueItem.UploadCheckpointNextIndex = 0;
                    queueItem.UpdatedAt = DateTime.UtcNow;
                    repository.CloudSyncLastStatus = "syncing_upload";
                    repository.UpdatedAt = queueItem.UpdatedAt;
                    await db.SaveChangesAsync(ct);
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

                for (var i = resumeIndex; i < missingHashes.Count; i++)
                {
                    var missingHash = missingHashes[i];
                    if (await TryUploadMissingBlockAsync(accessToken, missingHash, ct))
                        uploaded++;

                    queueItem.UploadCheckpointSignature = checkpointSignature;
                    queueItem.UploadCheckpointTotal = missingHashes.Count;
                    queueItem.UploadCheckpointNextIndex = i + 1;
                    queueItem.UpdatedAt = DateTime.UtcNow;
                    repository.CloudSyncLastStatus = $"syncing_upload {i + 1}/{missingHashes.Count}";
                    repository.UpdatedAt = queueItem.UpdatedAt;
                    await db.SaveChangesAsync(ct);
                }

                var confirmIdempotencyKey = BuildPushIdempotencyKey(
                    repository.Id,
                    queueItem.SnapshotId,
                    queueItem.RemoteSnapshotId,
                    package.Snapshot.PayloadSha256,
                    phase: 1,
                    attempt: runAttempt);

                var confirm = await cloudSync.PushSnapshotAsync(accessToken, repository.Id, package, confirmIdempotencyKey, ct);
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
            }

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

            await db.SaveChangesAsync(ct);

            log.LogInformation(
                "Cloud queue item completed. RepositoryId {RepositoryId}. LocalSnapshotId {SnapshotId}. RemoteSnapshotId {RemoteSnapshotId}. Missing {Missing}. Uploaded {Uploaded}",
                repository.Id,
                queueItem.SnapshotId,
                queueItem.RemoteSnapshotId,
                missingHashes.Count,
                uploaded);
        }
        catch (Exception ex)
        {
            await MarkQueueFailureAsync(queueItemId, ex, ct);
        }
    }

    private async Task MarkQueueFailureAsync(long queueItemId, Exception ex, CancellationToken ct)
    {
        var queueItem = await db.Set<RepositorySyncQueueItem>()
            .Include(q => q.Repository)
            .FirstOrDefaultAsync(q => q.Id == queueItemId, ct);

        if (queueItem is null)
            return;

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

    private async Task<long?> GetRemoteLatestSnapshotIdAsync(string accessToken, int repositoryId, CancellationToken ct)
    {
        var remoteRepositories = await cloudSync.GetRepositoriesAsync(accessToken, ct);
        var remote = remoteRepositories.FirstOrDefault(r => r.RepositoryId == repositoryId);
        return remote?.LatestSnapshotId;
    }

    private async Task<CloudSnapshotPackageDto?> BuildCloudSnapshotPackageAsync(
        int repositoryId,
        long localSnapshotId,
        long remoteSnapshotId,
        string? titleSuffix,
        CancellationToken ct)
    {
        var repository = await db.Repositories
            .Include(r => r.Directory)
            .FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted, ct);

        if (repository is null)
            return null;

        var snapshot = await db.RepositorySnapshots
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted && s.Id == localSnapshotId)
            .FirstOrDefaultAsync(ct);

        if (snapshot is null)
            return null;

        var entries = await db.RepositorySnapshotEntries
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

        var links = await db.SnapshotFileLinks
            .Where(l => l.SnapshotId == snapshot.Id && !l.IsDeleted)
            .Include(l => l.FileIdentity)
            .Include(l => l.FileVersion)
                .ThenInclude(v => v.Blocks)
            .OrderBy(l => l.FileIdentity.RelativePath)
            .ToListAsync(ct);

        if (links.Count == 0)
            return null;

        var fileVersions = links
            .Select(link => new CloudFileVersionDto(
                link.FileIdentity.RelativePath,
                link.FileVersionId,
                link.FileVersion.ContentHashSha256,
                link.FileVersion.SizeBytes,
                link.FileVersion.IsDeletionMarker,
                link.FileVersion.CreatedAt,
                link.FileVersion.Blocks
                    .Where(b => !b.IsDeleted)
                    .OrderBy(b => b.Sequence)
                    .Select(b => new CloudBlockRefDto(
                        b.Sequence,
                        b.BlockHashBlake3,
                        b.LengthBytes,
                        b.StoredSizeBytes))
                    .ToList()))
            .ToList();

        var snapshotTitle = AppendTitleSuffix(snapshot.Title, titleSuffix);

        return new CloudSnapshotPackageDto(
            new CloudRepositoryMetadataDto(repository.Id, repository.Name, repository.Description),
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
            fileVersions);
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

            var blocks = new List<StoredFileBlockDto>(version.Blocks.Count);
            var hasMissingBlock = false;

            foreach (var block in version.Blocks.OrderBy(b => b.Sequence))
            {
                var localOk = await EnsureLocalBlockAsync(accessToken, block.BlockHash, ct);
                if (!localOk)
                {
                    hasMissingBlock = true;
                    log.LogWarning(
                        "Missing block during cloud restore. Repo {Repository}. File {File}. Block {BlockHash}",
                        package.Repository.Name,
                        version.RelativePath,
                        block.BlockHash);
                    break;
                }

                blocks.Add(new StoredFileBlockDto(
                    block.Sequence,
                    block.BlockHash,
                    block.LengthBytes,
                    block.StoredSizeBytes));
            }

            if (hasMissingBlock)
                continue;

            await fileContentStore.RestoreFileAsync(
                blocks,
                targetPath,
                overwriteExisting: true,
                ct);
        }
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

        var candidate1 = Path.Combine(root, "managed", "blocks", safe[..2], safe[2..4], safe + ".bin");
        if (File.Exists(candidate1))
            return candidate1;

        var candidate2 = Path.Combine(root, safe[..2], safe[2..4], safe + ".bin");
        if (File.Exists(candidate2))
            return candidate2;

        return candidate1;
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

    private static string BuildRestorePath(string username, string repositoryName)
    {
        var safeRepo = SanitizeSegment(repositoryName);
        var safeUser = SanitizeSegment(username);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VeyraFlow",
            "cloud-restored",
            safeUser,
            safeRepo);
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
        if (string.IsNullOrWhiteSpace(extension))
            return string.Empty;

        var value = extension.Trim();
        if (!value.StartsWith('.'))
            value = "." + value;

        return value.ToLowerInvariant();
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
}

