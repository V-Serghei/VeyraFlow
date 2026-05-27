namespace Veyra.Infrastructure.Sync.Sync;

internal sealed class StorageMetricsResponse
{
    public bool Ok { get; init; }
    public StorageSummaryResponse? Summary { get; init; }
    public StorageBlocksResponse? Blocks { get; init; }
    public StoragePacksResponse? Packs { get; init; }
    public StorageFilesystemResponse? Filesystem { get; init; }
}
