# Repository Settings Save Pipeline

Repository settings save must be cheap when nothing changed and selective when only one area changed.

## Rules

- The UI keeps a field-aware dirty snapshot of persisted settings.
- If the current form equals the saved snapshot, the Save command is disabled and direct save calls return immediately.
- The application command has a second no-op guard so background or future callers cannot accidentally trigger heavy work for unchanged settings.
- Cloud queue processing runs only when cloud-related settings changed.
- Repository rescans and native setup apply run only when indexing setup changed.

## Change Categories

- Basic metadata: name, description, folder path.
- Tracking/indexing: linked formats and excluded patterns.
- Retention: local override flag, cleanup rules, schedule, storage mode, manual cleanup unlock, compaction.
- Cloud/sync: protected cloud metadata, conflict strategy, retry settings.

## Expensive Operations

- `IRepositoryScanner.ScanRepositoryAsync(... SaveFileVersions: false ...)` runs only after path, linked formats, or excluded patterns change.
- `INativeSetupApplier.ApplySetupAsync()` runs only after indexing setup changes.
- `IRepositoryCloudSyncOrchestrator.ProcessPendingQueueAsync()` runs only after cloud/sync settings change.
- Full `LoadAsync()` is not called after a successful save; the current form becomes the saved snapshot.

## Profiling

`UpdateRepositoryConfigurationHandler` logs per-stage timings:

- validation
- directory update
- repository DB save
- format linking
- scan
- native apply
- total duration

`RepositorySettingsViewModel.SaveAsync()` logs UI-side timings:

- validation
- mediator call
- optional cloud queue refresh
- total duration

These timings should be used before adding new save-time work.
