using System;
using System.Text.Json.Serialization;

namespace Veyra.Infrastructure.Native.Security;

internal sealed class ArtifactKeyRecordDocument
{
    [JsonPropertyName("key_id")]
    public string KeyId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("created_at_utc")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("rotated_at_utc")]
    public DateTime? RotatedAtUtc { get; set; }

    [JsonPropertyName("revoked_at_utc")]
    public DateTime? RevokedAtUtc { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("salt_base64")]
    public string SaltBase64 { get; set; } = string.Empty;
}
