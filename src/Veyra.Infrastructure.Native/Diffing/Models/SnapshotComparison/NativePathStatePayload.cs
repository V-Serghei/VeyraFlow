using System.Text.Json.Serialization;

namespace Veyra.Infrastructure.Native.Diffing;

internal sealed record NativePathStatePayload
{
    [JsonPropertyName("relative_path")]
    public string RelativePath { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("last_write_unix_seconds")]
    public long LastWriteUnixSeconds { get; init; }

    [JsonPropertyName("content_hash_sha256")]
    public string? ContentHashSha256 { get; init; }
}
