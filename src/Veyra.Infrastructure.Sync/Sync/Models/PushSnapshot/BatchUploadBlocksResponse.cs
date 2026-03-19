namespace Veyra.Infrastructure.Sync.Sync;

internal sealed class BatchUploadBlocksResponse
{
    public bool Ok { get; init; }
    public int StoredBlocks { get; init; }
    public int SkippedBlocks { get; init; }
}
