namespace Veyra.Infrastructure.Sync.Sync;

internal sealed class StoragePacksResponse
{
    public long TotalPacks { get; init; }
    public long ActivePacks { get; init; }
    public long SealedPacks { get; init; }
    public long BytesWritten { get; init; }
    public long PackedBlockRefs { get; init; }
}
