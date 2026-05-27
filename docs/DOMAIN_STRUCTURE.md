# Domain Structure

`Veyra.Domain` contains persisted domain entities and domain constants. It must not depend on Application, Infrastructure, Desktop, EF Core, HTTP, or Avalonia.

## Current Entity Areas

```text
Entities/
  Repository/
    Repository.cs
    RepositorySnapshot.cs
    RepositorySnapshotEntry.cs
    RepositorySyncQueueItem.cs
    SnapshotFileLink.cs

  FileVersioning/
    FileIdentity.cs
    FileSnapshot.cs
    FileVersion.cs
    FileVersionBlock.cs

  TextDiff/
    FileVersionTextDiff.cs
    FileVersionTextDiffHunk.cs
    FileVersionTextDiffLine.cs
    TextLineAtom.cs

  Watched/
    WatchedDirectory.cs
    WatchedDirectoryFormat.cs
    D_WatchedFormat.cs

  OperationJournalEntry.cs
  UserProfile.cs
```

## Bounded Context Direction

Future additions should use bounded-context folders:

- `Repository`
- `Snapshot`
- `FileVersioning`
- `BlockStorage`
- `Retention`
- `Sync`
- `Security`
- `Monitoring`
- `Settings`

Only create a new folder when there are multiple related entities or a clear domain boundary.

## Rules

- Domain entities should not contain UI text.
- Domain entities should not call file system, database, HTTP, native code, or DI.
- Keep persistence annotations out unless they are unavoidable and provider-neutral.
- Put behavior here only when it is true domain behavior, not orchestration.
