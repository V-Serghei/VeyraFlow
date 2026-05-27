using System.Text.Json.Serialization;

namespace Veyra.Infrastructure.Native.Diffing;

internal sealed record NativeVersionPlanEntryPayload
{
    [JsonPropertyName("relative_path")]
    public string? RelativePath { get; init; }

    [JsonPropertyName("change_kind")]
    public string? ChangeKind { get; init; }

    [JsonPropertyName("should_create_new_version")]
    public bool ShouldCreateNewVersion { get; init; }

    [JsonPropertyName("should_mark_identity_deleted")]
    public bool ShouldMarkIdentityDeleted { get; init; }
}
