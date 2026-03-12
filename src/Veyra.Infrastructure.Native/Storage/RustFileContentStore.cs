using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.DTOs;
using Veyra.Infrastructure.Native.Interop;
using Veyra.Infrastructure.Native.Security;

namespace Veyra.Infrastructure.Native.Storage;

internal sealed class RustFileContentStore : IFileContentStore
{
    private const int DefaultChunkSize = 64 * 1024;
    private const string ManagedHashPrefix = "sha256-";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<RustFileContentStore> _log;
    private readonly string _storeRoot;
    private readonly object _nativeCapabilityLock = new();

    private bool _nativeStoreAvailable;
    private bool _nativeRestoreAvailable;
    private readonly ArtifactBlockCryptor _artifactCryptor;

    public RustFileContentStore(
        IConfiguration configuration,
        ILogger<RustFileContentStore> log,
        ArtifactBlockCryptor artifactCryptor)
    {
        _log = log;
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
            var json = VeyraCoreNative.StoreFileBlocksJson(fullPath, _storeRoot, DefaultChunkSize);

            var payload = JsonSerializer.Deserialize<StorePayload>(json, JsonOptions)
                          ?? throw new InvalidOperationException("Native block-store returned empty payload.");

            var blocks = payload.Blocks
                .OrderBy(b => b.Sequence)
                .Select(b => new StoredFileBlockDto(
                    b.Sequence,
                    b.BlockHashBlake3,
                    b.LengthBytes,
                    b.StoredSizeBytes))
                .ToList();

            _log.LogInformation(
                "Stored file in native block-store. Path {Path}. Blocks {Blocks}. Deduped {Deduped}. New {New}",
                fullPath,
                payload.BlockCount,
                payload.DedupedBlocks,
                payload.NewBlocks);

            return new StoredFileContentDto(
                payload.FileSizeBytes,
                payload.StoredSizeBytes,
                payload.BlockCount,
                payload.DedupedBlocks,
                payload.NewBlocks,
                blocks);
        }
        catch (Exception ex) when (IsNativeBlocksUnavailable(ex))
        {
            DisableNativeStore(ex, fullPath);
            return await StoreFileManagedAsync(fullPath, ct);
        }
    }

    public async Task<long> RestoreFileAsync(
        IReadOnlyList<StoredFileBlockDto> blocks,
        string targetPath,
        bool overwriteExisting,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("Target path is required.", nameof(targetPath));

        var fullTarget = Path.GetFullPath(targetPath);
        if (blocks.Count == 0)
            return await CreateEmptyFileAsync(fullTarget, overwriteExisting, ct);

        if (!_nativeRestoreAvailable)
            return await RestoreFileManagedAsync(blocks, fullTarget, overwriteExisting, ct);

        try
        {
            var payload = blocks
                .OrderBy(b => b.Sequence)
                .Select(b => new RestoreBlockPayload
                {
                    BlockHashBlake3 = b.BlockHashBlake3,
                    LengthBytes = b.LengthBytes
                })
                .ToList();

            var json = JsonSerializer.Serialize(payload);
            var written = VeyraCoreNative.RestoreFileBlocks(_storeRoot, json, fullTarget, overwriteExisting);

            _log.LogInformation(
                "Restored file from native block-store. Target {Target}. Bytes {Bytes}. Blocks {Blocks}",
                fullTarget,
                written,
                blocks.Count);

            return written;
        }
        catch (Exception ex) when (IsNativeBlocksUnavailable(ex))
        {
            DisableNativeRestore(ex, fullTarget);
            return await RestoreFileManagedAsync(blocks, fullTarget, overwriteExisting, ct);
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
            FileShare.Read,
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
            var bytesToStore = _artifactCryptor.Protect(buffer.AsSpan(0, read), plaintextHash);
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

            if (block.BlockHashBlake3.StartsWith(ManagedHashPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var hash = block.BlockHashBlake3[ManagedHashPrefix.Length..];
                var blockPath = GetManagedBlockPath(hash);

                if (!File.Exists(blockPath))
                    throw new FileNotFoundException("Block file not found for restore.", blockPath);

                var storedBytes = await File.ReadAllBytesAsync(blockPath, ct);
                plaintextBytes = _artifactCryptor.Unprotect(storedBytes);

                if (plaintextBytes.Length < block.LengthBytes)
                    throw new InvalidOperationException(
                        $"Block {block.BlockHashBlake3} is shorter than expected after decryption.");
            }
            else if (IsNativeBlockHash(block.BlockHashBlake3))
            {
                var nativeCompressedPath = GetNativeBlockPath(block.BlockHashBlake3);
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
                        $"Managed fallback cannot restore native block hash {block.BlockHashBlake3}. Rebuild native veyra_core with block entrypoints.");
                }

                if (plaintextBytes.Length < block.LengthBytes)
                    throw new InvalidOperationException(
                        $"Native block {block.BlockHashBlake3} is shorter than expected after decompression.");
            }
            else
            {
                throw new InvalidOperationException(
                    $"Unsupported block hash format {block.BlockHashBlake3}.");
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
    private sealed record StorePayload
    {
        [JsonPropertyName("file_size_bytes")]
        public long FileSizeBytes { get; init; }

        [JsonPropertyName("stored_size_bytes")]
        public long StoredSizeBytes { get; init; }

        [JsonPropertyName("block_count")]
        public int BlockCount { get; init; }

        [JsonPropertyName("deduped_blocks")]
        public int DedupedBlocks { get; init; }

        [JsonPropertyName("new_blocks")]
        public int NewBlocks { get; init; }

        [JsonPropertyName("blocks")]
        public List<StoreBlockPayload> Blocks { get; init; } = [];
    }

    private sealed record StoreBlockPayload
    {
        [JsonPropertyName("sequence")]
        public int Sequence { get; init; }

        [JsonPropertyName("block_hash_blake3")]
        public string BlockHashBlake3 { get; init; } = string.Empty;

        [JsonPropertyName("length_bytes")]
        public int LengthBytes { get; init; }

        [JsonPropertyName("stored_size_bytes")]
        public long StoredSizeBytes { get; init; }
    }

    private sealed class RestoreBlockPayload
    {
        [JsonPropertyName("block_hash_blake3")]
        public string BlockHashBlake3 { get; init; } = string.Empty;

        [JsonPropertyName("length_bytes")]
        public int LengthBytes { get; init; }
    }
}

