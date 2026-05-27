# Reverse Sync — Cloud-to-Local Synchronization

This document covers all scenarios where data flows from the cloud back to the local machine, the safety guarantees that protect data integrity, and the known limitations of the current implementation.

---

## Overview

Cloud-to-local sync in VeyraFlow is not a continuous two-way mirror. The cloud is a write-forward append store: local snapshots are pushed to it and the full history is preserved there regardless of what happens locally. Pulling data back is a discrete, trigger-based operation. The only automatic reverse sync path is the new-machine restore that runs at startup.

---

## Scenarios

### New Machine — Automatic Restore at Startup

When the application starts with a signed-in user but no local repositories and no watched directories, `RunDeferredStartupAsync` (in `App.axaml.cs`) calls `RestoreRepositoriesFromCloudAsync`. The logic is:

1. Check for an active `UserProfile` in the local SQLite database.
2. Evaluate the access token; if it is invalid and cannot be refreshed, the cloud path is skipped entirely.
3. `GetRepositoriesAsync` fetches the list of remote repositories from the cloud API.
4. Remote repositories whose names already exist locally are excluded.
5. For each candidate, `PrepareCloudRepositoryRestoreAsync` calls `GetLatestSnapshotAsync` to retrieve the latest snapshot package (metadata + block map).
6. `RestoreFilesFromPackageAsync` executes the block prefetch and file reconstruction (see details below).
7. `CreateRepositoryWithFormatsCommand` registers the directory as a new local repository.
8. `ScanRepositoryCommand` immediately indexes the restored files to create the first local snapshot.

Repository preparation runs with a concurrency limit of `RestoreRepositoryMaxConcurrency = 2`. Block downloads inside each repository run with up to `RestoreDownloadMaxConcurrency = 8` parallel downloads.

### After Local Retention Cleanup

Local retention (`EfRepositoryRetentionService`) deletes snapshots and orphan blocks from the local store. It has no interaction with the cloud. The cloud preserves the complete snapshot history independently. If a user's local blocks are cleaned up by retention, the data is recoverable by running a restore from cloud (currently a manual action from App Settings → Sync → Restore from cloud).

### Partial Blocks Scenario

When any file in the restore package references blocks that are not present locally, `EnsureLocalBlocksForRestoreAsync` downloads the missing blocks before reconstruction proceeds. For each block hash in the package:

- If the local block file already exists on disk, it is reused.
- If it is missing, `cloudSync.DownloadBlockToFileAsync` downloads it to the local block store.
- Failed downloads are tracked in a `failedBlocks` set. Files whose blocks could not be downloaded are skipped with a warning log entry; other files in the same restore proceed normally.

### Cloud-Only Snapshots

The cloud may contain snapshots that no longer exist locally (deleted by retention or by a manual cleanup). Only the latest snapshot is used during the automatic restore flow — older snapshots are present in the cloud but the current client does not enumerate or restore them automatically. A dedicated history-exploration UI for this purpose has not been implemented yet.

### Repository Deleted Locally Then Restored

If a repository's local row is soft-deleted or the directory is gone, it no longer appears in `GetAllRepositoriesAsync`. On the next deferred startup (after re-login or fresh install), `RestoreRepositoriesFromCloudAsync` will see it as a restore candidate and recreate it from the latest cloud snapshot, exactly as for a new machine.

### Interrupted Sync — Resumable Upload

Outbound sync (local-to-cloud) uses a checkpoint mechanism to resume after network failure:

- Checkpoint state is stored in `RepositorySyncQueueItem`: `UploadCheckpointSignature`, `UploadCheckpointTotal`, `UploadCheckpointNextIndex`.
- The checkpoint is persisted every `UploadCheckpointPersistEveryBlocks = 32` blocks uploaded, or every `UploadCheckpointPersistInterval = 2 seconds`, whichever comes first.
- On reconnect the queue item is retried. If the checkpoint signature matches the current missing-block set, the upload resumes from `UploadCheckpointNextIndex` rather than restarting from zero.
- Retry scheduling uses exponential backoff: `baseDelay * 2^(attempt-1)`, where `baseDelay = Repository.SyncRetryBaseDelaySeconds` (clamped 5–600 s), and the total delay is clamped between 5 s and 7200 s.
- Connectivity failures (`IOException`, `TimeoutException`, `TaskCanceledException`, `HttpRequestException`) set the queue item to `offline_retry`. Auth failures go directly to `failed` (non-retryable).

### Conflicts

A conflict is detected when the remote head snapshot ID has changed since the last known sync and is not the snapshot currently being pushed. The per-repository `SyncConflictStrategy` field controls how it is resolved:

| Strategy | Constant | Behavior |
|---|---|---|
| `last_write_wins` | `RepositorySyncConflictStrategies.LastWriteWins` | Default. The local snapshot is pushed over the diverged remote head without merging. |
| `manual_merge` | `RepositorySyncConflictStrategies.ManualMerge` | Queue item is set to `conflict` status. Processing stops until the user intervenes. |
| `preserve_both` | `RepositorySyncConflictStrategies.PreserveBoth` | A new remote snapshot ID is derived from the conflict context; both diverged lines are kept as separate snapshots on the cloud. |

### Nested Repositories

Nested repositories (repositories whose directories are inside another repository's directory) are treated as independent entities. Each has its own `RepositorySyncQueueItem` rows, its own `SyncConflictStrategy`, and its own checkpoint tracking. There is no parent-child coupling in the sync layer.

---

## Safety Guarantees

| Guarantee | How it is enforced |
|---|---|
| No data loss | Cloud history is never modified by local retention. `help.pro_cloud_b2` in localization explicitly states that historical versions removed locally remain restorable from cloud unless the user runs a cloud-prune action. |
| No corrupted history graph | Idempotency keys are derived from `SHA256(repositoryId + snapshotId + remoteSnapshotId + payloadSha256 + phase + attempt)`. Duplicate or retried pushes are deduplicated by the cloud API. |
| No duplicate snapshots | The remote snapshot ID is deterministically derived from content hash, repository ID, and creation timestamp via `BuildRemoteSnapshotId`. |
| Dangling block references | `EnsureLocalBlocksForRestoreAsync` validates that every block in the package is either locally available or downloadable before file reconstruction. Files with missing blocks are skipped; the rest restore successfully. |
| Missing blocks in integrity repair | `EfRepositoryIntegrityService.VerifyRepositoryAsync(repairFromCloud: true)` probes each missing block against the cloud API and downloads it if present. The downloaded block is SHA-256 validated before being written to the local block store. |
| Stale running queue items | Items stuck in `Running` for longer than `RunningLeaseTimeout = 5 minutes` are recovered to `Retry` or `DeadLetter` at the start of each `ProcessPendingQueueAsync` call. |

---

## Not Yet Implemented

**"Bring cloud state to local state" (cloud prune):** This would delete cloud snapshots that no longer exist locally, effectively making the cloud match the local retention state. It is referenced in localization (`help.pro_cloud_b2`) as a "dangerous cloud-prune action" but no UI surface or backend implementation exists. The operation would require explicit, irreversible-loss confirmation and is intentionally blocked.

---

## Related Source Files

- `src/Veyra.Desktop/Services/Sync/Cloud/RepositoryCloudSyncOrchestrator.cs` — core sync logic
- `src/Veyra.Desktop/App.axaml.cs` — `RunDeferredStartupAsync`
- `src/Veyra.Application/DTOs/Repository/Core/RepositorySyncConflictStrategies.cs`
- `src/Veyra.Infrastructure.Data/Setup/Integrity/EfRepositoryIntegrityService.cs`
- `src/Veyra.Infrastructure.Data/Setup/Retention/EfRepositoryRetentionService.cs`
