# Image Storage in VeyraFlow

## Overview

Images are stored in VeyraFlow using the same pipeline as every other tracked file type. There is no image-specific encoding, decoding, or codec processing at any point during snapshot creation or file restoration. The storage engine treats image data as an opaque byte stream.

---

## Tracked Image Formats

Defined in `KnownFileExtensions.TrackedImageFormats` (`src/Veyra.Application/Common/Files/KnownFileExtensions.cs`):

| Extension | Notes |
|-----------|-------|
| `.png`    |       |
| `.jpg`    |       |
| `.jpeg`   |       |
| `.gif`    |       |
| `.bmp`    |       |
| `.webp`   |       |
| `.tif`    |       |
| `.tiff`   |       |
| `.svg`    | Also included in `TextContentFormats` for preview purposes |

---

## Storage Pipeline

```
Image file on disk
       |
       v
  Read as raw bytes (64 KB chunks)
       |
       v
  BLAKE3 hash per chunk  (native path)
  SHA256 hash per chunk  (managed path)
       |
       v
  zstd compression → .zst file  (native path)
  Brotli compression → .bin file (managed path)
       |
       v
  Block written to block-store directory
  (only if the hash does not already exist — deduplication)
```

### Chunk size

The block size is fixed at **64 KB** (`DefaultChunkSize = 64 * 1024` in `RustFileContentStore`). Every chunk is processed and stored independently.

### Native path

When the Rust native library (`veyra_core`) is available and artifact encryption is disabled, the P/Invoke entry point `veyra_store_file_blocks_utf8` is called. The Rust implementation performs BLAKE3 hashing and zstd compression. Blocks are written as `.zst` files under:

```
<block-store-root>/blocks/<hash[0:2]>/<hash[2:4]>/<hash>.zst
```

### Managed path (C# fallback)

Used when the native library is unavailable or when artifact encryption is enabled. Each chunk is:

1. Optionally compressed with Brotli.
2. Wrapped in a `VYRBLK` envelope (magic bytes + version byte + compression kind + original length as `int32-LE` + payload).
3. Optionally encrypted via `ArtifactBlockCryptor.Protect`.
4. Written as a `.bin` file keyed by SHA-256 of the stored (post-encryption) bytes under:

```
<block-store-root>/managed/blocks/<hash[0:2]>/<hash[2:4]>/<hash>.bin
```

The block storage key for managed blocks uses the `sha256-` prefix (e.g. `sha256-<hex>`), which distinguishes them from native BLAKE3 keys.

---

## No Codec Processing

At no point during storage or restoration is image data decoded into pixels, resampled, re-encoded, or otherwise interpreted as an image. The raw bytes that entered the pipeline are the exact bytes that leave it. This guarantees:

- **Lossless round-trips** for all formats, including lossy formats such as JPEG.
- **EXIF and ICC metadata preservation** — metadata bytes are part of the raw stream and are carried through unchanged.
- **Bit-identical restoration** — the `LengthBytes` field stored per block records the exact number of original bytes in that chunk, and restore writes precisely that many bytes to the output stream.

---

## Deduplication for Images

Deduplication operates at the block (64 KB chunk) level based on content hash:

- If two different image versions share an identical 64 KB region (same bytes at the same offset), only one copy of that block is stored.
- A one-pixel change in a large image will produce new blocks only for the 64 KB chunk(s) that contain the changed pixel. All other chunks are shared with the previous version.
- Images that are entirely identical across versions produce zero new blocks.
- Deduplication effectiveness depends entirely on byte-level similarity between versions, not on perceptual or structural image similarity.

---

## Image Diff Visualization

Image diff visualization is a separate concern from storage. When the user views a visual diff between two image versions:

- The files are decoded from disk using the native `veyra_render_image_diff_utf8` entry point (`VeyraCoreNative.cs`).
- This decoding happens only for preview/UI generation.
- The formats eligible for visual diff are defined in `KnownFileExtensions.ImageDiffFormats`:
  `.png`, `.jpg`, `.jpeg`, `.bmp`, `.gif`, `.webp`, `.tif`, `.tiff`, `.svg`.

**The storage pipeline is never involved in diff rendering. No image is ever re-stored after diff generation.**

---

## Post-Restore Validation

After a file is fully reconstructed from its blocks, an optional post-restore hash check can be applied. When the caller provides `expectedContentHash` to `IFileContentStore.RestoreFileAsync`, the implementation:

1. Reads the fully written output file.
2. Computes its SHA-256.
3. Compares it to the expected hash.
4. Throws `InvalidOperationException` if there is a mismatch.

The expected hash is the `ContentHashSha256` value stored in the `FileVersion` entity at snapshot time. For images restored from cloud, the `ContentHashSha256` field travels with the `CloudFileVersionDto` metadata and is available for this comparison.

---

## Cloud Restore

Image blocks downloaded from cloud are written to the local block-store before reconstruction begins. The reconstruction itself is identical to the local path: blocks are decompressed and written byte-for-byte. No quality degradation occurs. Block prefetch uses up to 8 concurrent downloads (`RestoreDownloadMaxConcurrency = 8`).

---

## Summary Table

| Concern | Behavior |
|---------|----------|
| Storage format | Raw bytes, chunked at 64 KB |
| Compression (native) | zstd (via Rust) |
| Compression (managed) | Brotli |
| Hash algorithm (native) | BLAKE3 |
| Hash algorithm (managed) | SHA-256 (`sha256-` prefix) |
| Image decoding during storage | Never |
| EXIF preservation | Yes — raw bytes are unchanged |
| Byte-identical restore | Yes — enforced by `LengthBytes` |
| Post-restore hash validation | SHA-256 of restored file vs `ContentHashSha256` |
| Deduplication granularity | 64 KB block |
| Diff visualization decoding | Only for UI preview, not storage |
