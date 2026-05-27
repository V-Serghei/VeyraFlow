# Offline Mode

VeyraFlow is an offline-first application. The cloud is an optional layer. Every core local operation functions without a network connection. This document describes the architecture, what works offline, what requires connectivity, how the connectivity service works, and how the application handles startup and reconnection.

---

## Architecture

The cloud sync layer is cleanly separated from local operations:

- `IRepositoryCloudSyncOrchestrator` and `ICloudSyncService` are the only types that issue outbound HTTP calls for sync.
- `IConnectivityStatusService` is the only type that probes network reachability.
- `ICloudAvailabilityService` is the sync-specific gate that prevents token refresh and HTTP work while cloud operations are unsafe.
- All local operations (snapshots, retention, diffs, file history, export) go through EF Core + SQLite and the Rust native library. Neither depends on connectivity.

When the cloud service is unavailable or the user is in guest mode, a no-op implementation (`NoopRepositoryCloudSyncOrchestrator`) is registered in place of the real orchestrator, ensuring no cloud code path is ever reached unintentionally.

---

## What Works Offline

| Feature | Notes |
|---|---|
| Repository creation and management | Fully local; no network dependency |
| Manual snapshots | Triggered by user; runs via `ScanRepositoryCommand` |
| Automatic scheduled snapshots | `SnapshotSchedulerService` continues scheduling regardless of connectivity state |
| File history and diffs | Reads from local SQLite + block store |
| Retention / cleanup | `EfRepositoryRetentionService` has no connectivity checks; orphan block cleanup is purely local |
| Transient cache cleanup | Local file system operation |
| System monitoring | `ConnectivityStatusService` and process metrics are local |
| Application settings | All settings read/write to local SQLite |
| Export bundles | Reads from local block store and produces a local archive |

---

## What Requires Connectivity

| Operation | Why |
|---|---|
| Cloud push (outbound sync) | `ICloudSyncService.PushSnapshotAsync`, `UploadBlockAsync` |
| Access token refresh | `IAuthService.RefreshAsync` calls the cloud auth endpoint |
| Cloud restore | `GetRepositoriesAsync`, `GetLatestSnapshotAsync`, `DownloadBlockToFileAsync` |
| Cloud repair | Block presence probe and download |
| Cloud storage metrics | API call to retrieve storage usage summary |

---

## Connectivity Service

`ConnectivityStatusService` is a singleton that monitors network state and fires `StatusChanged` events.

### States

| State | Meaning |
|---|---|
| `Unknown` | Not yet probed, or cloud probing is disabled (guest mode) |
| `Online` | Cloud API responded successfully to an HTTP HEAD probe |
| `InternetUnavailable` | Network interface reports no connectivity, or DNS/socket errors |
| `CloudUnavailable` | Network is up but the cloud API is unreachable or timed out |

### Polling Intervals

| Current state | Poll interval |
|---|---|
| `Online` | 90 seconds |
| `InternetUnavailable` or `CloudUnavailable` | 3 minutes |
| `Unknown` | 45 seconds |

In addition to polling, a `NetworkAvailabilityChanged` event from `NetworkChange` triggers an immediate probe when the OS reports a change in network interface availability. The probe itself has a 4-second timeout.

### SetCloudProbeEnabled

`SetCloudProbeEnabled(false)` disables all HTTP probing. The service immediately forces the state to `Unknown` and fires `StatusChanged`. No HTTP requests are made until probing is re-enabled. This is called in two places:

1. `App.axaml.cs` (`RunDeferredStartupAsync`) — called with `false` when no active username is found; called with `true` when a username is present.
2. `AppSettingsViewModel.OnHasActiveProfileChanged` — called whenever the active profile changes in the settings screen.

---

## Startup Behavior (Offline or No Profile)

`RunDeferredStartupAsync` handles both the offline and no-profile cases gracefully:

```
1. Check for active username in user_profiles table.
   - If no username found:
     → SetCloudProbeEnabled(false)
     → All cloud operations are skipped.
     → Application starts in guest/welcome state.

2. If username found:
   → SetCloudProbeEnabled(true)
   → Evaluate access token.
   → If token is invalid and cannot be refreshed:
       → Log warning.
       → Skip cloud restore and queue processing.
       → Start with local repositories only (if any).
   → If token is valid:
       → Try RestoreRepositoriesFromCloudAsync (if no local bootstrap).
       → Try ProcessPendingQueueAsync (resume any pending uploads).
       → Both calls are wrapped in try/catch; failures log warnings and
         do not prevent application startup.
```

All cloud calls in the startup pipeline catch exceptions and log them as warnings. A network failure at startup never blocks the UI from appearing.

---

## Offline Snapshot Scheduling

`SnapshotSchedulerService` schedules automatic snapshots based on per-repository interval configuration. It does not check `ConnectivityStatusService`. Snapshots are created locally regardless of whether the cloud is reachable. When cloud sync resumes, `ProcessPendingQueueAsync` picks up any snapshots that were created while offline and queued for upload.

The local snapshot pipeline queues sync work before any cloud token refresh. If the cloud availability gate is closed, the repository is marked `offline_retry` and no HTTP request is made.

---

## Retention Offline

`EfRepositoryRetentionService` evaluates retention rules against local SQLite data only. It:

- Does not read or write the cloud state.
- Does not check `ConnectivityStatusService`.
- Soft-deletes local snapshots and marks orphan blocks for cleanup.
- Runs on a configurable schedule; the schedule is based on wall-clock time, not connectivity.

Blocks deleted by local retention may still exist in the cloud. See `REVERSE_SYNC.md` for recovery details.

---

## Reconnect Behavior

When connectivity is restored, no automatic "flush" is triggered by the connectivity service itself. The pending sync queue is processed on the next call to `ProcessPendingQueueAsync`, which is triggered:

- At startup (see above).
- When the user presses "Process queue" in App Settings → Sync.
- After a manual "Sync now" action on a repository.
- After `TryPushLatestSnapshotAsync` is called by the automatic snapshot pipeline.

Queue items with status `Retry` and a future `NextAttemptAtUtc` are skipped until their scheduled retry time arrives.

---

## Related Source Files

- `src/Veyra.Desktop/Services/Connectivity/ConnectivityStatusService.cs`
- `src/Veyra.Desktop/Services/Connectivity/IConnectivityStatusService.cs`
- `src/Veyra.Desktop/App.axaml.cs` — `RunDeferredStartupAsync`
- `src/Veyra.Desktop/Services/Scheduling/SnapshotSchedulerService.cs`
- `src/Veyra.Infrastructure.Data/Setup/Retention/EfRepositoryRetentionService.cs`
- `src/Veyra.Desktop/ViewModels/Pages/Settings/AppSettingsViewModel.cs` — `OnHasActiveProfileChanged`, `ResolveCloudConnectivityMessage`, `CanSyncToCloud`
- `src/Veyra.Infrastructure.Sync/Sync/Cloud/NoopRepositoryCloudSyncOrchestrator.cs`
