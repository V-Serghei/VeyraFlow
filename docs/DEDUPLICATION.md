# Deduplication

This document describes how VeyraFlow avoids storing redundant data at both the block level and the file level, how deduplication interacts with retention and deletion, and the constraints that govern cross-repository isolation.

---

## Table of Contents

1. [Chunking Strategy](#chunking-strategy)
2. [Hashing](#hashing)
3. [Block Metadata Table](#block-metadata-table)
4. [Block Existence Check](#block-existence-check)
5. [Reference Tracking](#reference-tracking)
6. [Deduplication Between Snapshots](#deduplication-between-snapshots)
7. [File-Level Deduplication](#file-level-deduplication)
8. [Cross-Repository Isolation](#cross-repository-isolation)
9. [Deduplication and Retention Interaction](#deduplication-and-retention-interaction)
10. [Block Deletion Safety](#block-deletion-safety)
11. [Block Compression](#block-compression)

---

## Chunking Strategy

Files are split into **fixed-size chunks** before hashing and storage.

| Parameter | Value |
|---|---|
| Default chunk size | 64 KB (65,536 bytes) |
| Minimum chunk size | 4 KB (4,096 bytes) |
| Maximum chunk size | 4 MB (4,194,304 bytes) |

The chunk size is passed to the Rust native entrypoint `veyra_store_file_blocks_utf8` as the `chunkSize` parameter (clamped to the min/max range). The managed fallback (`StoreFileManagedAsync`) always uses the 64 KB default.

Files smaller than the chunk size produce a single block. Empty files produce zero blocks; they are stored as empty `FileVersion` records with `SizeBytes = 0` and no `FileVersionBlock` rows.

---

## Hashing

Two hashing schemes are used depending on the storage path.

### Native blocks — BLAKE3 (via Rust)

The native Rust library computes **BLAKE3** hashes for each block. The resulting hash is a 256-bit value represented as 64 lowercase hex characters. This hash is stored directly as the `BlockStorageKey`.

BLAKE3 properties relevant to deduplication:
- Deterministic: same bytes always produce the same hash.
- Collision-resistant: two different content blocks will not share a key under normal operation.
- The 64-character hex string uniquely identifies a block in the physical block store.

### Managed blocks — SHA-256 with prefix

When the native library is unavailable or encryption is enabled, `StoreFileManagedAsync` computes a **SHA-256** hash of the stored (possibly encrypted + compressed) bytes — not of the plaintext. The `BlockStorageKey` is stored as:

```
sha256-<64-hex-chars>
```

The `"sha256-"` prefix is the `ManagedHashPrefix` constant and distinguishes managed block keys from native block keys. Code that processes block keys checks for this prefix to determine which storage path to read from.

---

## Block Metadata Table

The `FileVersionBlock` table holds one row per block per file version.

| Column | Type | Description |
|---|---|---|
| `Id` | `long` | Primary key |
| `FileVersionId` | `long` | Foreign key to `FileVersion` |
| `Sequence` | `int` | 0-based chunk ordering within the file |
| `BlockStorageKey` | `string` | BLAKE3 hex (64 chars) or `sha256-<hex>` |
| `LengthBytes` | `int` | Original uncompressed block size |
| `StoredSizeBytes` | `long` | On-disk size of the block file (after compression) |
| `CreatedAt` | `DateTime` | UTC timestamp |
| `IsDeleted` | `bool` | Soft-delete flag (set during retention) |
| `DeletedAt` | `DateTime?` | UTC timestamp when soft-deleted |

### Indexes

- `(FileVersionId, Sequence)` — unique composite index; enforces ordering integrity and prevents duplicate block entries.
- `BlockStorageKey` — index for reference-count queries during retention and integrity checks.

---

## Block Existence Check

Before writing a new block file to disk, the code checks whether a file already exists at the hash-derived path:

```csharp
if (!File.Exists(blockPath))
{
    // Attempt to create with FileMode.CreateNew
}
else
{
    dedupedBlocks++;
}
```

The `FileMode.CreateNew` flag on the write stream ensures atomicity: if two concurrent processes race to create the same block, only one succeeds. The losing thread receives an `IOException`, which is caught and treated as a dedup hit (the block already exists). This makes the check TOCTOU-safe under normal file system semantics.

The native path uses atomic temp-file rename within the Rust library, achieving the same effect.

---

## Reference Tracking

There is **no explicit reference count column** on any block table. Reference counts are computed on-demand by querying:

```sql
SELECT COUNT(*)
FROM FileVersionBlock
WHERE IsDeleted = 0
  AND BlockStorageKey = @hash
```

This approach avoids increment/decrement synchronization issues at the cost of requiring a query during retention cleanup. The query uses the `BlockStorageKey` index to remain efficient.

---

## Deduplication Between Snapshots

Because the `BlockStorageKey` is a content hash:

- A file that has not changed between two snapshots will produce blocks with the same keys.
- The block existence check will find the files already present.
- New `FileVersionBlock` rows will reference the same `BlockStorageKey`, but no new physical block files are written.
- The `DedupedBlocks` count in `StoredFileContentDto` reflects how many blocks were skipped.

Example: a 512 KB file that has not changed since the previous snapshot will produce 8 blocks (at 64 KB each), all 8 counted as deduped, zero new bytes written to disk.

---

## File-Level Deduplication

Before calling the block store for a file, `SaveSnapshotAsync` checks whether the latest existing `FileVersion` for that `FileIdentity` already matches the current file:

```csharp
var latestMatchesCurrent = hasLatest
    && !latestVersion.IsDeletionMarker
    && latestVersion.SizeBytes == current.SizeBytes
    && string.Equals(latestVersion.ContentHashSha256, currentHash,
                     StringComparison.OrdinalIgnoreCase);
```

If the match is confirmed **and** the version already has block rows (`latestHasBlocks = true`), the existing `FileVersion` is reused and linked into the new snapshot via a `SnapshotFileLink`. No new `FileVersion` or `FileVersionBlock` rows are created, and `IFileContentStore.StoreFileAsync` is not called at all.

This is the most efficient deduplication path because it avoids even reading the file from disk.

---

## Cross-Repository Isolation

Deduplication is **not performed across repositories**. Each repository has its own block store namespace. This is intentional:

- It simplifies retention and deletion: removing a repository's blocks cannot affect another repository's data.
- It avoids the need for global reference counting.
- It allows independent encryption keys per repository without cross-contamination of block storage.

Two repositories containing identical files will each store their own copy of every block.

---

## Deduplication and Retention Interaction

Retention cleanup uses a **two-phase approach** to safely remove blocks without corrupting data that other snapshots still reference.

### Phase 1 — Soft-delete metadata

When retention marks a set of snapshots for deletion, it first soft-deletes (sets `IsDeleted = true`) all associated `FileVersionBlock`, `FileVersion`, `RepositorySnapshotEntry`, and `RepositorySnapshot` rows within a single database transaction.

### Phase 2 — Resolve deletable block hashes

After soft-deleting metadata, retention calls `ResolveDeletableManagedHashesAsync` (for managed blocks). This queries for all `BlockStorageKey` values that:

1. Appear in the set of candidate (soft-deleted) blocks.
2. Do **not** appear in any non-deleted `FileVersionBlock` row outside that candidate set.

Only hashes that satisfy both conditions are placed in the "deletable" set.

### Phase 3 — Physical file deletion

Physical block files are deleted only for keys in the deletable set. Because the reference check (Phase 2) is done after the soft-delete (Phase 1), the window between checking and deleting is minimized and contained within the same retention run.

---

## Block Deletion Safety

The rule is: **a block file is physically deleted only when no non-deleted `FileVersionBlock` row references its `BlockStorageKey`**.

This is enforced by `ResolveDeletableManagedHashesAsync`:

```csharp
var activeManagedHashes = await db.Set<FileVersionBlock>()
    .IgnoreQueryFilters()
    .Where(b => !b.IsDeleted
             && candidateHashes.Contains(b.BlockStorageKey)
             && !candidateBlockIds.Contains(b.Id))
    .Select(b => b.BlockStorageKey)
    .Distinct()
    .ToListAsync(ct);
```

Any hash found in an active (non-deleted) row that is not part of the current candidate set is excluded from the deletable set. This ensures blocks shared across multiple snapshots or file versions are not removed until the last reference is gone.

---

## Block Compression

### Native blocks (Rust / Zstd)

The Rust library compresses each block using **Zstd at level 6** before writing to disk. The stored file uses the `VYB1` envelope format:

```
[magic: "VYB1" (4 bytes)]
[version: u8]
[compression_kind: u8]  // 0=none, 1=zstd
[payload: compressed bytes]
```

If the compressed output is larger than the original, the uncompressed bytes are stored instead (smart compression). The file is named `{hash}.zst` regardless of whether compression was applied.

### Managed blocks (C# / Brotli)

The C# managed path uses **Brotli (`CompressionLevel.Optimal`)** for compression. The stored file uses the `VYRBLK` envelope format:

```
[magic: "VYRBLK" (6 bytes)]
[version: u8 = 1]
[compression_kind: u8]  // 0=none, 1=brotli
[original_length: i32 LE]
[payload: compressed or raw bytes]
```

The same smart-compression rule applies: if the Brotli-compressed bytes are not smaller than the original, the raw bytes are stored with `compression_kind = 0`. The file is named `{hash}.bin`.

Both compression approaches are applied **before** encryption (when encryption is enabled, the encrypted output is what gets hashed and stored as the block key).
