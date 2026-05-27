# Snapshot Lifecycle

This document describes how snapshots are created, what they contain, how change detection works, and the guarantees around data integrity throughout the process.

---

## Table of Contents

1. [Triggers](#triggers)
2. [Trigger Strings and Classification](#trigger-strings-and-classification)
3. [What Files Enter a Snapshot](#what-files-enter-a-snapshot)
4. [Captured Metadata per Entry](#captured-metadata-per-entry)
5. [Change Detection](#change-detection)
6. [Version Creation Pipeline](#version-creation-pipeline)
7. [Transactionality and Partial Snapshot Protection](#transactionality-and-partial-snapshot-protection)
8. [Busy File Handling](#busy-file-handling)
9. [Deduplication at Snapshot Time](#deduplication-at-snapshot-time)
10. [AutoCapture Flag](#autocapture-flag)
11. [Cloud Push After Snapshot](#cloud-push-after-snapshot)

---

## Triggers

Snapshots can be initiated in three ways.

### Manual (via `ScanRepositoryCommand`)

A manual snapshot is triggered by dispatching `ScanRepositoryCommand` with `Options.SaveFileVersions = true`. This goes through the MediatR pipeline to `ScanRepositoryHandler`, which delegates to `IRepositoryScanner.ScanRepositoryAsync`.

```csharp
var command = new ScanRepositoryCommand(
    RepositoryId: repositoryId,
    Options: new RepositoryScanOptionsDto(SaveFileVersions: true));

await mediator.Send(command);
```

The handler logs the result and queues a background cloud sync when a snapshot was successfully created.

### Scheduled (via `SnapshotSchedulerService`)

`SnapshotSchedulerService` runs a `PeriodicTimer` loop (poll interval configurable via `SnapshotSchedulerOptions.PollSeconds`, clamped to 5–600 seconds). On each tick it:

1. Checks whether the current local time falls inside the configured **quiet hours** window. If yes, the tick is skipped.
2. Queries all repositories and filters to those whose `LastScannedAt` is older than the configured interval (`IntervalMinutes`, clamped to 1–1440 minutes).
3. Calls `IRepositoryScanner.ScanRepositoryAsync` for each due repository in parallel, up to `MaxConcurrentScans`.
4. For each due repository, passes `SaveFileVersions = repo.AutoCaptureFileVersions` from the repository entity, meaning only repositories with auto-capture enabled will produce versioned snapshots.
5. Applies retry logic (`RetryCount`, `RetryDelaySeconds`) on transient failures.

Quiet hours support wraparound (e.g., 22:00–06:00 the next day).

### Live Watchers (delta-based)

Live file system watchers produce delta updates using `ApplyWorkingSnapshotDeltaAsync` and `ApplyVersionedSnapshotDeltaAsync` on `IRepositorySnapshotRepository`. These produce "working" snapshots that track index changes incrementally rather than as full scans.

---

## Trigger Strings and Classification

The trigger string is stored on the `RepositorySnapshot` entity and is used for filtering, retention policies, and display. `RepositorySnapshotTriggerClassifier` maps triggers to three snapshot kinds.

| Kind | Prefix patterns |
|---|---|
| `Automatic` | `auto_snapshot*`, `scheduled_snapshot*`, `initial_snapshot*` |
| `Working` | `scheduled_sync*`, `sync_index*`, `sync_live_watcher` |
| `Manual` | everything else |

The trigger string is computed by `RustRepositoryScanner.ResolveTrigger()` based on `IsScheduled` and `SaveFileVersions` flags, and the active scan engine:

| Scenario | Trigger string |
|---|---|
| Manual scan, save versions, Rust engine | `manual_snapshot_rust` |
| Manual scan, save versions, managed fallback | `manual_snapshot_managed_fallback` |
| Scheduled scan, save versions, Rust engine | `scheduled_snapshot_rust` |
| Scheduled scan, no versions, Rust engine | `scheduled_sync_rust` |
| Manual scan, no versions, Rust engine | `sync_index_rust` |
| Cloud restore sync | `cloud_restore_sync` (set via `TriggerOverride`) |

A `TriggerOverride` in `RepositoryScanOptionsDto` takes precedence over all computed values.

---

## What Files Enter a Snapshot

File inclusion is controlled by two mechanisms.

### Linked Formats (extension filter)

Each repository has a list of **linked format extensions** (e.g., `.docx`, `.pdf`, `.png`). The Rust scanner receives these as a CSV string. Only files matching at least one linked extension are included in the scan result. If the linked formats list is empty, all files are included.

### Exclusion Patterns

Each repository has a list of **excluded patterns**. These are evaluated via `RepositoryScanExclusionMatcher.IsExcluded(relativePath, excludedPatterns)` after the raw scan result is projected. Both files and directories can be excluded. Excluded directories cause the entire subtree to be skipped.

Entries that pass both filters are projected into `RepositoryScanEntryDto` objects and forwarded to `IRepositorySnapshotRepository.SaveSnapshotAsync`.

---

## Captured Metadata per Entry

Each `RepositoryScanEntryDto` carries:

| Field | Type | Description |
|---|---|---|
| `RelativePath` | `string` | Path relative to the repository root (forward slashes) |
| `ParentRelativePath` | `string?` | Relative path of the parent directory, or `null` for root-level entries |
| `Name` | `string` | File or directory name |
| `IsDirectory` | `bool` | `true` for directories |
| `Extension` | `string?` | Lowercase extension including dot (e.g., `.txt`), `null` for directories |
| `SizeBytes` | `long` | File size in bytes, `0` for directories |
| `LastWriteUtc` | `DateTime` | Last-write timestamp in UTC |
| `ContentHashSha256` | `string?` | Hex-encoded SHA-256 of the file content; `null` for directories |

Directory entries are included in the snapshot to preserve the full tree structure. They do not participate in version creation.

---

## Change Detection

### Working snapshots (no file versions)

Before saving, `SaveSnapshotAsync` compares the current entry set against the previous snapshot's entries using `HasMeaningfulEntryChanges`. If no meaningful differences exist, a `NoChanges` result is returned and the snapshot shell is not written.

### Versioned snapshots (with file versions)

For versioned scans, change detection is two-step:

1. **Path-level comparison**: `ISnapshotComparisonEngine.CompareRepositoryPathsAsync` delegates to the Rust engine (`veyra_compare_repository_paths_utf8`) which compares the current file states against the baseline. A file is considered changed if its `ContentHashSha256` or `SizeBytes` differs.
2. **Version materialization check**: Even if no paths changed, `NeedsVersionMaterializationAsync` checks whether any current files are missing a stored `FileVersion` with blocks. If both checks return false, a `NoChanges` result is returned.

The final per-file decision is made via `ISnapshotComparisonEngine.PlanRepositoryVersionsAsync` (Rust: `veyra_plan_repository_versions_utf8`), which produces a `ShouldCreateNewVersion` flag per path considering current state, previous state, latest known version, and whether that version already has blocks.

---

## Version Creation Pipeline

When a new version must be created for a file, the pipeline is:

1. **`FileIdentity` upsert** — ensures a `FileIdentity` row exists for the relative path within the repository. Identities are never deleted while files exist in any retained snapshot.
2. **Block storage** — calls `IFileContentStore.StoreFileAsync(absolutePath)` to chunk the file and store blocks. Returns a `StoredFileContentDto` containing the block list.
3. **`FileVersion` creation** — a new `FileVersion` row is added with `ContentHashSha256`, `SizeBytes`, `LastWriteUtc`, `IsDeletionMarker = false`.
4. **`FileVersionBlock` rows** — one row per chunk, linked to the `FileVersion` via `FileVersionId`. Fields: `Sequence`, `BlockStorageKey`, `LengthBytes`, `StoredSizeBytes`.
5. **`SnapshotFileLink` junction** — links the `RepositorySnapshot` to the `FileVersion` via `(SnapshotId, FileVersionId, FileIdentityId)`.

Deleted files receive a `FileVersion` with `IsDeletionMarker = true` and no block rows.

### Snapshot header fields written to `RepositorySnapshot`

| Field | Value |
|---|---|
| `RepositoryId` | From the scan command |
| `Trigger` | Computed trigger string |
| `Title` | Optional user-supplied title (normalized) |
| `TagsCsv` | Semicolon-separated tags |
| `CreatedAt` | `scannedAtUtc` from the scan call |
| `TotalEntries` | Total file + directory count |
| `FileEntries` | File-only count |
| `DirectoryEntries` | Directory-only count |
| `TotalFileBytes` | Sum of `SizeBytes` for non-directory entries |

---

## Transactionality and Partial Snapshot Protection

All writes inside `SaveSnapshotAsync` are wrapped in a **single `IDbContextTransaction`** opened at the start. The transaction covers:

- Snapshot header insert
- Snapshot entry inserts (batched, 64 per batch)
- File identity upserts
- File version and block inserts
- Snapshot file link inserts (batched, 128 per batch)
- Repository stats update (`FileCount`, `VersionCount`, `TotalSizeBytes`, `LastScannedAt`)

The transaction is committed in one call at the end. Any unhandled exception causes the transaction to be rolled back, leaving no partial data in the database.

### Early-abort conditions

| Condition | Behavior |
|---|---|
| No changes detected (working snapshot) | Snapshot shell deleted, transaction committed clean, `NoChanges` returned |
| No changes detected (versioned snapshot) | Same as above |
| No new versions after planning (all files unchanged) | `DeleteSnapshotShellAsync` called, transaction committed, `NoChanges` returned |
| Exception during block storage or DB write | Transaction rolled back, exception propagated |

---

## Busy File Handling

When `IFileContentStore.StoreFileAsync` throws an `IOException` (typically because the file is locked by another process), the exception is caught by `TryStoreBlocksAsync`. The file is added to the `busyFiles` list and processing continues with the remaining files.

A snapshot with busy files:

- Is still committed to the database if at least one version was created.
- Has `HasBusyFiles = true` and a non-zero `BusyFilesCount` in the result DTO.
- Does **not** trigger a cloud push (see below).

If no file versions are created and there are also no busy files (i.e., all unchanged), the snapshot shell is discarded.

---

## Deduplication at Snapshot Time

### Block-level deduplication

The native `veyra_store_file_blocks_utf8` (and the managed `StoreFileManagedAsync`) check for an existing block file at the hash-derived path before writing. If the file already exists, the block is counted as `deduped` and no write occurs. This is a TOCTOU-safe check because blocks are written with `FileMode.CreateNew` — a concurrent writer racing to create the same file will cause one of them to receive an `IOException`, which is caught and treated as a dedup hit.

### File-level deduplication

Before creating a new `FileVersion`, the code checks whether the latest known version for that `FileIdentity` already has `SizeBytes` and `ContentHashSha256` matching the current file and has existing block rows. If it does, the existing version is reused and linked into the new snapshot — no new blocks are stored.

---

## AutoCapture Flag

Each `Repository` entity has an `AutoCaptureFileVersions` boolean. When `SnapshotSchedulerService` processes a scheduled scan, it passes `SaveFileVersions: repo.AutoCaptureFileVersions`. Repositories with `AutoCaptureFileVersions = false` receive working-index scans (trigger `scheduled_sync_*`) rather than versioned snapshots (trigger `scheduled_snapshot_*`).

---

## Cloud Queue After Snapshot

Cloud sync is only queued when all of the following are true:

- `Options.SaveFileVersions == true`
- `result.SnapshotCreated == true`
- `result.HasBusyFiles == false`

For manual scans (`ScanRepositoryHandler`), cloud post-processing is fired via `Task.Run` in the background using an isolated DI scope calling `IRepositoryCloudSyncOrchestrator.TryPushLatestSnapshotAsync`.

`TryPushLatestSnapshotAsync` writes a local `RepositorySyncQueueItem` before any token refresh or HTTP work. If `ICloudAvailabilityService` reports offline, cloud unavailable, unauthorized, reconnecting, or unknown state, the queue row remains pending and repository cloud status is marked `offline_retry`. No HTTP request is made while the gate is closed.

For scheduled scans (`SnapshotSchedulerService`), the local scan is marked complete before cloud post-processing. If cloud is unavailable, the orchestrator returns quickly after queue/status updates.

If the snapshot has busy files, the push is skipped and a warning is logged with the count of busy files.
