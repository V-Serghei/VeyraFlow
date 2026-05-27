using System.Text.Json.Serialization;

namespace Veyra.Infrastructure.Native.Security;

internal sealed class ArtifactKeyRingDocument
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("active_key_id")]
    public string ActiveKeyId { get; set; } = string.Empty;

    [JsonPropertyName("updated_at_utc")]
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("keys")]
    public List<ArtifactKeyRecordDocument> Keys { get; set; } = [];
}
