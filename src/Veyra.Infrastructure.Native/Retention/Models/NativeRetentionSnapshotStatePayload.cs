using System.Text.Json.Serialization;

namespace Veyra.Infrastructure.Native.Retention;

internal sealed record NativeRetentionSnapshotStatePayload
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("created_at_ticks")]
    public long CreatedAtTicks { get; init; }

    [JsonPropertyName("created_at_utc_ticks")]
    public long CreatedAtUtcTicks { get; init; }

    [JsonPropertyName("total_file_bytes")]
    public long TotalFileBytes { get; init; }

    [JsonPropertyName("trigger")]
    public string Trigger { get; init; } = string.Empty;

    [JsonPropertyName("is_protected")]
    public bool IsProtected { get; init; }

    [JsonPropertyName("is_manual")]
    public bool IsManual { get; init; }

    [JsonPropertyName("is_automatic")]
    public bool IsAutomatic { get; init; }

    [JsonPropertyName("is_automatic_match")]
    public bool IsAutomaticMatch { get; init; }

    [JsonPropertyName("is_working")]
    public bool IsWorking { get; init; }
}
