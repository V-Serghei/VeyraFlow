# Retention Policies

Retention policies are cleanup rules for repository history. They decide which old snapshots, file versions, diff cache rows, and unused local block files can be removed or archived.

## Defaults

- Retention is disabled by default.
- Local automatic cleanup is off by default.
- Cloud history is preserved by default.
- Manual cleanup is allowed only when the user explicitly starts it.
- Local cleanup is not the same thing as cloud cleanup.

## User-facing language

- Office mode should describe retention as "old version cleanup rules".
- Professional mode may use the technical name "retention policy".
- Office mode can hide advanced controls, but it must not hide the fact that cleanup rules exist.
- Help pages must always show the full settings path: `Settings -> Storage & cleanup -> Retention policies`.

## Policy hierarchy

Effective policy resolution is deterministic:

1. Nested repository local override.
2. Repository local override.
3. Nearest parent repository override.
4. Global policy.
5. Disabled policy.

If a repository has a local override, global settings must not override it. If a nested repository has no local override, it inherits from the nearest parent repository that has one; only then does it fall back to global rules.

Global cleanup must iterate repositories, but each repository must be cleaned using its own effective policy.

## Safety rules

- The safest first scope is automatic snapshots only.
- Manual snapshots are protected by default and require explicit unlock plus confirmation before cleanup can touch them.
- A dry run should be completed before saving or applying a local policy.
- A maintenance window is required for scheduled automatic cleanup.
- Current live files must never be deleted by retention cleanup.

## Never-delete protection

The cleanup planner must always skip:

- archived snapshots
- snapshots tagged `#keep`
- snapshots tagged `#protected`
- snapshots tagged `#no-delete`
- snapshots tagged `#never-delete`
- snapshots tagged `#не_удалять`
- snapshots tagged `#навсегда`

These tags are intentionally simple so office users can understand them. If a user wants to keep a snapshot or the important file versions represented by that snapshot, they should add one of these tags before enabling cleanup.

## Block deletion

Retention cleanup must remove expired metadata/history references first, then recompute the block reference graph.

A physical block may be deleted only if no remaining active reference points at the same block hash, including:

- snapshot links
- file versions
- retained history entries
- active repository state
- deduplicated/shared references from other repositories

Cloud synchronization state is separate. Deleting a local orphaned block must not imply that cloud-only history is pruned.

## Cloud history

By default, cloud stores the full history even if local retention removed local history. The expected user model is:

- "Clean local history" saves local space.
- "Restore full history from cloud" brings cloud-preserved history back locally when supported.
- "Sync cloud history with local state" is a separate dangerous operation and must require an explicit irreversible-loss confirmation.

Do not implement cloud pruning as a side effect of local cleanup.

## Delete vs archive mode

- Delete mode permanently removes cleanup candidates locally and then deletes now-unreferenced local block files.
- Archive mode keeps the snapshot visible and moves heavy local block payloads to the snapshot archive when possible.
- Archived snapshots are treated as protected history and are not later selected by delete-mode cleanup.

## Guest mode and offline behavior

- Retention policies are **fully local** — they run without cloud or internet.
- Guest users (no account) can use retention if the Pro experience is enabled; in Basic mode the UI is hidden but the engine still executes scheduled cleanup.
- Cloud history is preserved independently: local retention removes local data only. Cloud history is never pruned as a side effect.
- If the app starts offline, scheduled retention and cleanup continue to run normally.
- Retention execution never requires authentication, token refresh, or network access.
- If a repository has cloud sync enabled but the cloud is unavailable, local retention proceeds; cloud reconciliation is deferred to the next sync.

## What auto-snapshots do in each mode

| Mode | Auto-snapshots |
|------|---------------|
| Professional | Full config via Automation tab (interval, quiet hours, scope) |
| Basic (office) | Simplified toggle in General tab — on/off only, uses safe defaults |
| Guest | Same as the chosen experience mode — no cloud dependency |
| Offline | Continues as scheduled, no cloud push until reconnected |

## Implementation map

- Retention execution: `src/Veyra.Infrastructure.Data/Setup/Retention/EfRepositoryRetentionService.cs`
- Retention DTO: `src/Veyra.Application/DTOs/Repository/Retention/RepositoryRetentionPolicyDto.cs`
- Policy source constants: `src/Veyra.Application/DTOs/Repository/Retention/RepositoryRetentionPolicySources.cs`
- Repository settings UI: `src/Veyra.Desktop/Views/Pages/RepositorySettings/RepositorySettingsView.axaml`
- Repository settings logic: `src/Veyra.Desktop/ViewModels/Pages/RepositorySettings/RepositorySettingsViewModel.cs`
- Help Center articles: `src/Veyra.Desktop/ViewModels/Windows/Shell/InfoWindowViewModel.cs`
- User-facing strings: `src/Veyra.Desktop/Localization/en.json` and `src/Veyra.Desktop/Localization/ru.json`
