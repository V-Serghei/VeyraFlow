# Troubleshooting

Common issues and their fixes for VeyraFlow development.

---

## Retention: trigger "automatic" does not match filter

**Symptom**: A snapshot with trigger `"automatic"` is unexpectedly retained (or deleted) because the retention filter does not recognize it.

**Cause**: `IsAutomatic()` checks for known prefixes, not the literal word `"automatic"`:

```
auto_snapshot_*
scheduled_snapshot_*
initial_snapshot_*
```

**Fix**: Use a concrete trigger name — e.g. `"auto_snapshot_rust"` or `"scheduled_snapshot_rust"`. Never pass `"automatic"` as a trigger value.

---

## Post-restore hash mismatch

**Symptom**: After `RestoreFileVersionHandler` completes, the restored file's BLAKE3 hash does not match `expectedContentHash`.

**Meaning**: The block data stored for that file version differs from what was originally written. Two possible causes:

1. **Block corruption** — the block was written correctly but the stored bytes were later corrupted (storage fault, partial write).
2. **Tampered data** — the block content was modified after initial storage.

**Investigation steps**:

1. Re-read the block directly from `IFileContentStore` and compute its hash manually.
2. Compare against the `FileVersionBlock.Hash` value in the database.
3. If the database hash matches but the file on disk does not → storage-layer corruption.
4. If the database hash itself differs from the original snapshot record → data was tampered or a write race occurred.
5. Check the Rust native layer logs for AES-GCM decryption errors, which indicate a corrupted or wrong key.

---

## Cloud sync stuck / queue not draining

**Symptom**: Pending sync operations accumulate and are never processed.

**Checks**:

1. Call `ProcessPendingQueue()` on `RepositoryCloudSyncOrchestrator` manually to trigger a queue drain.
2. Check the dead-letter threshold: operations that have failed **3 or more times** are moved to dead-letter and will not be retried automatically.
3. Verify `SyncRetryMaxAttempts` and `SyncRetryBaseDelaySeconds` are set to sensible values in repository settings.
4. Confirm cloud connectivity — the orchestrator skips the queue when `CanSyncToCloud` is `false`.

---

## RetentionPolicyQualityGateTests: soft-delete read-back returns nothing

**Symptom**: A test applies retention, then tries to read back the "soft-deleted" snapshots and finds none.

**Cause**: Retention `Apply()` performs a **hard delete in a single pass**: it soft-marks rows and calls `ExecuteDeleteAsync` in the same transaction. By the time the transaction commits, the rows are gone — there is no intermediate soft-deleted state to read back.

**Fix**: Do not assert on soft-deleted entities after retention apply. Assert on the absence of rows (or query with `IgnoreQueryFilters()` before the operation completes).

---

## EF Core: soft-deleted entities not visible

**Symptom**: A query returns zero results for rows you know exist; `IsDeleted = true` on those rows.

**Cause**: `VeyraDbContext` applies a global query filter that excludes `IsDeleted == true` by default.

**Fix**: Chain `IgnoreQueryFilters()` when you intentionally need to see deleted records:

```csharp
await _context.Snapshots
    .IgnoreQueryFilters()
    .Where(s => s.RepositoryId == id)
    .ToListAsync();
```

---

## Avalonia namespace errors

### ScrollBarVisibility

```
// WRONG
using Avalonia.Controls;

// CORRECT
using Avalonia.Controls.Primitives;
```

`ScrollBarVisibility` lives in `Avalonia.Controls.Primitives`, not `Avalonia.Controls`.

### Other common misplaced types

| Type | Correct namespace |
|---|---|
| `ScrollBarVisibility` | `Avalonia.Controls.Primitives` |
| `ScrollViewer` | `Avalonia.Controls` |
| `CornerRadius` | `Avalonia` |
| `Thickness` | `Avalonia` |

When adding a converter, always verify the full namespace via NuGet source before committing.

---

## Test FakeContentStore: missing interface members

**Symptom**: `FakeContentStore` does not compile, or tests fail at runtime with `NotImplementedException`.

**Cause**: `IFileContentStore` requires all members to be implemented. A minimal fake must include:

```csharp
Task<string> StoreFileAsync(Stream content, string hash);
Task<Stream> RestoreFileAsync(string hash, string expectedContentHash);
```

Note the `expectedContentHash` parameter on `RestoreFileAsync` — omitting it or using the wrong signature causes a compile error or silent interface mismatch.

---

## RepositoryDto constructor: missing positional parameters

**Symptom**: `CS1729` or `ArgumentException` when constructing `RepositoryDto` in tests or mappers.

**Cause**: `RepositoryDto` is a positional record. All parameters are required, including:

- `TotalSizeBytes` (long)
- `RetentionPolicy` (RetentionPolicyDto or null)

**Fix**: Pass all positional arguments in the correct order. Do not use object initializer syntax to skip parameters — add them explicitly, using `null` where optional semantics are intended.
