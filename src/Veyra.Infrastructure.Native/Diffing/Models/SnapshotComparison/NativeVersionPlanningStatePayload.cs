using System.Text.Json.Serialization;

namespace Veyra.Infrastructure.Native.Diffing;

internal sealed record NativeVersionPlanningStatePayload
{
    [JsonPropertyName("relative_path")]
    public string RelativePath { get; init; } = string.Empty;

    [JsonPropertyName("has_current")]
    public bool HasCurrent { get; init; }

    [JsonPropertyName("current_size_bytes")]
    public long CurrentSizeBytes { get; init; }

    [JsonPropertyName("current_content_hash_sha256")]
    public string? CurrentContentHashSha256 { get; init; }

    [JsonPropertyName("has_previous")]
    public bool HasPrevious { get; init; }

    [JsonPropertyName("previous_size_bytes")]
    public long PreviousSizeBytes { get; init; }

    [JsonPropertyName("previous_content_hash_sha256")]
    public string? PreviousContentHashSha256 { get; init; }

    [JsonPropertyName("has_latest_version")]
    public bool HasLatestVersion { get; init; }

    [JsonPropertyName("latest_is_deletion_marker")]
    public bool LatestIsDeletionMarker { get; init; }

    [JsonPropertyName("latest_size_bytes")]
    public long LatestSizeBytes { get; init; }

    [JsonPropertyName("latest_has_blocks")]
    public bool LatestHasBlocks { get; init; }
}
