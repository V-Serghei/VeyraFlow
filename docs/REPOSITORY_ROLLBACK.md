# Repository Snapshot Rollback

Repository rollback restores the working folder to the file state of a selected snapshot without deleting older snapshot history.

## Rollback Mode

UI action: `Rollback repository to this snapshot` in `src/Veyra.Desktop/Views/Windows/SnapshotHistoryWindow.axaml`.

Application flow:

- `RepositoryExplorerViewModel.RollbackRepositoryToSelectedSnapshotAsync` requests a restore plan through `GetRepositorySnapshotRestorePlanQuery`.
- `GetRepositorySnapshotRestorePlanHandler` counts files that will be restored, files whose content differs from the current latest state, current files that are not present in the selected snapshot, and missing block keys.
- If blocks are missing, rollback is blocked before files are touched.
- `RestoreRepositorySnapshotHandler` restores the selected snapshot's file versions to the repository root.
- Current files that are not part of the selected snapshot are moved to a sibling backup folder outside the repository, `.veyra-rollback-backups/repository-{repositoryId}/snapshot-{snapshotId}-{timestamp}/`, instead of being deleted.
- Internal restore/rollback folders and Office lock files such as `~$document.docx` are ignored during rollback, so temporary files do not block restoring the real repository state.
- After successful rollback, `IRepositorySnapshotRepository.SaveSnapshotAsync` is called with `forceSnapshotCreation: true`.

The new snapshot uses:

- Trigger: `repository_rollback_snapshot`
- Title: `Rollback to snapshot {snapshotId}`

Old snapshots remain intact. Example:

```text
S1
S2
S3 current

rollback to S1

S1
S2
S3
S4 Rollback to snapshot 1
```

If cloud sync is enabled, the new rollback snapshot is pushed through `IRepositoryCloudSyncOrchestrator.TryPushLatestSnapshotAsync` like any other newly created snapshot. Existing cloud snapshots are not deleted.

## Restore As Copies Mode

UI action: `Restore snapshot as copies`.

This mode is intentionally non-destructive:

- Files are restored under `.veyra-restores/snapshot-{snapshotId}/`.
- Existing repository files are not overwritten.
- Extra current files are not moved.
- No new snapshot is created automatically.
- Cloud sync is not triggered automatically.

Use this when the user wants to inspect or manually recover files from an older snapshot without changing the current repository state.

The `.veyra-restores` folder is treated as an internal Veyra path by scanner/live sync filters and should not become part of future repository history.

## Block Safety

Both rollback and copy-restore call `IFileContentStore.FindMissingBlocksAsync` before restore starts. The native content store implementation checks managed `sha256-*` blocks and native block keys, and it attempts to hydrate archived blocks through `IRepositorySnapshotArchiveService` first.

If any required block is unavailable, the operation returns a clear error and does not start writing, overwriting, or moving files.

## Important Files

- `src/Veyra.Application/Commands/Repository/RestoreRepositorySnapshotCommand.cs`
- `src/Veyra.Application/Commands/Repository/RestoreRepositorySnapshotHandler.cs`
- `src/Veyra.Application/Queries/Repository/GetRepositorySnapshotRestorePlanQuery.cs`
- `src/Veyra.Application/Queries/Repository/GetRepositorySnapshotRestorePlanHandler.cs`
- `src/Veyra.Application/DTOs/Repository/Snapshots/RepositorySnapshotRestorePlanDto.cs`
- `src/Veyra.Application/DTOs/Repository/Snapshots/RepositorySnapshotRestoreResultDto.cs`
- `src/Veyra.Infrastructure.Data/Setup/Snapshots/EfRepositorySnapshotRepository.cs`
- `src/Veyra.Infrastructure.Native/Storage/RustFileContentStore.cs`
- `src/Veyra.Desktop/ViewModels/Pages/Explorer/RepositoryExplorerViewModel.cs`
- `src/Veyra.Desktop/Views/Windows/SnapshotHistoryWindow.axaml`
- `tests/Veyra.Application.Tests/RepositorySnapshotRestoreHandlerTests.cs`
