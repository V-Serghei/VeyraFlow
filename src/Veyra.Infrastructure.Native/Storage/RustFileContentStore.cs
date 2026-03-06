using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.DTOs;
using Veyra.Infrastructure.Native.Interop;

namespace Veyra.Infrastructure.Native.Storage;

public sealed class RustFileContentStore(
    IConfiguration configuration,
    ILogger<RustFileContentStore> log) : IFileContentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _storeRoot = ResolveStoreRoot(configuration);

    public Task<StoredFileContentDto> StoreFileAsync(string filePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path is required.", nameof(filePath));

        var fullPath = Path.GetFullPath(filePath);
        var json = VeyraCoreNative.StoreFileBlocksJson(fullPath, _storeRoot, 64 * 1024);

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

        log.LogInformation(
            "Stored file in block-store. Path {Path}. Blocks {Blocks}. Deduped {Deduped}. New {New}",
            fullPath,
            payload.BlockCount,
            payload.DedupedBlocks,
            payload.NewBlocks);

        return Task.FromResult(new StoredFileContentDto(
            payload.FileSizeBytes,
            payload.StoredSizeBytes,
            payload.BlockCount,
            payload.DedupedBlocks,
            payload.NewBlocks,
            blocks));
    }

    public Task<long> RestoreFileAsync(
        IReadOnlyList<StoredFileBlockDto> blocks,
        string targetPath,
        bool overwriteExisting,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (blocks.Count == 0)
            throw new InvalidOperationException("No blocks provided for restore.");

        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("Target path is required.", nameof(targetPath));

        var fullTarget = Path.GetFullPath(targetPath);

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

        log.LogInformation(
            "Restored file from block-store. Target {Target}. Bytes {Bytes}. Blocks {Blocks}",
            fullTarget,
            written,
            blocks.Count);

        return Task.FromResult(written);
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
