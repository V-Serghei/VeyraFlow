# Cloud Sync Flow in VeyraFlow

All cloud sync logic lives in `RepositoryCloudSyncOrchestrator` (`src/Veyra.Desktop/Services/Sync/Cloud/RepositoryCloudSyncOrchestrator.cs`). This document describes the two main directions of data flow: pushing a local snapshot to the cloud, and restoring repositories from the cloud to a new local installation.

---

## Local to Cloud (Push)

### Entry Point

`TryPushLatestSnapshotAsync(int repositoryId)` is the main trigger. It is called:
- After a new snapshot is created (post-scan).
- Manually via "sync now" UI actions.

If sync is paused at the time of the call, the snapshot is still enqueued but processing is deferred until sync resumes.

### Push Sequence

```
TryPushLatestSnapshotAsync
  |
  +--> Load latest snapshot for repositoryId
  |    (must have at least one SnapshotFileLink)
  |
  +--> BuildRemoteSnapshotId
  |    SHA256("{clientInstanceId}|{repositoryId}|{snapshotId}|{createdAt:O}")
  |    → int64 (sign bit cleared)
  |
  +--> EnqueueSnapshotPushAsync
  |    Creates or resets RepositorySyncQueueItem (status=pending)
  |
  +--> ProcessPendingQueueAsync
         |
         +--> RecoverStaleRunningQueueItemsAsync (lease timeout = 5 min)
         |
         +--> Loop: GetNextQueueItemAsync → ProcessQueueItemAsync
```

### ProcessQueueItemAsync in Detail

```
1. Mark item status=running
2. Get remote latest snapshot ID (cached 3 seconds)
3. Detect conflict (remote head diverged from CloudLastRemoteSnapshotId)
   - manual_merge  → status=conflict, stop
   - preserve_both → generate new remote ID, append timestamp suffix to title
   - last_write_wins → proceed
4. BuildCloudSnapshotPackageAsync
   - Load repository header
   - Load snapshot metadata
   - Load RepositorySnapshotEntries (ordered by RelativePath)
   - Load SnapshotFileLinks → FileVersions → FileVersionBlocks (ordered by Sequence)
   - Assemble CloudSnapshotPackageDto
   - Apply metadata encryption if ProtectCloudMetadata=true
5. Build idempotency key (phase=0, attempt=N)
6. PushSnapshotAsync (phase 0)
   POST /api/sync/repositories/{id}/snapshots
   Body: { repository, snapshot, entries, fileVersions }
   Server returns: { ok, missingBlockHashes }
7. If missingHashes.Count > 0 → upload phase
   See "Block Upload" below
8. Build idempotency key (phase=1)
9. PushSnapshotAsync (phase 1 / confirmation)
   Same endpoint, same body, new idempotency key
   Server must return missingBlockHashes=[] or throw
10. Update repository:
    CloudLastSyncedAt, CloudLastLocalSnapshotId, CloudLastRemoteSnapshotId
    status=completed
```

### Block Upload

Server-side deduplication: the server queries `cloud_blocks` by hash and returns only the hashes it does not yet have. The client never uploads blocks the server already holds.

**Batch limits** (constants in `RepositoryCloudSyncOrchestrator`):

| Constant | Value |
|----------|-------|
| `UploadBatchMaxBlocks` | 16 blocks per batch |
| `UploadBatchMaxBytes` | 32 MB per batch |

Upload attempts multipart batch first (`UploadBlockBatchAsync`). If the server returns `404`, `405`, or `501`, it falls back to single-block upload for the remainder of the session.

### Resumable Upload (Checkpoint)

Checkpoint fields are stored in `RepositorySyncQueueItem`:

| Field | Content |
|-------|---------|
| `UploadCheckpointSignature` | SHA-256 of `{payloadSha256}\|{sorted missing hashes joined by \|}` |
| `UploadCheckpointTotal` | Total number of missing blocks for this upload run |
| `UploadCheckpointNextIndex` | 0-based index of the next block to upload |

Checkpoint is persisted to the database every **32 blocks** (`UploadCheckpointPersistEveryBlocks`) or every **2 seconds** (`UploadCheckpointPersistInterval`), whichever comes first.

On resume (retry of the same queue item), if `UploadCheckpointSignature` matches the current missing-block list and `UploadCheckpointTotal` matches, upload resumes from `UploadCheckpointNextIndex`. If the signature does not match (different missing list, e.g. new snapshot), the checkpoint is reset and upload starts from index 0.

### Retry and Backoff

| Condition | Behavior |
|-----------|---------|
| Transient error (network, timeout, HTTP 5xx) | `status=retry`, increment `AttemptCount` |
| Auth failure (401, 403, "token", "invalid session") | `status=failed`, no retry |
| `AttemptCount >= MaxAttempts` | `status=dead_letter` |

Retry delay formula:

```
delay = clamp(baseDelay * 2^(attempt-1), 5, 7200) seconds
```

Where `baseDelay` is `Repository.SyncRetryBaseDelaySeconds` (clamped 5–600 s). The maximum delay is **7200 seconds** (2 hours).

### Pause / Resume

Pause is triggered via `ICloudSyncRuntimeControlService`. When paused:

1. The `StateChanged` event is raised.
2. The active queue item's `CancellationTokenSource` is cancelled.
3. The cancelled item is moved from `running` back to `retry` (not lost).
4. All pending/running items for affected repositories have their repository status set to `paused`.

On resume, `ProcessPendingQueueAsync` is called and the queue continues from the next due item.

---

## Cloud to Local (Restore)

### Entry Point

`RestoreRepositoriesFromCloudAsync(string? targetRootDirectory)` restores all cloud repositories that do not already exist locally (matched by name, case-insensitive).

### Restore Sequence

```
RestoreRepositoriesFromCloudAsync
  |
  +--> GetRepositoriesAsync → all cloud repositories for the current user
  |
  +--> Filter: exclude names already present in local repositories
  |
  +--> Deduplicate by name (first occurrence per name)
  |
  +--> Parallel.ForEachAsync (max concurrency = RestoreRepositoryMaxConcurrency = 2)
         |
         +--> PrepareCloudRepositoryRestoreAsync (per repository)
                |
                +--> GetLatestSnapshotAsync
                |    GET /api/sync/repositories/{id}/snapshots/latest
                |    Returns CloudSnapshotPackageDto (header + entries + fileVersions + blocks)
                |
                +--> BuildRestorePath
                |    Default: %LOCALAPPDATA%\VeyraFlow\cloud-restored\{username}\{repoName}
                |    Or: targetRootDirectory\{username}\{repoName}
                |
                +--> RestoreFilesFromPackageAsync
                     (see below)
```

### RestoreFilesFromPackageAsync

```
1. Create all directory entries on disk (package.Entries where IsDirectory=true)

2. Resolve "latest version per file path":
   GroupBy(RelativePath) → take most recent by CreatedAtUtc then FileVersionId

3. Collect all required block hashes across all files

4. EnsureLocalBlocksForRestoreAsync
   Parallel.ForEachAsync (max RestoreDownloadMaxConcurrency = 8)
     For each hash:
       - If block already in local store → skip
       - Else: cloudSync.DownloadBlockToFileAsync(hash, localPath)
   Returns set of hashes that failed to download

5. For each file version (in resolved order):
   - Skip if any of its blocks are in failedRestoreBlocks (log warning)
   - Call fileContentStore.RestoreFileAsync(blocks, targetPath, overwriteExisting=true)
   - IsDeletionMarker=true → delete file if it exists
```

### Post-Restore Repository Bootstrap

After all files are written to disk, for each prepared restore:

```csharp
// 1. Create local repository record
mediator.Send(new CreateRepositoryWithFormatsCommand(
    name, description, restorePath, formats));

// 2. Trigger initial scan to index the restored content
mediator.Send(new ScanRepositoryCommand(
    repositoryId,
    options: new RepositoryScanOptionsDto(
        SaveFileVersions: true,
        TriggerOverride: "cloud_restore_sync",
        SnapshotTitle: "cloud_restore_{timestamp}")));
```

The `formats` list is derived from the file extensions present in the cloud snapshot package. If no extensions are detected, it defaults to `[".txt"]`.

### Restore Limitations

| Limitation | Detail |
|-----------|--------|
| Only latest snapshot | `GetLatestSnapshotAsync` fetches the single most recent snapshot per repository. Full version history remains in the cloud but is not restored automatically. |
| Name-based deduplication | If a local repository with the same name already exists, the cloud repository is skipped entirely. |
| Max restore parallelism | 2 repositories restored concurrently (`RestoreRepositoryMaxConcurrency = 2`). |
| Block download parallelism | 8 concurrent block downloads per repository (`RestoreDownloadMaxConcurrency = 8`). |
| Failed blocks | Files with missing blocks are skipped with a warning log. No partial files are written. |

---

## API Endpoints Used

| Operation | Method | Path |
|-----------|--------|------|
| List repositories | `GET` | `/api/sync/repositories` |
| Push snapshot (metadata + block check) | `POST` | `/api/sync/repositories/{id}/snapshots` |
| Get latest snapshot | `GET` | `/api/sync/repositories/{id}/snapshots/latest` |
| Upload single block | `PUT` | `/api/sync/blocks/{hash}` |
| Upload block batch | `POST` | `/api/sync/blocks/batch` |
| Download block | `GET` | `/api/sync/blocks/{hash}` |
| Check block exists | `HEAD` | `/api/sync/blocks/{hash}` |

All requests include:
- `Authorization: Bearer {accessToken}`
- `X-Veyra-Sync-Protocol: 1`
- `X-Idempotency-Key: {key}` (push operations only)
