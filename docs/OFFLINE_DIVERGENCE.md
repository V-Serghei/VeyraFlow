# Offline Divergence

Offline divergence happens when local work continues while cloud is unavailable, or when cloud has history that local storage no longer has.

## Common Causes

- Cloud server offline.
- User logged out and later logged in.
- Local data cleanup.
- Local repository deleted.
- Local folder moved.
- Local files changed while sync was paused.
- Retention removed local history but cloud retained it.

## Required Behavior

- Keep local work fully available.
- Queue cloud sync.
- Detect divergence before upload/restore.
- Show a comparison summary.
- Preserve both histories by default.
- Create a recovery snapshot when restoring into existing local data.

## Resolution Options

- Upload local changes as a new cloud snapshot.
- Restore cloud data to a separate path.
- Relink local folder to cloud repository.
- Restore full cloud history.
- Keep local and cloud as separate repositories.
- Permanently prune cloud-only history only after explicit danger confirmation.

## Non-Destructive Default

If the system cannot prove that an action is safe, it must not delete, overwrite, or prune. The user should receive a clear status and a safe next action.

