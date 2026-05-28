# Cloud Conflict Resolution

Cloud conflict resolution exists to prevent silent data loss. A conflict is any case where local and cloud disagree about repository identity, file state, snapshot history, or deletion state.

## Conflict Types

- Local-only file.
- Cloud-only file.
- File changed locally.
- File changed in cloud.
- File changed on both sides.
- File deleted locally but present in cloud.
- File deleted in cloud but present locally.
- Snapshot exists only in cloud.
- Snapshot exists only locally.
- Repository path mismatch.
- Nested repository ownership mismatch.

## Required UI

The conflict UI must show:

- Affected files.
- Local-only changes.
- Cloud-only changes.
- Modified-on-both files.
- Deleted-local / exists-cloud files.
- Deleted-cloud / exists-local files.
- Recommended safe action.

## Safe Defaults

- Preserve both histories.
- Create a recovery snapshot.
- Do not overwrite local files unless the user explicitly chooses it.
- Do not delete cloud history unless the user confirms an irreversible action.

## Merge Strategy

Automatic merge is allowed only when it is provably non-destructive. Otherwise the UI must ask the user.

Possible strategies:

- Keep local.
- Keep cloud.
- Keep both.
- Restore cloud copy as separate file/path.
- Create recovery snapshot and defer decision.

