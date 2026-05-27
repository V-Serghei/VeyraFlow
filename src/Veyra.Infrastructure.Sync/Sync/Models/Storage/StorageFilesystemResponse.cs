namespace Veyra.Infrastructure.Sync.Sync;

internal sealed class StorageFilesystemResponse
{
    public long PackFileCount { get; init; }
    public long LooseFileCount { get; init; }
    public long OtherFileCount { get; init; }
    public long PackFileBytes { get; init; }
    public long LooseFileBytes { get; init; }
    public long OtherFileBytes { get; init; }
    public long TotalPhysicalBytes { get; init; }
}
