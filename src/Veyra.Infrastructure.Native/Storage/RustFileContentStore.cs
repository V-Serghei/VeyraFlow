using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.DTOs;
using Veyra.Infrastructure.Native.Interop;

namespace Veyra.Infrastructure.Native.Storage;

public sealed class RustFileContentStore : IFileContentStore
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
        ILogger<RustFileContentStore> log)
    {
        _log = log;
        _storeRoot = ResolveStoreRoot(configuration);
        _artifactCryptor = ArtifactBlockCryptor.Create(configuration, log);

        var native = NativeRuntimeHealth.Probe();
        _nativeStoreAvailable = native.SupportsStoreFileBlocks;
        _nativeRestoreAvailable = native.SupportsRestoreFileBlocks;

        if (_artifactCryptor.Enabled && (_nativeStoreAvailable || _nativeRestoreAvailable))
        {
            _nativeStoreAvailable = false;
            _nativeRestoreAvailable = false;
            _log.LogWarning(
                "Artifact encryption is enabled. Native block-store is temporarily disabled to guarantee encrypted payload storage.");
        }

        if (_nativeStoreAvailable && _nativeRestoreAvailable)
        {
            _log.LogInformation(
                "Native block-store entrypoints detected. Library {LoadedPath}",
                native.LoadedPath ?? "(unknown)");
        }
        else
        {
            _log.LogWarning(
                "Native block-store entrypoints are unavailable at startup. Store {StoreAvailable}. Restore {RestoreAvailable}. Library {LoadedPath}. Error {Error}",
                _nativeStoreAvailable,
                _nativeRestoreAvailable,
                native.LoadedPath ?? "(not loaded)",
                native.ErrorMessage ?? "(no error details)");
        }
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
            if (!block.BlockHashBlake3.StartsWith(ManagedHashPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Managed fallback cannot restore native block hash {block.BlockHashBlake3}. Rebuild native veyra_core with block entrypoints.");

            var hash = block.BlockHashBlake3[ManagedHashPrefix.Length..];
            var blockPath = GetManagedBlockPath(hash);

            if (!File.Exists(blockPath))
                throw new FileNotFoundException("Block file not found for restore.", blockPath);

            var storedBytes = await File.ReadAllBytesAsync(blockPath, ct);
            var plaintextBytes = _artifactCryptor.Unprotect(storedBytes);

            if (plaintextBytes.Length < block.LengthBytes)
                throw new InvalidOperationException(
                    $"Block {block.BlockHashBlake3} is shorter than expected after decryption.");

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

    private sealed class ArtifactBlockCryptor
    {
        private const string Magic = "VYRAENC";
        private const byte Version = 1;
        private const int NonceSize = 12;
        private const int TagSize = 16;

        private readonly byte[] _key;

        private ArtifactBlockCryptor(bool enabled, byte[]? key)
        {
            Enabled = enabled;
            _key = key ?? [];
        }

        public bool Enabled { get; }

        public static ArtifactBlockCryptor Create(IConfiguration cfg, ILogger log)
        {
            var enabled = ParseBool(cfg["Security:ArtifactEncryption:Enabled"], defaultValue: true);
            if (!enabled)
            {
                log.LogInformation("Artifact encryption is disabled by configuration.");
                return new ArtifactBlockCryptor(false, null);
            }

            var keyBase64 = cfg["Security:ArtifactEncryption:KeyBase64"];
            if (string.IsNullOrWhiteSpace(keyBase64))
                keyBase64 = Environment.GetEnvironmentVariable("VEYRA_ARTIFACT_KEY_BASE64");

            if (!string.IsNullOrWhiteSpace(keyBase64))
            {
                var directKey = TryDecodeKeyMaterial(keyBase64.Trim());
                if (directKey is not null)
                {
                    log.LogInformation("Artifact encryption enabled via configured key material.");
                    return new ArtifactBlockCryptor(true, directKey);
                }

                log.LogWarning("Configured artifact encryption key is invalid. Falling back to local key file.");
            }

            try
            {
                var keyPath = ResolveKeyPath(cfg);
                Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);

                if (File.Exists(keyPath))
                {
                    var fromFile = TryDecodeKeyMaterial(File.ReadAllText(keyPath).Trim());
                    if (fromFile is not null)
                    {
                        log.LogInformation("Artifact encryption enabled. Key source: {KeyPath}", keyPath);
                        return new ArtifactBlockCryptor(true, fromFile);
                    }
                }

                var generated = new byte[32];
                RandomNumberGenerator.Fill(generated);
                File.WriteAllText(keyPath, Convert.ToBase64String(generated));

                log.LogInformation("Artifact encryption enabled. New local key generated at {KeyPath}", keyPath);
                return new ArtifactBlockCryptor(true, generated);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to initialize artifact encryption key storage. Continuing without encryption.");
                return new ArtifactBlockCryptor(false, null);
            }
        }

        public byte[] Protect(ReadOnlySpan<byte> plaintext, string plaintextHashSha256)
        {
            if (!Enabled)
                return plaintext.ToArray();

            var nonce = DeriveNonce(plaintextHashSha256);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];

            using (var aes = new AesGcm(_key, TagSize))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag);
            }

            var envelope = new byte[Magic.Length + 1 + NonceSize + ciphertext.Length + TagSize];
            Encoding.ASCII.GetBytes(Magic).CopyTo(envelope, 0);
            envelope[Magic.Length] = Version;
            nonce.CopyTo(envelope.AsSpan(Magic.Length + 1, NonceSize));
            ciphertext.CopyTo(envelope.AsSpan(Magic.Length + 1 + NonceSize, ciphertext.Length));
            tag.CopyTo(envelope.AsSpan(envelope.Length - TagSize, TagSize));

            return envelope;
        }

        public byte[] Unprotect(ReadOnlySpan<byte> stored)
        {
            if (!Enabled)
                return stored.ToArray();

            if (!LooksEncrypted(stored))
                return stored.ToArray();

            var nonceStart = Magic.Length + 1;
            var ciphertextStart = nonceStart + NonceSize;
            var ciphertextLength = stored.Length - ciphertextStart - TagSize;
            if (ciphertextLength < 0)
                throw new InvalidOperationException("Encrypted block envelope is invalid.");

            var nonce = stored.Slice(nonceStart, NonceSize).ToArray();
            var ciphertext = stored.Slice(ciphertextStart, ciphertextLength).ToArray();
            var tag = stored.Slice(stored.Length - TagSize, TagSize).ToArray();
            var plaintext = new byte[ciphertextLength];

            using (var aes = new AesGcm(_key, TagSize))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext);
            }

            return plaintext;
        }

        private static bool ParseBool(string? value, bool defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value))
                return defaultValue;

            return bool.TryParse(value.Trim(), out var parsed) ? parsed : defaultValue;
        }

        private static string ResolveKeyPath(IConfiguration cfg)
        {
            var configured = cfg["Security:ArtifactEncryption:KeyPath"];
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.GetFullPath(configured.Trim());

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VeyraFlow",
                "keys",
                "artifact_encryption.key");
        }

        private static byte[]? TryDecodeKeyMaterial(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            try
            {
                var bytes = Convert.FromBase64String(value);
                return bytes.Length == 32 ? bytes : null;
            }
            catch
            {
                // Fall through to hex parse.
            }

            try
            {
                var hex = value.Trim();
                if (hex.Length == 64)
                {
                    var bytes = Convert.FromHexString(hex);
                    return bytes.Length == 32 ? bytes : null;
                }
            }
            catch
            {
                // Ignore invalid hex.
            }

            return null;
        }

        private bool LooksEncrypted(ReadOnlySpan<byte> data)
        {
            if (data.Length < Magic.Length + 1 + NonceSize + TagSize)
                return false;

            var magic = Encoding.ASCII.GetBytes(Magic);
            if (!data.Slice(0, magic.Length).SequenceEqual(magic))
                return false;

            return data[magic.Length] == Version;
        }

        private byte[] DeriveNonce(string plaintextHashSha256)
        {
            var nonceSeed = HMACSHA256.HashData(
                _key,
                Encoding.UTF8.GetBytes($"veyra:block:{plaintextHashSha256}"));

            return nonceSeed.AsSpan(0, NonceSize).ToArray();
        }
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
