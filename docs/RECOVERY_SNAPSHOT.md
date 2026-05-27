# Recovery Snapshot

A Recovery Snapshot records the state created after cloud restore or cloud/local comparison when there is a risk of losing history.

## When To Create

Create a recovery snapshot after:

- Latest-snapshot cloud restore through `RestoreRepositoryFromCloudAsync`.
- Restoring cloud history into an existing local folder.
- Relinking a local folder to a cloud repository.
- Detecting local/cloud divergence.
- Applying a conflict strategy that changes local files.
- Completing a restore where local files were different from cloud head.

## What It Records

The long-term model should record:

- Post-restore local state.
- Cloud head used for comparison.
- Local-only files.
- Cloud-only files.
- Locally deleted files that still exist in cloud.
- Locally modified files.
- Files modified on both sides.
- Blocks reused locally.
- Blocks downloaded from cloud.
- Files skipped because blocks were missing.

## Naming

Use a clear trigger and title:

- Trigger: `cloud_recovery_snapshot`.
- Title: `Recovery after cloud restore`.

The UI should label it as a recovery point, not as a normal automatic snapshot.

## Retention

Recovery snapshots must be protected by default. Retention may only remove them if the user explicitly allows cleanup of protected/manual recovery history.

## Current Implementation

`RepositoryCloudSyncOrchestrator.RestoreRepositoryFromCloudAsync` restores the latest cloud snapshot and immediately runs `ScanRepositoryCommand` with:

- Trigger: `cloud_recovery_snapshot`.
- Title: `Recovery after cloud restore`.

Full-history recovery snapshots and detailed divergence metadata are still pending because full-history cloud packages and compare result persistence are not implemented yet.

## Related: Repository Rollback

Repository rollback is a separate local history operation. It restores the repository working folder to the state of a selected local snapshot and then creates a new snapshot with trigger `repository_rollback_snapshot`. Details are documented in `docs/REPOSITORY_ROLLBACK.md`.
