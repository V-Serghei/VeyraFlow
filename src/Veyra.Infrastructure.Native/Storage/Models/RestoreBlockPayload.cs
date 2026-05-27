using System.Text.Json.Serialization;

namespace Veyra.Infrastructure.Native.Storage;

internal sealed class RestoreBlockPayload
{
    [JsonPropertyName("block_hash_blake3")]
    public string BlockStorageKey { get; init; } = string.Empty;

    [JsonPropertyName("length_bytes")]
    public int LengthBytes { get; init; }
}
