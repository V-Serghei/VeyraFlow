# Application Structure

`Veyra.Application` owns use cases, contracts, DTOs, validation, and cross-cutting pipeline behavior.

It must not depend on EF Core implementations, Avalonia, native code, or concrete cloud HTTP clients.

## Folders

```text
Abstractions/
  Auth/
  Indexing/
  Observability/
  Security/
  Setup/
  Sync/

Commands/
  Auth/
  Repository/
  Security/
  Setup/

Queries/
  Repository/
  Search/
  Security/

DTOs/
Common/
Services/
DependencyInjection.cs
```

## Command And Query Rules

- Commands mutate state and return `OperationResult` or `OperationResult<T>`.
- Queries read state and return DTOs directly.
- Handlers should depend on the smallest Application abstraction they need.
- Validators live next to commands when validation is not trivial.
- Do not inject Infrastructure implementations into handlers.

## Feature-Oriented Direction

New repository use cases should use feature subfolders instead of expanding the flat `Commands/Repository` and `Queries/Repository` folders.

Recommended command folders:

```text
Commands/Repository/Management/
Commands/Repository/Versioning/
Commands/Repository/Restore/
Commands/Repository/Maintenance/
Commands/Repository/Retention/
Commands/Repository/Bundle/
Commands/Repository/Settings/
```

Recommended query folders:

```text
Queries/Repository/Settings/
Queries/Repository/Entries/
Queries/Repository/History/
Queries/Repository/Versions/
Queries/Repository/Diff/
Queries/Repository/Bundle/
```

Existing files can move incrementally. Keep namespaces stable during moves unless all call sites are updated in the same change.

## Interface Segregation Targets

`IRepositorySnapshotRepository` is too broad. Split it by responsibility during the next snapshot refactor:

- `ISnapshotReadRepository`: latest entries, history, changed files.
- `ISnapshotWriteRepository`: full snapshot save and live delta apply.
- `ISnapshotDiffRepository`: pending/version diff previews and stored text diffs.
- `ISnapshotRestoreRepository`: file-version restore data and text content.
- `ISnapshotRetentionRepository`: retention cleanup support if needed.

Do not add unrelated methods to broad interfaces unless it is required for production and the split cannot happen in the same change.
