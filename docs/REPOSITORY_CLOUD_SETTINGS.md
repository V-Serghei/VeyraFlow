# Repository Cloud Settings

Repository Cloud Settings show and control cloud behavior for one repository.

## Required Fields

- Cloud sync enabled.
- Linked cloud repository id.
- Last sync time.
- Sync status.
- Pending upload/download.
- Cloud history status.
- Restore availability.
- Conflict status.
- Effective sync strategy.
- Local storage used.
- Cloud storage used.

## Required Actions

- Sync now.
- Restore full history from cloud.
- Compare local with cloud.
- Open cloud snapshots.
- Relink with cloud repository.
- Disable cloud sync for this repository.
- Delete this repository from cloud.
- Permanently align cloud with local state.

## Effective State

The UI must make the relationship clear:

- Linked and healthy.
- Linked but cloud unavailable.
- Linked but local folder missing.
- Cloud-only.
- Local-only.
- Diverged.
- Waiting for restore.
- Waiting for upload.

## Safety

Dangerous actions use warning styling and irreversible confirmation. Safe actions must be preferred and presented first.

