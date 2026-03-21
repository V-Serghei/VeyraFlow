using System.Text.Json.Serialization;

namespace Veyra.Infrastructure.Native.Diffing;

internal sealed record NativeLinkStatePayload
{
    [JsonPropertyName("file_identity_id")]
    public long FileIdentityId { get; init; }

    [JsonPropertyName("file_version_id")]
    public long FileVersionId { get; init; }

    [JsonPropertyName("is_deletion_marker")]
    public bool IsDeletionMarker { get; init; }

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("version_created_unix_seconds")]
    public long VersionCreatedUnixSeconds { get; init; }

    [JsonPropertyName("relative_path")]
    public string RelativePath { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
}
