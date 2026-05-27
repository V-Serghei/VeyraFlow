# Feature Organization

VeyraFlow should move toward feature-oriented folders instead of very large technical buckets. The goal is not cosmetic renaming; move code when it clarifies ownership and reduces coupling.

## Application Features

Repository commands and queries should be grouped by user-facing capability:

```text
Commands/Repository/
  Management/
  Settings/
  Versioning/
  Restore/
  Retention/
  Cleanup/
  Cloud/
  Bundle/
  Maintenance/

Queries/Repository/
  Settings/
  Entries/
  History/
  Versions/
  Diff/
  Tags/
  Bundle/
```

Each feature folder can contain:

- command or query records
- handlers
- validators
- local mapping helpers
- feature-specific DTOs when the DTO is not shared elsewhere

## Placement Rules

- Put orchestration in Application handlers or Application services.
- Put persistence details in Infrastructure implementations.
- Put UI state and presentation-only behavior in Desktop ViewModels/services.
- Put domain state and domain-only rules in Domain.
- Do not create a shared helper folder just because two features have similar names; share only stable concepts.

## Repository Feature Boundaries

- `Management`: create, rename, delete, restore repository records.
- `Settings`: repository preferences, sync settings, retention settings, format tracking settings.
- `Versioning`: snapshots, file versions, snapshot deltas, initial snapshot lifecycle.
- `Restore`: restore file version as copy, restore with replacement, restore repository history.
- `Retention`: policy resolution, dry run, cleanup planning, cleanup execution.
- `Cloud`: queueing, cloud history restore, cloud state reconciliation.
- `Diff`: pending diff preview, version diff preview, stored text diff cache.
- `Bundle`: archive/export/import package flows.
- `Maintenance`: repair, missing-data checks, integrity checks, safe rebuild operations.

## Interface Segregation

Large abstractions should be split by feature ownership. `IRepositorySnapshotRepository` is the main known target:

- `ISnapshotReadRepository`
- `ISnapshotWriteRepository`
- `ISnapshotDiffRepository`
- `ISnapshotRestoreRepository`
- `ISnapshotRetentionRepository`

Handlers should request the smallest interface that supports their use case. This keeps future tests smaller and prevents one feature from depending on unrelated snapshot behavior.

## Migration Strategy

Move files incrementally:

1. Create the target feature folder.
2. Move a small cohesive set of commands/queries.
3. Keep namespace changes contained in the same commit.
4. Update tests with the moved feature.
5. Do not mix behavior changes with large folder moves unless the behavior change is required to complete the move.
