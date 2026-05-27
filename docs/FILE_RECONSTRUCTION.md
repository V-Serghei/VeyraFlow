# File Reconstruction in VeyraFlow

## Overview

All file types — images, documents, archives, audio, video, code — are reconstructed by the same byte-stream pipeline. There is no format-specific logic in the restore path. The process is: load blocks ordered by sequence, decompress each block, write the exact original byte count.

Reconstruction is implemented in `RustFileContentStore` (`src/Veyra.Infrastructure.Native/Storage/RustFileContentStore.cs`) which implements `IFileContentStore`.

---

## Block Ordering

Blocks are stored with a 0-based integer `Sequence` field (`FileVersionBlock.Sequence`). Reconstruction always sorts ascending by `Sequence` before processing.

- Native path: the block list serialized to JSON is sorted `OrderBy(b => b.Sequence)` before being passed to the Rust entry point.
- Managed path: `blocks.OrderBy(b => b.Sequence)` is enumerated inside `RestoreFileManagedAsync`.

The `Sequence` field in the database is populated at store time in the order the chunks were read from the original file.

---

## Reconstruction Flow

```
Load FileVersionBlocks ordered by Sequence
            |
            v
  (Empty file?) --> Create zero-byte file, done
            |
            v
  EnsureArchivedBlocksAvailableAsync
  (extracts blocks from ZIP archive if needed)
            |
            v
  ---- Native path ----------------
  |  Serialize block list to JSON  |
  |  Call veyra_restore_file_blocks_utf8 |
  |  Rust: read each .zst block,   |
  |  decompress (zstd), write      |
  |  exact LengthBytes, rename     |
  |  temp file to targetPath       |
  ---------------------------------
            |
            v (or managed fallback)
  ---- Managed path ---------------
  |  For each block in sequence:   |
  |  1. Locate .bin or .zst file   |
  |  2. Decrypt (ArtifactBlockCryptor.Unprotect) |
  |  3. Decode VYRBLK envelope     |
  |     (Brotli decompress if set) |
  |  4. Write block.LengthBytes to |
  |     output stream              |
  ---------------------------------
            |
            v
  (expectedContentHash provided?)
  --> Compute SHA-256 of written file
  --> Compare to expectedContentHash
  --> Throw if mismatch
```

---

## Native Path

Entry point: `veyra_restore_file_blocks_utf8` (P/Invoke in `VeyraCoreNative.cs`).

```csharp
// Signature
private static extern long veyra_restore_file_blocks_utf8(
    string storeRoot,
    string blocksJson,      // JSON array of { blockStorageKey, lengthBytes }
    string targetPath,
    int overwriteExisting);
```

The Rust implementation reads each `.zst` block from the store, decompresses it with zstd, and writes exactly `lengthBytes` to a temporary file. The temporary file is then atomically renamed to `targetPath`. The return value is the total number of bytes written; a negative return indicates failure (message retrievable via `veyra_last_error_utf8`).

**The native path is disabled automatically when artifact encryption is enabled**, because the Rust library does not handle the managed encryption envelope format. The fallback to managed path is transparent to callers.

---

## Managed Path (C# Fallback)

Used when:
- The native library is not loaded or missing required entry points.
- Artifact encryption is enabled (`ArtifactBlockCryptor.IsEncryptionEnabled`).
- A runtime call to the native library fails with `EntryPointNotFoundException`, `DllNotFoundException`, or `BadImageFormatException`.

For each block in sequence order:

### Managed blocks (`sha256-` prefix)

1. Resolve `.bin` file path: `<store-root>/managed/blocks/<h[0:2]>/<h[2:4]>/<h>.bin`.
2. Read all bytes from the file.
3. Pass to `ArtifactBlockCryptor.Unprotect` (decrypts AES-GCM if encryption is active; otherwise a no-op).
4. Pass to `DecodeManagedEncryptedPayload`:
   - Verify `VYRBLK` magic prefix.
   - Read version byte (must be `1`).
   - Read compression kind (`0` = none, `1` = Brotli).
   - Read `int32-LE` original length.
   - Decompress if Brotli.
5. Write `block.LengthBytes` bytes to the output stream.

### Native blocks (64-char hex BLAKE3 hash)

1. Resolve `.zst` file path: `<store-root>/blocks/<h[0:2]>/<h[2:4]>/<h>.zst`.
2. Read all bytes.
3. Call `VeyraCoreNative.ZstdDecompress(compressedBytes, block.LengthBytes)`.
4. Write `block.LengthBytes` bytes to the output stream.

---

## Block-Level Integrity Checks

| Check | Condition | Exception thrown |
|-------|-----------|-----------------|
| Block file exists | File must be present before decompression | `FileNotFoundException` |
| Decompressed length | `plaintextBytes.Length >= block.LengthBytes` | `InvalidOperationException` |
| Managed hash verification | SHA-256 of stored `.bin` file compared to hash in `BlockStorageKey` | Detected during integrity check, not restore |
| Post-restore file hash | SHA-256 of full restored file vs `expectedContentHash` | `InvalidOperationException` |

When a block file is not found, restore fails immediately. No partial output file is left because the output `FileStream` is opened with `FileMode.Create` at the start and is only complete if all blocks write successfully. If an exception occurs mid-restore, the partial file remains on disk but is not moved to the final path.

For the native path, the Rust implementation writes to a temporary file and renames it atomically, so a failure leaves no partially-written file at the target path.

---

## Empty File Restore

If `blocks.Count == 0`, `CreateEmptyFileAsync` is called directly, which creates a zero-byte file at `targetPath` without reading any blocks. The `EnsureArchivedBlocksAvailableAsync` and hash validation steps are skipped.

---

## Archive Hydration

Before any block is read, `EnsureArchivedBlocksAvailableAsync` is called with the list of all `BlockStorageKey` values. This allows `IRepositorySnapshotArchiveService` to extract blocks from a ZIP archive if the snapshot has been archived (cold storage). If hydration fails, a warning is logged and restore continues — the missing block will produce a `FileNotFoundException` if it truly does not exist.

---

## Post-Restore File Hash Validation

After full reconstruction (both native and managed paths), if `expectedContentHash` is non-empty, `ValidateRestoredFileHashAsync` is called:

```csharp
// Computes SHA-256 of the fully written output file and compares to expectedContentHash.
// Throws InvalidOperationException on mismatch, with the target path, expected hash, and actual hash in the message.
```

This is applied when `expectedContentHash` is passed to `RestoreFileAsync`. Callers that supply the `FileVersion.ContentHashSha256` value get full end-to-end integrity guarantees.

---

## Cloud Repair

`EfRepositoryIntegrityService` (`src/Veyra.Infrastructure.Data/Setup/Integrity/EfRepositoryIntegrityService.cs`) can repair missing or corrupted blocks by downloading them from the cloud API endpoint `/api/sync/blocks/{hash}`. The repair flow:

1. Resolve the local block path.
2. If the file is missing or its SHA-256 does not match the hash in the `BlockStorageKey`, attempt `TryRepairBlockFromCloudAsync`.
3. Download to a `.repair` temp file.
4. Verify the downloaded file's hash before moving it to the final path.
5. Report the block as `Repaired` or `Missing`/`Corrupted` depending on outcome.

Repair concurrency is controlled by `RepairProbeMaxConcurrency = 8` when probing cloud and sequential per-block downloads when writing.

---

## File Type Unification

The pipeline makes no distinctions between file types. Documents, images, executables, archives, audio, and video all pass through `RustFileContentStore.RestoreFileAsync` via the same code path. The only file-type-aware behavior in the system is:

- `KnownFileExtensions.ImageDiffFormats` — triggers visual diff rendering (separate from storage).
- `KnownFileExtensions.TextDiffFormats` — triggers text diff rendering (separate from storage).

Neither of these affects block storage or reconstruction.
