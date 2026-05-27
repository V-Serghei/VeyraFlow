# Cloud Restore

Cloud restore is now exposed through Cloud Repository Manager and Restore Wizard, not only through automatic startup logic. The current implementation supports latest-snapshot restore as a background operation. Full-history restore is visible but explicitly blocked until cloud-api exposes historical snapshot packages.

## Current UX Contract

Restore planning runs through `ICloudRepositoryManagementService.BuildRestorePlanAsync`. Restore execution is queued through `ICloudRepositoryManagementService.QueueRestoreAsync`.

The wizard shows:

- Which cloud repository will be restored.
- Target path.
- Whether the target path already exists.
- Snapshot count available for the current operation.
- Required blocks.
- Blocks already present locally.
- Blocks to download.
- Estimated bytes to download.
- Local/cloud conflicts detected from the latest cloud snapshot.

## Full History in the Cloud

The cloud is designed to preserve complete history independently from local retention. Local retention cleanup does not imply cloud cleanup.

Current behavior:

- Latest-snapshot restore is implemented.
- Full-history restore is not implemented.
- If the user selects full-history restore, the service returns a `not_implemented` conflict and does not start a destructive or misleading operation.

## Automatic Restore on a New Machine

The deferred startup pipeline in `App.axaml.cs` still calls `RestoreRepositoriesFromCloudAsync` automatically when the machine has no local bootstrap state.

Interactive restore should be preferred when local repositories already exist, because the user must see cloud-only repositories and choose what to restore instead of having cloud silently overwrite local state.

## Latest-Snapshot Restore Flow

1. Resolve active cloud profile and access token.
2. Request the latest snapshot package with `ICloudSyncService.GetLatestSnapshotAsync`.
3. Resolve target path from the wizard options or the default restored-cloud folder.
4. Download missing blocks only.
5. Reuse local blocks already present in the block store.
6. Restore files from the package without silently deleting unrelated local files.
7. Register or restore the local repository metadata.
8. Run a recovery scan with trigger `cloud_recovery_snapshot` and title `Recovery after cloud restore`.

## What Is Not Restored Yet

| Item | Status |
|---|---|
| Latest snapshot files | Implemented |
| Older snapshots / full history | Blocked until backend endpoints exist |
| Repository settings | Defaults are applied |
| Sync queue state | Not transferred |
| Detailed conflict-resolution strategies | Planned; current flow reports conflicts and uses safe restore |

## Dangerous Cloud Prune

Remove cloud-only history / align cloud with local state is intentionally blocked. It must not be enabled until the product has:

- Dry-run previews.
- Typed confirmation.
- Shared-block reference safety.
- Clear irreversible-risk UX.

## Related Source Files

- `src/Veyra.Infrastructure.Data/Sync/RepositoryCloudSyncOrchestrator.cs`
- `src/Veyra.Infrastructure.Data/Sync/CloudRepositoryManagementService.cs`
- `src/Veyra.Desktop/Views/Windows/CloudRepositoryManagerWindow.axaml`
- `src/Veyra.Desktop/Views/Windows/CloudInformationWindow.axaml`
- `src/Veyra.Desktop/Views/Windows/CloudRestoreWizardWindow.axaml`
- `src/Veyra.Infrastructure.Sync/Sync/Cloud/CloudSyncHttpService.cs`
- `server/cloud-api/internal/handlers/sync.go`
