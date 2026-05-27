namespace Veyra.Infrastructure.Sync.Sync;

internal sealed class StorageRepairRequest
{
    public int ScanLimit { get; init; }
    public int CompactLimit { get; init; }
}
