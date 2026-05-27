namespace Veyra.Infrastructure.Sync.Sync;

internal sealed class StorageBlocksResponse
{
    public long TotalBlocks { get; init; }
    public long PackedBlocks { get; init; }
    public long LooseBlocks { get; init; }
    public long MissingBlocks { get; init; }
    public long LogicalBytes { get; init; }
    public long PackedBytes { get; init; }
    public long LooseBytes { get; init; }
    public long MissingBytes { get; init; }
}
