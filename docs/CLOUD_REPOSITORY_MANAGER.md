# Cloud Repository Manager

Cloud Repository Manager is the product surface for discovering, restoring, comparing, syncing, and deleting cloud repositories. Cloud must not be a hidden background-only subsystem: users must be able to see what exists remotely, what is linked locally, what is missing locally, and which operations are safe or irreversible.

## Entry Points

- Main page: Cloud information button.
- Global Settings -> Cloud: Cloud manager button.
- Repository Settings -> Cloud: Cloud manager button.
- Cloud Information Window: opens the manager for actions.

## Implemented Surface

The UI depends on `ICloudRepositoryManagementService` from `src/Veyra.Application/Abstractions/Sync/`. The implementation lives in `src/Veyra.Infrastructure.Data/Sync/CloudRepositoryManagementService.cs` and returns data-only DTOs from `src/Veyra.Application/DTOs/CloudSync/`.

Current implementation:

- `CloudRepositoryManagerWindow` lists remote repositories, linked local repositories, cloud-only repositories, restore state, sync state, and operation state.
- `CloudInformationWindow` gives a read-only summary and links to the manager.
- `CloudRestoreWizardWindow` builds a real latest-snapshot restore plan.
- Restore, compare, sync-now, and cloud-delete are queued as background operations through `CloudRepositoryOperationTracker`.
- Delete from cloud requires a UI confirmation and a service-level typed confirmation: `DELETE {cloudRepositoryId}`.
- Full-history restore and remove cloud-only history are intentionally blocked until backend history packages and safe cloud pruning are implemented.

## Repository Row Fields

Each cloud repository row shows:

- Repository name.
- Cloud repository id.
- Original or local path when known.
- Whether a local copy is linked.
- Whether the local path exists.
- Snapshot count available from the current cloud summary.
- Latest snapshot date.
- Cloud size when available.
- Sync status.
- Restore status.
- Conflict status.

## Actions

Safe actions:

- Restore.
- Restore to a selected path through the wizard.
- Compare with local.
- Sync now.

Dangerous actions:

- Delete from cloud.
- Remove cloud-only history.
- Permanently align cloud with local state.

Only delete-from-cloud currently calls the backend. Cloud-only history pruning is blocked and removes nothing until dry-run previews and shared-reference deletion safety are implemented.

## Non-Blocking Rule

Cloud manager actions create background operations. They must not block navigation, local repository work, snapshot creation, file browsing, settings, or local restore.

Each operation exposes:

- Operation id.
- Status.
- Current phase.
- Progress.
- Error state.
- Final summary.
