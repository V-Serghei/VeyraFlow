# Known Issues and Gotchas

## Retention / Snapshot Triggers

### `"automatic"` is not an automatic trigger
`RepositorySnapshotTriggerClassifier.IsAutomatic("automatic")` returns **false**.
`IsAutomatic` checks prefixes: `"auto_snapshot_"`, `"scheduled_snapshot_"`, `"initial_snapshot_"`.

**In tests** seed snapshots with `Trigger = "auto_snapshot_rust"` (or any `auto_snapshot_*`), not `"automatic"`.

**In retention filter** the repository trigger filter `"automatic"` means "match snapshots that are classified as automatic triggers" — i.e., triggers starting with `auto_snapshot_`, `scheduled_snapshot_`, `initial_snapshot_`. The literal string `"automatic"` as a snapshot trigger is classified as **manual**.

### Retention apply is a hard-delete in one transaction
`EfRepositoryRetentionService.RunRetentionAsync(dryRun: false)` does:
1. Soft-mark (set `IsDeleted=true`) on snapshots, links, versions, blocks
2. Immediately hard-delete those same rows via `ExecuteDeleteAsync`

All in a **single transaction**. After the method returns, deleted rows are physically gone — no soft-delete record exists.
In tests: assert `Null` after apply, not `IsDeleted == true`.

---

## EF Core / SQLite

### Soft-deleted entities
Global query filter hides `IsDeleted = true` rows. Always call `.IgnoreQueryFilters()` when you need deleted rows:
```csharp
await db.Set<RepositorySnapshot>().IgnoreQueryFilters()...
```

### ExecuteUpdateAsync vs SaveChanges
Bulk updates via `ExecuteUpdateAsync` bypass EF change tracking. Do not mix bulk updates with tracked entity modifications in the same logical unit without refreshing.

---

## Query Namespaces

All repository queries (including GetAllRepositories, GetRepositoryDetail) live in `Veyra.Application.Queries.Repository`.
There is NO `Veyra.Application.Queries` namespace — the root-level was consolidated into `Repository/` subfolder.

```csharp
// Correct
using Veyra.Application.Queries.Repository;
// ...
mediator.Send(new GetAllRepositoriesQuery())
mediator.Send(new GetRepositoryDetailQuery(id))
```

---

## Domain Entity Namespaces

All domain entities use `namespace Veyra.Domain.Entities;` regardless of subfolder location:
- `Entities/Repository/*.cs` → still `Veyra.Domain.Entities`
- `Entities/FileVersioning/*.cs` → still `Veyra.Domain.Entities`
- `Entities/TextDiff/*.cs` → still `Veyra.Domain.Entities`
- `Entities/Watched/*.cs` → still `Veyra.Domain.Entities`

No using changes needed when referencing entities — one namespace covers all.

---

## Avalonia Namespace Errors

| Type | Correct Namespace |
|------|-------------------|
| `ScrollBarVisibility` | `Avalonia.Controls.Primitives` |
| `ScrollViewer` | `Avalonia.Controls` |
| `CornerRadius`, `Thickness` | `Avalonia` (base) |
| `Button`, `TextBox` | `Avalonia.Controls` |

**Always verify** namespace via NuGet source before adding a new converter or control type.

---

## Test Fake Implementations

### IFileContentStore (since v+hash validation)
Must implement `RestoreFileAsync` with the full signature including `expectedContentHash`:
```csharp
Task<long> RestoreFileAsync(
    IReadOnlyList<StoredFileBlockDto> blocks,
    string targetPath,
    bool overwriteExisting,
    string? expectedContentHash = null,
    CancellationToken ct = default);
```

### RepositoryDto constructor
Requires all positional params — there is no short constructor:
```csharp
new RepositoryDto(
    Id: id, Name: "test", Description: null,
    DirectoryId: 1, DirectoryPath: path,
    LinkedFormats: [], ExcludedPatterns: [],
    IsDeleted: false, FileCount: 0, VersionCount: 0,
    TotalSizeBytes: 0L, LastScannedAt: null,
    RetentionPolicy: new RepositoryRetentionPolicyDto(false, null, null, null, [], 0, null, null, null, null))
```

### RestoreFileVersionCommand argument order
```csharp
new RestoreFileVersionCommand(
    RepositoryId: int,
    RelativePath: string,   // ← second!
    FileVersionId: long,    // ← third!
    OverwriteCurrent: bool,
    TargetPath: string?)
```

---

## Post-Restore Hash Mismatch

`InvalidOperationException: Restored file hash does not match the expected content hash`

Causes:
- Block corruption in storage (zstd decompression produced wrong bytes)
- Tampered block file on disk
- Bug in `RustFileContentStore` reconstruction logic

Investigation: compare `ContentHashSha256` on `FileVersion` row against SHA-256 of actual file after restore.
