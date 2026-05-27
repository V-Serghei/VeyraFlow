namespace Veyra.Infrastructure.Sync.Sync;

internal sealed class StorageRepairStatsResponse
{
    public int Scanned { get; init; }
    public int MissingMarked { get; init; }
    public int BrokenLooseRefs { get; init; }
    public int BrokenPackRefs { get; init; }
    public int Compacted { get; init; }
}
