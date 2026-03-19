namespace Veyra.Infrastructure.Sync.Sync;

internal sealed class StorageRepairResponse
{
    public bool Ok { get; init; }
    public StorageRepairStatsResponse? Repair { get; init; }
    public StorageMetricsResponse? Metrics { get; init; }
}
