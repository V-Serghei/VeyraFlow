# Cloud Information Window

Cloud Information is the read-only overview of cloud status. It opens quickly and can hand the user off to Cloud Repository Manager for actions.

## Implemented Content

- Account name and email.
- Connection status.
- Last successful sync.
- Pending operations.
- Failed operations.
- Cloud storage used.
- Cloud repository count.
- Cloud snapshot count.
- Cloud block count.

## Repository Groups

- Linked locally.
- Available only in cloud.
- Conflicted.

Missing local folder and waiting-for-restore states are represented in repository rows through restore status. Deeper grouping can be added without changing the UI contract because the window reads from `ICloudRepositoryManagementService`.

## Entry Points

- Main page.
- Cloud Repository Manager can be opened from Global Settings -> Cloud.
- Cloud Repository Manager can be opened from Repository Settings -> Cloud.

## UX Rules

- The window refreshes through `ICloudRepositoryManagementService`.
- Offline state is explicit.
- The user should not need logs to understand whether cloud is linked, offline, or waiting.
