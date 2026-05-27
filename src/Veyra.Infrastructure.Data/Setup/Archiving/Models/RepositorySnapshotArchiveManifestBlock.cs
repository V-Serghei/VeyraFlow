namespace Veyra.Infrastructure.Data.Setup;

internal sealed class RepositorySnapshotArchiveManifestBlock
{
    public string BlockStorageKey { get; set; } = string.Empty;
    public string EntryName { get; set; } = string.Empty;
    public int LengthBytes { get; set; }
    public long StoredSizeBytes { get; set; }
}
