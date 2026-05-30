using System.Text.Json.Serialization;

namespace Veyra.Infrastructure.Native.Retention;

internal sealed record NativeRetentionPlanPayload
{
    [JsonPropertyName("snapshot_ids_to_delete")]
    public List<long> SnapshotIdsToDelete { get; init; } = [];

    [JsonPropertyName("automatic_snapshots_compacted")]
    public int AutomaticSnapshotsCompacted { get; init; }
}
