using System.Text.Json.Serialization;

namespace Veyra.Infrastructure.Native.Storage;

internal sealed record StoreBlockPayload
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
