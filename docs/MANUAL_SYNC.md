# Manual Sync

Manual Sync is a user-triggered background job. It is not a blocking modal operation.

## UX Contract

When the user clicks Sync now:

- Create a background operation.
- Keep navigation enabled.
- Keep repository pages usable.
- Keep snapshot creation usable.
- Show status and progress in the cloud area.
- Offer cancel/pause when safe.
- Show a final summary.

## Required Phases

1. Validate cloud session.
2. Check connectivity state.
3. Build local manifest.
4. Fetch remote manifest.
5. Compare local and cloud head.
6. Resolve or report conflicts.
7. Upload metadata.
8. Upload missing blocks.
9. Finalize remote state.
10. Record operation summary.

## Conflict Behavior

Manual sync must not silently overwrite or delete. If local and cloud diverge, show a diff summary and offer safe choices:

- Keep local and upload new snapshot.
- Keep cloud and restore to a separate path.
- Preserve both by creating a recovery snapshot.
- Open conflict details.

## Summary

The completion summary must include:

- Uploaded snapshots.
- Uploaded blocks.
- Skipped blocks.
- Uploaded bytes.
- Downloaded bytes, if any.
- Conflicts detected.
- Retry count.
- Final status.

