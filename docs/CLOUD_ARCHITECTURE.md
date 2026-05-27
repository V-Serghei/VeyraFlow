# Cloud Architecture in VeyraFlow

## Overview

Cloud sync connects the C# desktop client to a Go REST API backed by PostgreSQL. The design is offline-first: the desktop operates entirely without cloud connectivity, and sync is an asynchronous background operation with a persistent queue.

This document describes the implementation architecture. Product-level cloud recovery and management requirements are split into:

- `docs/CLOUD_REPOSITORY_MANAGER.md`
- `docs/CLOUD_INFORMATION_WINDOW.md`
- `docs/CLOUD_RECOVERY_FLOW.md`
- `docs/CLOUD_SYNC_BACKGROUND.md`
- `docs/MANUAL_SYNC.md`
- `docs/CLOUD_CONFLICT_RESOLUTION.md`
- `docs/RECOVERY_SNAPSHOT.md`
- `docs/GLOBAL_CLOUD_SETTINGS.md`
- `docs/REPOSITORY_CLOUD_SETTINGS.md`
- `docs/OFFLINE_DIVERGENCE.md`

---

## Stack

| Layer | Technology | Location |
|-------|-----------|----------|
| Desktop client | C# / .NET 10 | `src/Veyra.Desktop/` |
| HTTP sync service | C# / .NET 10 | `src/Veyra.Infrastructure.Sync/` |
| Cloud API server | Go | `server/cloud-api/` |
| Server database | PostgreSQL | `server/cloud-api/internal/handlers/sync.go` |
| Client database | SQLite (EF Core) | `src/Veyra.Infrastructure.Data/` |

---

## Key Components

### Client Side

| Component | Location | Responsibility |
|-----------|----------|---------------|
| `RepositoryCloudSyncOrchestrator` | `src/Veyra.Infrastructure.Data/Sync/` | Main orchestration. Queue management, package building, retry logic, conflict resolution, cloud restore. |
| `CloudSyncHttpService` | `src/Veyra.Infrastructure.Sync/Sync/Cloud/` | HTTP layer. All API calls, multipart upload, idempotency headers. |
| `ICloudAvailabilityService` | `src/Veyra.Application/Abstractions/Sync/` | Sync gate and circuit breaker contract. Prevents token refresh/HTTP work while cloud is unavailable. |
| `ICloudSyncRuntimeControlService` | `src/Veyra.Application/Abstractions/Sync/` | Pause/resume contract for the global sync queue. |
| `IAccessTokenPolicyService` | `src/Veyra.Application/Abstractions/Auth/` | Evaluates whether an access token can be used for sync (`CanUseForSync`). |
| `EfRepositoryIntegrityService` | `src/Veyra.Infrastructure.Data/Setup/Integrity/` | Block integrity check and cloud repair. |

### Server Side

| Component | Location | Responsibility |
|-----------|----------|---------------|
| Sync handlers | `server/cloud-api/internal/handlers/sync.go` | Push snapshot, block existence check, block upload, block download, list repositories, get latest snapshot. |

---

## Authentication

- The client obtains an access token via `IAuthService`.
- Before each sync operation, `IAccessTokenPolicyService.Evaluate(token)` is called. The result exposes `CanUseForSync` and `State` (e.g. `ExpiringSoon`, `Expired`).
- If the token is expiring soon, sync proceeds but a warning is logged.
- If the token is expired and a refresh token is available, `IAuthService.RefreshAsync` is called automatically inside `TryGetSyncAccessTokenAsync` — protected by a `SemaphoreSlim` to prevent concurrent refresh races.
- If a refresh is rejected by the server (`CloudAuthRefreshRejectedException`), the local cloud session is signed out and the sync operation is aborted.
- Auth failures are **not retried** (`retryable = !authFailure`).

---

## Cloud Data Model (Server — PostgreSQL)

| Table | Purpose |
|-------|---------|
| `snapshots` | Per-repository snapshot headers |
| `snapshot_entries` | File/directory entries within a snapshot |
| `snapshot_file_versions` | File version metadata per snapshot entry |
| `cloud_blocks` | Block hash registry (tracks which blocks are stored) |
| `sync_idempotency_keys` | Server-side deduplication of push operations |

Block content files are stored separately from the PostgreSQL database (cloud object/file storage, accessed via the Go server).

---

## Sync Queue

Each snapshot push is represented by a `RepositorySyncQueueItem` (`src/Veyra.Domain/Entities/RepositorySyncQueueItem.cs`).

### Statuses

| Status | Meaning |
|--------|---------|
| `pending` | Waiting to be processed |
| `running` | Currently being uploaded |
| `conflict` | Remote head changed and strategy is `manual_merge` |
| `retry` | Failed but has attempts remaining |
| `completed` | Successfully synced |
| `failed` | Auth failure or non-retryable error |
| `dead_letter` | Exhausted all retry attempts |
| `cancelled` | Cancelled by user |

Stale `running` items older than 5 minutes (`RunningLeaseTimeout`) are recovered to `retry` or `dead_letter` at the start of each queue processing cycle.

### Queue Processing

Only one queue item is processed at a time (guarded by `SemaphoreSlim _queueGate`). The orchestrator loops through due items (`pending` with `NextAttemptAtUtc <= now`, eligible `retry`, and auto-resolved `conflict`) in order of `NextAttemptAtUtc` then `CreatedAt`.

### Connectivity-aware queue gate

Before token refresh or HTTP work, queue processing checks `ICloudAvailabilityService.ShouldSkipCloudOperation`. When the gate is closed, repositories with pending/running/retry cloud work are marked `offline_retry` and processing returns without network calls. This keeps local snapshot creation and startup responsive when the cloud API is down.

---

## Conflict Strategies

Configured per repository (`Repository.SyncConflictStrategy`):

| Strategy | Behavior |
|----------|---------|
| `last_write_wins` | Proceeds with push regardless of remote head divergence |
| `manual_merge` | Sets queue item status to `conflict`; requires user action to continue |
| `preserve_both` | Generates a new remote snapshot ID and appends a `preserve_both_<timestamp>` suffix to the snapshot title before pushing |

A conflict is detected when `repository.CloudLastRemoteSnapshotId` is set, the server reports a different latest snapshot ID, and the remote head is not the snapshot being pushed.

---

## Metadata Encryption

When `repository.ProtectCloudMetadata` is `true`, the following fields are encrypted before transmission via `ICloudMetadataProtectionService`:

- Repository name and description.
- Snapshot title.
- File entry relative paths.

The encryption is applied inside `CloudSyncHttpService` when building the HTTP request. Decryption is applied when parsing the response from `GetRepositoriesAsync` and `GetLatestSnapshotAsync`. Block hashes and numeric metadata are never encrypted.

---

## Idempotency

Every push operation generates an idempotency key with the format:

```
push-{repositoryId}-{snapshotId}-p{phase}-{sha256[0:24]}
```

The SHA-256 input is: `push|{repositoryId}|{snapshotId}|{remoteSnapshotId}|{payloadSha256}|{phase}|a{attempt}`

- `phase 0`: initial metadata push.
- `phase 1`: confirmation push after blocks are uploaded.

The key is sent in the `X-Idempotency-Key` HTTP header. The server stores it in `sync_idempotency_keys` and returns the same response for duplicate requests, preventing double-writes on network retries.

---

## Remote Snapshot ID

The remote snapshot ID is a deterministic `int64` derived from:

```
SHA256("{clientInstanceId}|{repositoryId}|{localSnapshotId}|{createdAt:O}")
```

The first 8 bytes of the SHA-256 output are interpreted as a signed `int64` with the sign bit cleared (`& long.MaxValue`). The `clientInstanceId` defaults to `SHA256("{MachineName}|{UserName}")` unless overridden in configuration (`CloudSync:ClientInstanceId`).

---

## Cloud History Independence

Cloud history is never affected by local retention policy execution. Local cleanup (deletion of old snapshots and blocks from SQLite and the local block-store) does not trigger any deletion on the cloud side. Cloud snapshots accumulate independently and can only be managed via explicit server-side operations.

---

## Runtime Controls

`ICloudSyncRuntimeControlService` exposes:

- **Pause**: cancels the active queue item via `CancellationTokenSource`. If a push was in progress, the active item is moved to `retry` status (not cancelled), so it will resume from its upload checkpoint. All pending items are marked `paused` on their repository status field.
- **Resume**: allows queue processing to continue from the next due item.

The service raises a `StateChanged` event. `RepositoryCloudSyncOrchestrator` subscribes to this event during queue processing and cancels the active item immediately when paused.
