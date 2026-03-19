namespace Veyra.Infrastructure.Sync.Sync;

internal sealed class StorageSummaryResponse
{
    public long LogicalBlockCount { get; init; }
    public long LogicalBytes { get; init; }
    public long PhysicalObjectCount { get; init; }
    public long PhysicalPayloadBytes { get; init; }
    public long MissingBlockCount { get; init; }
    public long ReducedObjectCount { get; init; }
    public long ReducedObjectPercentFloor { get; init; }
}
