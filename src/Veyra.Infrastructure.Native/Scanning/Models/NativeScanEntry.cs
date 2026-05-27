using System.Text.Json.Serialization;

namespace Veyra.Infrastructure.Native.Scanning;

internal sealed record NativeScanEntry
{
    [JsonPropertyName("relative_path")]
    public string RelativePath { get; init; } = string.Empty;

    [JsonPropertyName("parent_relative_path")]
    public string? ParentRelativePath { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("is_directory")]
    public bool IsDirectory { get; init; }

    [JsonPropertyName("extension")]
    public string? Extension { get; init; }

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("last_write_unix_seconds")]
    public long LastWriteUnixSeconds { get; init; }

    [JsonPropertyName("content_hash_sha256")]
    public string? ContentHashSha256 { get; init; }
}
