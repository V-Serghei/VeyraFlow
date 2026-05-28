# Global Cloud Settings

Global Cloud Settings control the overall cloud behavior. They must not make local repositories unusable when cloud is offline.

## Required Controls

- Cloud account status.
- Server availability.
- Automatic sync enabled/disabled.
- Background sync interval.
- Sync on startup.
- Sync on repository change.
- Sync queue view.
- Global restore options.
- Cloud repository browser.
- Cloud storage usage.
- Cloud cleanup options.
- Danger zone for irreversible cloud delete.

## Required Status

- Online/offline/cloud unavailable/unauthorized.
- Last health check.
- Last successful sync.
- Pending operations.
- Failed operations.
- Paused state.

## Danger Zone

Dangerous global actions include:

- Delete all cloud repositories.
- Remove cloud-only history.
- Align cloud with current local state.

These actions must require explicit confirmation text and must explain that cloud history may become unrecoverable.

