using System.Text.Json.Serialization;

namespace Veyra.Infrastructure.Native.Retention;

internal sealed record NativeRetentionPlanRequestPayload
{
    [JsonPropertyName("snapshots")]
    public List<NativeRetentionSnapshotStatePayload> Snapshots { get; init; } = [];

    [JsonPropertyName("max_age_days")]
    public int? MaxAgeDays { get; init; }

    [JsonPropertyName("max_snapshots")]
    public int? MaxSnapshots { get; init; }

    [JsonPropertyName("max_total_size_bytes")]
    public long? MaxTotalSizeBytes { get; init; }

    [JsonPropertyName("trigger_filter")]
    public List<string> TriggerFilter { get; init; } = [];

    [JsonPropertyName("allow_manual_snapshot_cleanup")]
    public bool AllowManualSnapshotCleanup { get; init; }

    [JsonPropertyName("automatic_compaction_enabled")]
    public bool AutomaticCompactionEnabled { get; init; }

    [JsonPropertyName("automatic_compaction_window_hours")]
    public int? AutomaticCompactionWindowHours { get; init; }

    [JsonPropertyName("now_ticks")]
    public long NowTicks { get; init; }
}
