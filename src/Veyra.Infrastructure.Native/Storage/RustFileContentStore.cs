using System.Security.Cryptography;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.DTOs;
using Veyra.Infrastructure.Native.Execution;
using Veyra.Infrastructure.Native.Interop;
using Veyra.Infrastructure.Native.Runtime;
using Veyra.Infrastructure.Native.Security;

namespace Veyra.Infrastructure.Native.Storage;

internal sealed class RustFileContentStore : IFileContentStore
{
    private const int DefaultChunkSize = 64 * 1024;
    private const string ManagedHashPrefix = "sha256-";
    private const string ManagedPayloadMagic = "VYRBLK";
    private const byte ManagedPayloadVersion = 1;
    private const byte ManagedCompressionNone = 0;
    private const byte ManagedCompressionBrotli = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<RustFileContentStore> _log;
    private readonly string _storeRoot;
    private readonly object _nativeCapabilityLock = new();
    private readonly INativeExecutionScheduler _scheduler;
    private readonly IRepositorySnapshotArchiveService _snapshotArchive;

    private bool _nativeStoreAvailable;
    private bool _nativeRestoreAvailable;
    private readonly ArtifactBlockCryptor _artifactCryptor;

    public RustFileContentStore(
        IConfiguration configuration,
        ILogger<RustFileContentStore> log,
        INativeExecutionScheduler scheduler,
        IRepositorySnapshotArchiveService snapshotArchive,
        ArtifactBlockCryptor artifactCryptor)
    {
        _log = log;
        _scheduler = scheduler;
        _snapshotArchive = snapshotArchive;
        _storeRoot = ResolveStoreRoot(configuration);
        _artifactCryptor = artifactCryptor;

        var native = NativeRuntimeHealth.Probe();
        _nativeStoreAvailable = native.SupportsStoreFileBlocks;
        _nativeRestoreAvailable = native.SupportsRestoreFileBlocks;

        if (_artifactCryptor.IsEncryptionEnabled)
        {
            _nativeStoreAvailable = false;
            _nativeRestoreAvailable = false;
            _log.LogInformation(
                "Artifact encryption is enabled. Managed block-store path is active to guarantee encrypted payload storage. Native support detected: store {StoreAvailable}, restore {RestoreAvailable}. Library {LoadedPath}",
                native.SupportsStoreFileBlocks,
                native.SupportsRestoreFileBlocks,
                native.LoadedPath ?? "(not loaded)");
            return;
        }

        if (_nativeStoreAvailable && _nativeRestoreAvailable)
        {
            _log.LogInformation(
                "Native block-store entrypoints detected. Library {LoadedPath}",
                native.LoadedPath ?? "(unknown)");
            return;
        }

        _log.LogWarning(
            "Native block-store entrypoints are unavailable at startup. Store {StoreAvailable}. Restore {RestoreAvailable}. Library {LoadedPath}. Error {Error}",
            _nativeStoreAvailable,
            _nativeRestoreAvailable,
            native.LoadedPath ?? "(not loaded)",
            native.ErrorMessage ?? "(no error details)");
    }

    public async Task<StoredFileContentDto> StoreFileAsync(string filePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path is required.", nameof(filePath));

        var fullPath = Path.GetFullPath(filePath);

        if (!_nativeStoreAvailable)
            return await StoreFileManagedAsync(fullPath, ct);

        try
        {
            var result = await _scheduler.RunAsync(() =>
            {
                var json = VeyraCoreNative.StoreFileBlocksJson(fullPath, _storeRoot, DefaultChunkSize);

                var payload = JsonSerializer.Deserialize<StorePayload>(json, JsonOptions)
                              ?? throw new InvalidOperationException("Native block-store returned empty payload.");

                var blocks = payload.Blocks
                    .OrderBy(b => b.Sequence)
                    .Select(b => new StoredFileBlockDto(
                        b.Sequence,
                        b.BlockStorageKey,
                        b.LengthBytes,
                        b.StoredSizeBytes))
                    .ToList();

                return new StoredFileContentDto(
                    payload.FileSizeBytes,
                    payload.StoredSizeBytes,
                    payload.BlockCount,
                    payload.DedupedBlocks,
                    payload.NewBlocks,
                    blocks);
            }, ct);

            _log.LogInformation(
                "Stored file in native block-store. Path {Path}. Blocks {Blocks}. Deduped {Deduped}. New {New}",
                fullPath,
                result.BlockCount,
                result.DedupedBlocks,
                result.NewBlocks);
            NativeFeatureUsageTracker.MarkNativeHit(NativeFeatureUsageTracker.StoreBlocks);

            return result;
        }
        catch (Exception ex) when (IsNativeBlocksUnavailable(ex))
        {
            NativeFeatureUsageTracker.MarkManagedFallback(NativeFeatureUsageTracker.StoreBlocks);
            DisableNativeStore(ex, fullPath);
            return await StoreFileManagedAsync(fullPath, ct);
        }
    }

    public async Task<long> RestoreFileAsync(
        IReadOnlyList<StoredFileBlockDto> blocks,
        string targetPath,
        bool overwriteExisting,
        string? expectedContentHash = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("Target path is required.", nameof(targetPath));

        var fullTarget = Path.GetFullPath(targetPath);
        if (blocks.Count == 0)
            return await CreateEmptyFileAsync(fullTarget, overwriteExisting, ct);

        await EnsureArchivedBlocksAvailableAsync(blocks, ct);

        long written;
        if (!_nativeRestoreAvailable)
        {
            written = await RestoreFileManagedAsync(blocks, fullTarget, overwriteExisting, ct);
        }
        else
        {
            try
            {
                written = await _scheduler.RunAsync(() =>
                {
                    var payload = blocks
                        .OrderBy(b => b.Sequence)
                        .Select(b => new RestoreBlockPayload
                        {
                            BlockStorageKey = b.BlockStorageKey,
                            LengthBytes = b.LengthBytes
                        })
                        .ToList();

                    var json = JsonSerializer.Serialize(payload);
                    return VeyraCoreNative.RestoreFileBlocks(_storeRoot, json, fullTarget, overwriteExisting);
                }, ct);

                _log.LogInformation(
                    "Restored file from native block-store. Target {Target}. Bytes {Bytes}. Blocks {Blocks}",
                    fullTarget,
                    written,
                    blocks.Count);
                NativeFeatureUsageTracker.MarkNativeHit(NativeFeatureUsageTracker.RestoreBlocks);
            }
            catch (Exception ex) when (IsNativeBlocksUnavailable(ex))
            {
                NativeFeatureUsageTracker.MarkManagedFallback(NativeFeatureUsageTracker.RestoreBlocks);
                DisableNativeRestore(ex, fullTarget);
                written = await RestoreFileManagedAsync(blocks, fullTarget, overwriteExisting, ct);
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedContentHash))
            await ValidateRestoredFileHashAsync(fullTarget, expectedContentHash, ct);

        return written;
    }

    public async Task<IReadOnlyList<string>> FindMissingBlocksAsync(
        IReadOnlyCollection<string> blockStorageKeys,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var keys = blockStorageKeys
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Select(static key => key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (keys.Count == 0)
            return Array.Empty<string>();

        try
        {
            await _snapshotArchive.EnsureArchivedBlocksAvailableAsync(keys, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Archived snapshot hydration check failed before block availability scan.");
        }

        var missing = new List<string>();
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();

            var path = ResolveBlockPath(key);
            if (path is null || !File.Exists(path))
                missing.Add(key);
        }

        return missing;
    }

    private async Task ValidateRestoredFileHashAsync(string fullTarget, string expectedHash, CancellationToken ct)
    {
        string actualHash;
        await using (var stream = new FileStream(
            fullTarget,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: DefaultChunkSize,
            useAsync: true))
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hashBytes = await sha.ComputeHashAsync(stream, ct);
            actualHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogError(
                "Post-restore hash mismatch. Target {Target}. Expected {Expected}. Actual {Actual}",
                fullTarget,
                expectedHash,
                actualHash);
            throw new InvalidOperationException(
                $"Restored file hash does not match the expected content hash. " +
                $"The file at '{fullTarget}' may be corrupted.");
        }

        _log.LogDebug(
            "Post-restore hash validated. Target {Target}. Hash {Hash}",
            fullTarget,
            actualHash);
    }

    private async Task EnsureArchivedBlocksAvailableAsync(
        IReadOnlyList<StoredFileBlockDto> blocks,
        CancellationToken ct)
    {
        var blockStorageKeys = blocks
            .Select(static block => block.BlockStorageKey)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (blockStorageKeys.Count == 0)
            return;

        try
        {
            await _snapshotArchive.EnsureArchivedBlocksAvailableAsync(blockStorageKeys, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Archived snapshot hydration check failed before restore.");
        }
    }

    private async Task<long> CreateEmptyFileAsync(string fullTarget, bool overwriteExisting, CancellationToken ct)
    {
        if (File.Exists(fullTarget) && !overwriteExisting)
            throw new IOException($"Target file already exists {fullTarget}");

        var dir = Path.GetDirectoryName(fullTarget);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        await using var stream = new FileStream(
            fullTarget,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1,
            useAsync: true);

        await stream.FlushAsync(ct);

        _log.LogInformation(
            "Restored empty file version. Target {Target}. Bytes 0. Blocks 0",
            fullTarget);

        return 0;
    }

    private async Task<StoredFileContentDto> StoreFileManagedAsync(string fullPath, CancellationToken ct)
    {
        var blocks = new List<StoredFileBlockDto>();

        var fileSizeBytes = 0L;
        var storedSizeBytes = 0L;
        var dedupedBlocks = 0;
        var newBlocks = 0;
        var sequence = 0;

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: DefaultChunkSize,
            useAsync: true);

        var buffer = new byte[DefaultChunkSize];

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0)
                break;

            fileSizeBytes += read;

            var plaintextHash = ComputeSha256(buffer.AsSpan(0, read));
            var bytesToStore = _artifactCryptor.Protect(
                BuildManagedEncryptedPayload(buffer.AsSpan(0, read)),
                plaintextHash);
            var storedHash = ComputeSha256(bytesToStore);
            var blockHash = ManagedHashPrefix + storedHash;
            var blockPath = GetManagedBlockPath(storedHash);

            var created = false;
            if (!File.Exists(blockPath))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(blockPath)!);

                    await using var outStream = new FileStream(
                        blockPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: DefaultChunkSize,
                        useAsync: true);

                    await outStream.WriteAsync(bytesToStore.AsMemory(0, bytesToStore.Length), ct);
                    created = true;
                    newBlocks++;
                    storedSizeBytes += bytesToStore.Length;
                }
                catch (IOException)
                {
                    dedupedBlocks++;
                }
            }
            else
            {
                dedupedBlocks++;
            }

            blocks.Add(new StoredFileBlockDto(
                sequence,
                blockHash,
                read,
                created ? bytesToStore.Length : 0));

            sequence++;
        }

        _log.LogInformation(
            "Stored file in managed block-store. Path {Path}. Blocks {Blocks}. Deduped {Deduped}. New {New}",
            fullPath,
            blocks.Count,
            dedupedBlocks,
            newBlocks);

        return new StoredFileContentDto(
            fileSizeBytes,
            storedSizeBytes,
            blocks.Count,
            dedupedBlocks,
            newBlocks,
            blocks);
    }

    private async Task<long> RestoreFileManagedAsync(
        IReadOnlyList<StoredFileBlockDto> blocks,
        string fullTarget,
        bool overwriteExisting,
        CancellationToken ct)
    {
        if (File.Exists(fullTarget) && !overwriteExisting)
            throw new IOException($"Target file already exists {fullTarget}");

        var dir = Path.GetDirectoryName(fullTarget);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        await using var outStream = new FileStream(
            fullTarget,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: DefaultChunkSize,
            useAsync: true);

        var totalWritten = 0L;

        foreach (var block in blocks.OrderBy(b => b.Sequence))
        {
            byte[] plaintextBytes;

            if (block.BlockStorageKey.StartsWith(ManagedHashPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var hash = block.BlockStorageKey[ManagedHashPrefix.Length..];
                var blockPath = GetManagedBlockPath(hash);

                if (!File.Exists(blockPath))
                    throw new FileNotFoundException("Block file not found for restore.", blockPath);

                var storedBytes = await File.ReadAllBytesAsync(blockPath, ct);
                plaintextBytes = DecodeManagedEncryptedPayload(_artifactCryptor.Unprotect(storedBytes));

                if (plaintextBytes.Length < block.LengthBytes)
                    throw new InvalidOperationException(
                        $"Block {block.BlockStorageKey} is shorter than expected after decryption.");
            }
            else if (IsNativeBlockHash(block.BlockStorageKey))
            {
                var nativeCompressedPath = GetNativeBlockPath(block.BlockStorageKey);
                if (!File.Exists(nativeCompressedPath))
                    throw new FileNotFoundException("Native block file not found for restore.", nativeCompressedPath);

                var compressedBytes = await File.ReadAllBytesAsync(nativeCompressedPath, ct);

                try
                {
                    plaintextBytes = VeyraCoreNative.ZstdDecompress(compressedBytes, block.LengthBytes);
                }
                catch (EntryPointNotFoundException)
                {
                    throw new InvalidOperationException(
                        $"Managed fallback cannot restore native block hash {block.BlockStorageKey}. Rebuild native veyra_core with block entrypoints.");
                }

                if (plaintextBytes.Length < block.LengthBytes)
                    throw new InvalidOperationException(
                        $"Native block {block.BlockStorageKey} is shorter than expected after decompression.");
            }
            else
            {
                throw new InvalidOperationException(
                    $"Unsupported block hash format {block.BlockStorageKey}.");
            }

            await outStream.WriteAsync(plaintextBytes.AsMemory(0, block.LengthBytes), ct);
            totalWritten += block.LengthBytes;
        }

        _log.LogInformation(
            "Restored file from managed block-store. Target {Target}. Bytes {Bytes}. Blocks {Blocks}",
            fullTarget,
            totalWritten,
            blocks.Count);

        return totalWritten;
    }

    private void DisableNativeStore(Exception ex, string fullPath)
    {
        var switched = false;

        lock (_nativeCapabilityLock)
        {
            if (_nativeStoreAvailable)
            {
                _nativeStoreAvailable = false;
                switched = true;
            }
        }

        if (switched)
        {
            _log.LogWarning(
                ex,
                "Native block-store entrypoints became unavailable. Switching store operation to managed mode. Path {Path}",
                fullPath);
        }
        else
        {
            _log.LogDebug(
                ex,
                "Native block-store entrypoint call failed while already in managed store mode. Path {Path}",
                fullPath);
        }
    }

    private void DisableNativeRestore(Exception ex, string fullTarget)
    {
        var switched = false;

        lock (_nativeCapabilityLock)
        {
            if (_nativeRestoreAvailable)
            {
                _nativeRestoreAvailable = false;
                switched = true;
            }
        }

        if (switched)
        {
            _log.LogWarning(
                ex,
                "Native block restore entrypoints became unavailable. Switching restore operation to managed mode. Target {Target}",
                fullTarget);
        }
        else
        {
            _log.LogDebug(
                ex,
                "Native block restore entrypoint call failed while already in managed restore mode. Target {Target}",
                fullTarget);
        }
    }

    private static bool IsNativeBlocksUnavailable(Exception ex)
    {
        if (ex is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException)
            return true;

        if (ex is InvalidOperationException ioe)
        {
            return ioe.Message.Contains("entry point", StringComparison.OrdinalIgnoreCase)
                   || ioe.Message.Contains("Native block", StringComparison.OrdinalIgnoreCase)
                   || ioe.Message.Contains("Unable to load DLL", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string ComputeSha256(ReadOnlySpan<byte> data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static byte[] BuildManagedEncryptedPayload(ReadOnlySpan<byte> plaintext)
    {
        var rawBytes = plaintext.ToArray();
        var payloadBytes = rawBytes;
        var compressionKind = ManagedCompressionNone;

        var compressed = TryCompressManagedPayload(rawBytes);
        if (compressed is not null && compressed.Length > 0 && compressed.Length < rawBytes.Length)
        {
            payloadBytes = compressed;
            compressionKind = ManagedCompressionBrotli;
        }

        var magicBytes = System.Text.Encoding.ASCII.GetBytes(ManagedPayloadMagic);
        var envelope = new byte[magicBytes.Length + 1 + 1 + sizeof(int) + payloadBytes.Length];
        var offset = 0;

        magicBytes.CopyTo(envelope, offset);
        offset += magicBytes.Length;

        envelope[offset++] = ManagedPayloadVersion;
        envelope[offset++] = compressionKind;
        BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(offset, sizeof(int)), rawBytes.Length);
        offset += sizeof(int);

        payloadBytes.CopyTo(envelope.AsSpan(offset, payloadBytes.Length));
        return envelope;
    }

    private static byte[] DecodeManagedEncryptedPayload(ReadOnlySpan<byte> storedPayload)
    {
        if (!LooksLikeManagedEncryptedPayload(storedPayload))
            return storedPayload.ToArray();

        var magicBytes = System.Text.Encoding.ASCII.GetBytes(ManagedPayloadMagic);
        var offset = magicBytes.Length;

        var version = storedPayload[offset++];
        if (version != ManagedPayloadVersion)
            return storedPayload.ToArray();

        var compressionKind = storedPayload[offset++];
        var expectedLength = BinaryPrimitives.ReadInt32LittleEndian(storedPayload.Slice(offset, sizeof(int)));
        offset += sizeof(int);

        var payload = storedPayload[offset..].ToArray();
        return compressionKind switch
        {
            ManagedCompressionNone => payload,
            ManagedCompressionBrotli => DecompressManagedPayload(payload, expectedLength),
            _ => throw new InvalidOperationException($"Unsupported managed block compression kind {compressionKind}.")
        };
    }

    private static bool LooksLikeManagedEncryptedPayload(ReadOnlySpan<byte> payload)
    {
        var magicBytes = System.Text.Encoding.ASCII.GetBytes(ManagedPayloadMagic);
        if (payload.Length < magicBytes.Length + 1 + 1 + sizeof(int))
            return false;

        return payload[..magicBytes.Length].SequenceEqual(magicBytes);
    }

    private static byte[]? TryCompressManagedPayload(byte[] rawBytes)
    {
        try
        {
            using var output = new MemoryStream();
            using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                brotli.Write(rawBytes, 0, rawBytes.Length);
            }

            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static byte[] DecompressManagedPayload(byte[] compressedBytes, int expectedLength)
    {
        using var input = new MemoryStream(compressedBytes, writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: false);
        using var output = expectedLength > 0
            ? new MemoryStream(expectedLength)
            : new MemoryStream();

        brotli.CopyTo(output);
        return output.ToArray();
    }

    private string GetManagedBlockPath(string hash)
    {
        var normalized = hash.Trim().ToLowerInvariant();

        var p1 = normalized.Length >= 2 ? normalized[..2] : "00";
        var p2 = normalized.Length >= 4 ? normalized[2..4] : "00";

        return Path.Combine(_storeRoot, "managed", "blocks", p1, p2, $"{normalized}.bin");
    }

    private string GetNativeBlockPath(string hash)
    {
        var normalized = hash.Trim().ToLowerInvariant();

        var p1 = normalized.Length >= 2 ? normalized[..2] : "00";
        var p2 = normalized.Length >= 4 ? normalized[2..4] : "00";

        return Path.Combine(_storeRoot, "blocks", p1, p2, $"{normalized}.zst");
    }

    private string? ResolveBlockPath(string key)
    {
        if (key.StartsWith(ManagedHashPrefix, StringComparison.OrdinalIgnoreCase))
            return GetManagedBlockPath(key[ManagedHashPrefix.Length..]);

        return IsNativeBlockHash(key)
            ? GetNativeBlockPath(key)
            : null;
    }

    private static bool IsNativeBlockHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash) || hash.Length != 64)
            return false;

        foreach (var ch in hash)
        {
            var isHex = (ch >= '0' && ch <= '9')
                        || (ch >= 'a' && ch <= 'f')
                        || (ch >= 'A' && ch <= 'F');

            if (!isHex)
                return false;
        }

        return true;
    }

    private static string ResolveStoreRoot(IConfiguration cfg)
    {
        var fromCfg = cfg["Storage:BlockStorePath"];
        var fromEnv = Environment.GetEnvironmentVariable("VEYRA_BLOCK_STORE");

        var root = !string.IsNullOrWhiteSpace(fromCfg)
            ? fromCfg.Trim()
            : !string.IsNullOrWhiteSpace(fromEnv)
                ? fromEnv.Trim()
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VeyraFlow",
                    "block-store");

        Directory.CreateDirectory(root);
        return Path.GetFullPath(root);
    }
}
