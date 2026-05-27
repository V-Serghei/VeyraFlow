using System.Text.Json.Serialization;

namespace Veyra.Infrastructure.Native.Diffing;

internal sealed record NativePathChangePayload
{
    [JsonPropertyName("relative_path")]
    public string? RelativePath { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("change_kind")]
    public string? ChangeKind { get; init; }

    [JsonPropertyName("current_size_bytes")]
    public long CurrentSizeBytes { get; init; }

    [JsonPropertyName("baseline_size_bytes")]
    public long BaselineSizeBytes { get; init; }

    [JsonPropertyName("current_last_write_unix_seconds")]
    public long CurrentLastWriteUnixSeconds { get; init; }

    [JsonPropertyName("baseline_last_write_unix_seconds")]
    public long? BaselineLastWriteUnixSeconds { get; init; }
}
