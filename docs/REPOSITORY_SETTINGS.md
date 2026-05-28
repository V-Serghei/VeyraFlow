# Repository Settings

Documents the repository settings system in VeyraFlow.

## Components

| Layer | File |
|---|---|
| ViewModel | `src/Veyra.Desktop/ViewModels/RepositorySettingsViewModel.cs` |
| View | `src/Veyra.Desktop/Views/RepositorySettingsView.axaml` |
| Command | `src/Veyra.Application/Commands/Repository/UpdateRepositoryCommand.cs` |
| Handler | `src/Veyra.Application/Commands/Repository/UpdateRepositoryHandler.cs` |
| Repository | `IRepositoryRepository.UpdateRepositoryAsync` |

## Save Flow

1. ViewModel binds user input to properties.
2. On save, ViewModel dispatches `UpdateRepositoryCommand`.
3. FluentValidation runs before the handler (pipeline behavior).
4. Handler calls `IRepositoryRepository.UpdateRepositoryAsync`.

Validation errors surface back to the ViewModel as inline field errors.

## Retention Policy Section

- Controlled by `IsRetentionSectionExpanded` (bool, default `false` — collapsed).
- Expanding the section reveals retention-specific fields.
- `RetentionPolicyOverrideEnabled`:
  - `true` — the repository uses its own local policy.
  - `false` — falls back to the parent group policy, then the global policy.

### Retention Triggers

The "automatic" filter covers any trigger whose name starts with one of:

```
auto_snapshot_
scheduled_snapshot_
initial_snapshot_
```

`IsAutomatic()` checks for these prefixes. Passing the literal string `"automatic"` will not match — use a concrete trigger name such as `"auto_snapshot_rust"` or `"scheduled_snapshot_rust"`.

### Never-Delete Tags

Snapshots tagged with any of the following are excluded from retention deletion:

```
#keep
#protected
#never-delete
#no-delete
#навсегда
#не_удалять
```

Tags are normalized before comparison: `#` is stripped, special characters are removed, and the result is lowercased.

### Retention Status Display

`HumanizeRetentionStatus()` converts the raw `LastRetentionStatus` enum value to a human-readable string shown in the UI.

## Maintenance Window

The maintenance window field is **optional**. It has been removed from required validation — leaving it blank is valid and disables scheduled maintenance.

## Sync Settings

| Property | Description |
|---|---|
| `SyncRetryMaxAttempts` | Maximum number of retry attempts for a failed sync operation |
| `SyncRetryBaseDelaySeconds` | Base delay (seconds) for exponential back-off between retries |

### Sync Conflict Strategies

| Value | Behavior |
|---|---|
| `last_write_wins` | The most recently modified version is kept |
| `manual_merge` | Conflict is surfaced to the user for manual resolution |
| `preserve_both` | Both versions are kept with disambiguating names |

## Validation Rules (FluentValidation)

- Repository name: required, max length enforced.
- `SyncRetryMaxAttempts`: must be a positive integer.
- `SyncRetryBaseDelaySeconds`: must be a positive number.
- Maintenance window: optional (no required rule).
- Retention fields: validated only when `RetentionPolicyOverrideEnabled` is `true`.
