using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Veyra.Infrastructure.Native.Storage;

internal sealed record StorePayload
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
