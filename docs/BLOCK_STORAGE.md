# Block Storage

This document describes the physical storage layout for content blocks, the native and managed storage paths, block envelope formats, atomic write semantics, and the archive subsystem.

---

## Table of Contents

1. [Directory Layout](#directory-layout)
   - [Native blocks](#native-blocks)
   - [Managed (encrypted) blocks](#managed-encrypted-blocks)
   - [Snapshot archives](#snapshot-archives)
2. [Native Block Path (Rust)](#native-block-path-rust)
3. [Managed Block Path (C#)](#managed-block-path-c)
4. [Block Envelope Formats](#block-envelope-formats)
   - [Native: VYB1](#native-vyb1)
   - [Managed: VYRBLK](#managed-vyrblk)
5. [Atomic Write Semantics](#atomic-write-semantics)
6. [Smart Compression](#smart-compression)
7. [Block Store Root Configuration](#block-store-root-configuration)
8. [Archive Mode](#archive-mode)
9. [Restore: Hydrating Archived Blocks](#restore-hydrating-archived-blocks)
10. [Path Resolution Examples](#path-resolution-examples)

---

## Directory Layout

### Native blocks

```
{blockStoreRoot}/
  blocks/
    {hash[0:2]}/
      {hash[2:4]}/
        {hash}.zst
```

The hash is the 64-character lowercase BLAKE3 hex string produced by the Rust library. The first two hex characters form the first path component, the next two form the second, and the full hash (without truncation) forms the file name.

Example for hash `a1b2c3d4e5f6...` (64 chars):

```
{blockStoreRoot}/blocks/a1/b2/a1b2c3d4e5f6...zst
```

### Managed (encrypted) blocks

```
{blockStoreRoot}/
  managed/
    blocks/
      {storedHash[0:2]}/
        {storedHash[2:4]}/
          {storedHash}.bin
```

For managed blocks the `BlockStorageKey` in the database is stored with a `sha256-` prefix (e.g., `sha256-a1b2c3...`). The prefix is stripped when constructing the file path; only the 64-character hex portion is used for the directory segments and file name.

Example for `BlockStorageKey = "sha256-a1b2c3d4e5f6..."`:

```
{blockStoreRoot}/managed/blocks/a1/b2/a1b2c3d4e5f6....bin
```

### Snapshot archives

```
{archiveRoot}/
  repositories/
    {repositoryId}/
      {year}/
        {month}/
          {day}/
            {snapshotId}_{title}.zip
```

Archives are created by `IRepositorySnapshotArchiveService` during archive-mode retention runs. The ZIP file contains the block files for snapshots that have been moved out of the live block store.

---

## Native Block Path (Rust)

The native path is active when:

- The `veyra_core` native library is loaded and the `veyra_store_file_blocks_utf8` and `veyra_restore_file_blocks_utf8` entrypoints are present.
- Artifact encryption is **not** enabled (encryption forces the managed path).

Storage call:

```csharp
var json = VeyraCoreNative.StoreFileBlocksJson(filePath, _storeRoot, DefaultChunkSize);
```

This calls the Rust P/Invoke entrypoint:

```c
int veyra_store_file_blocks_utf8(
    const char* filePath,
    const char* storeRoot,
    uint32_t    chunkSize,
    uint8_t*    output,
    uint64_t    outputLen,
    uint64_t*   written);
```

The function returns a JSON payload describing the stored blocks:

```json
{
  "file_size_bytes": 131072,
  "stored_size_bytes": 98304,
  "block_count": 2,
  "deduped_blocks": 0,
  "new_blocks": 2,
  "blocks": [
    { "sequence": 0, "block_storage_key": "a1b2...", "length_bytes": 65536, "stored_size_bytes": 49152 },
    { "sequence": 1, "block_storage_key": "c3d4...", "length_bytes": 65536, "stored_size_bytes": 49152 }
  ]
}
```

Restore call:

```csharp
long written = VeyraCoreNative.RestoreFileBlocks(_storeRoot, blocksJson, targetPath, overwriteExisting);
```

Corresponding entrypoint:

```c
int64_t veyra_restore_file_blocks_utf8(
    const char* storeRoot,
    const char* blocksJson,
    const char* targetPath,
    int32_t     overwriteExisting);
```

If a native entrypoint throws `EntryPointNotFoundException`, `DllNotFoundException`, or `BadImageFormatException`, `RustFileContentStore` disables the native path (thread-safe flag flip) and falls back to the managed C# implementation for that and all subsequent operations.

---

## Managed Block Path (C#)

The managed path is active when:

- The native library is unavailable.
- Artifact encryption is enabled (always forces managed).

`StoreFileManagedAsync` reads the file in 64 KB chunks, computes SHA-256 of the stored payload bytes, derives the block path, and writes with `FileMode.CreateNew`. If the file already exists at that path, the write fails with `IOException`, which is treated as a dedup hit.

`RestoreFileManagedAsync` reads each block file by key, decodes the `VYRBLK` envelope (decompresses and decrypts if needed), and streams the plaintext bytes to the target file in sequence order.

---

## Block Envelope Formats

### Native: VYB1

The Rust library writes this envelope around each compressed block:

```
Offset  Size  Field
------  ----  -----
0       4     Magic bytes: "VYB1" (ASCII)
4       1     Version (u8)
5       1     Compression kind: 0=none, 1=zstd
6       N     Payload bytes (compressed or raw)
```

The BLAKE3 hash in the file name is the hash of the **original plaintext block**, not of the envelope.

### Managed: VYRBLK

The C# managed path writes this envelope:

```
Offset  Size  Field
------  ----  -----
0       6     Magic bytes: "VYRBLK" (ASCII)
6       1     Version (u8 = 1)
7       1     Compression kind: 0=none, 1=brotli
8       4     Original (uncompressed) length (i32 little-endian)
12      N     Payload bytes (compressed or raw)
```

The SHA-256 in the `BlockStorageKey` (and in the file name) is the hash of the **entire envelope bytes after optional encryption**. The original length field allows pre-allocating the decompression buffer.

If encryption is enabled, the `ArtifactBlockCryptor.Protect` call wraps the entire envelope before the hash is computed:

1. Build `VYRBLK` envelope around (optionally Brotli-compressed) plaintext.
2. Call `ArtifactBlockCryptor.Protect(envelopeBytes, plaintextHash)` → returns encrypted bytes.
3. Compute SHA-256 of the encrypted bytes → this becomes the stored hash.
4. Write encrypted bytes to `{hash}.bin`.

On restore, steps are reversed: read `.bin`, decrypt, decode `VYRBLK` envelope, decompress.

---

## Atomic Write Semantics

### Managed path

The managed path uses `FileMode.CreateNew` when opening the output stream:

```csharp
await using var outStream = new FileStream(
    blockPath,
    FileMode.CreateNew,   // fails if file already exists
    FileAccess.Write,
    FileShare.None,
    bufferSize: DefaultChunkSize,
    useAsync: true);
```

`FileMode.CreateNew` is atomic on NTFS and most Unix file systems in the sense that only one opener will succeed. If two processes race to create the same block, the second receives `IOException` and counts the block as deduped. No partial file is left on disk because the stream is never flushed before the exception.

### Native path

The Rust library uses a temp-file-then-atomic-rename pattern: the block is written to a temporary file in the same directory, then renamed to the final hash-named path. This prevents partial block files from being visible to other readers and makes the write effectively atomic.

---

## Smart Compression

Both the native and managed paths try compression first and fall back to storing raw bytes if compression is not beneficial.

**Managed path logic:**

```csharp
var compressed = TryCompressManagedPayload(rawBytes);  // Brotli
if (compressed is not null && compressed.Length > 0 && compressed.Length < rawBytes.Length)
{
    payloadBytes = compressed;
    compressionKind = ManagedCompressionBrotli;
}
// else: store raw bytes with compression_kind = 0
```

**Native path:** the Rust library applies the same check for Zstd. The file extension `.zst` is used regardless of whether the content is actually compressed.

---

## Block Store Root Configuration

The block store root is resolved at startup by `ResolveStoreRoot()`. The resolution order is:

1. `Storage:BlockStorePath` in `appsettings.json` (or any bound `IConfiguration` source).
2. `VEYRA_BLOCK_STORE` environment variable.
3. Default: `%LOCALAPPDATA%\VeyraFlow\block-store` (Windows) or the platform equivalent of `LocalApplicationData`.

```csharp
private static string ResolveStoreRoot(IConfiguration cfg)
{
    var fromCfg = cfg["Storage:BlockStorePath"];
    var fromEnv = Environment.GetEnvironmentVariable("VEYRA_BLOCK_STORE");

    var root = !string.IsNullOrWhiteSpace(fromCfg) ? fromCfg.Trim()
             : !string.IsNullOrWhiteSpace(fromEnv)  ? fromEnv.Trim()
             : Path.Combine(
                 Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                 "VeyraFlow", "block-store");

    Directory.CreateDirectory(root);
    return Path.GetFullPath(root);
}
```

The same logic is used in both `RustFileContentStore` and `EfRepositoryRetentionService`.

---

## Archive Mode

When retention is configured with `StorageMode = "archive"`, the retention service does not delete snapshot metadata. Instead:

1. `IRepositorySnapshotArchiveService.EnsureSnapshotsArchivedAsync` packs the blocks for the selected snapshots into ZIP archives under the archive layout.
2. After archiving, `PruneArchivedOnlyLocalBlocksAsync` removes local block files that now exist exclusively in archives (not referenced by any retained live snapshot).
3. Archived snapshots have `IsArchived = true` set on their `RepositorySnapshot` row.

Archived snapshots are **protected from future retention delete runs**: `IsSnapshotProtectedFromRetention` returns `true` when `IsArchived == true`.

---

## Restore: Hydrating Archived Blocks

Before restoring a file version, `RustFileContentStore.RestoreFileAsync` calls `EnsureArchivedBlocksAvailableAsync`:

```csharp
await _snapshotArchive.EnsureArchivedBlocksAvailableAsync(blockStorageKeys, ct);
```

This method checks whether any of the required block storage keys are present only in archive ZIPs (not in the live block store). If so, it extracts those blocks back into the live block store before proceeding with the restore. This makes archive-mode retention transparent to the restore caller: the caller simply requests a restore and blocks are hydrated automatically if needed.

---

## Path Resolution Examples

Given `blockStoreRoot = C:\Users\user\AppData\Local\VeyraFlow\block-store`:

### Native block

| Hash | File path |
|---|---|
| `a1b2c3d4...` (64 chars) | `block-store\blocks\a1\b2\a1b2c3d4....zst` |

### Managed block

| BlockStorageKey | File path |
|---|---|
| `sha256-a1b2c3d4...` | `block-store\managed\blocks\a1\b2\a1b2c3d4....bin` |

### Short hashes (edge case)

If a hash is shorter than 4 characters (should not happen in practice), the code pads the directory segments with `"00"`:

```csharp
var p1 = normalized.Length >= 2 ? normalized[..2] : "00";
var p2 = normalized.Length >= 4 ? normalized[2..4] : "00";
```
