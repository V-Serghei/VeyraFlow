using System.Text.Json.Serialization;

namespace Veyra.Infrastructure.Native.Diffing;

internal sealed record NativeLinkChangePayload
{
    [JsonPropertyName("file_identity_id")]
    public long FileIdentityId { get; init; }

    [JsonPropertyName("file_version_id")]
    public long FileVersionId { get; init; }

    [JsonPropertyName("relative_path")]
    public string? RelativePath { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("change_kind")]
    public string? ChangeKind { get; init; }

    [JsonPropertyName("current_size_bytes")]
    public long CurrentSizeBytes { get; init; }

    [JsonPropertyName("previous_size_bytes")]
    public long PreviousSizeBytes { get; init; }

    [JsonPropertyName("version_created_unix_seconds")]
    public long VersionCreatedUnixSeconds { get; init; }
}
