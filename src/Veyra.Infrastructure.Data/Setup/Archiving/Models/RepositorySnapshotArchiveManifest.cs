namespace Veyra.Infrastructure.Data.Setup;

internal sealed class RepositorySnapshotArchiveManifest
{
    public int RepositoryId { get; set; }
    public long SnapshotId { get; set; }
    public DateTime SnapshotCreatedAtUtc { get; set; }
    public string SnapshotTrigger { get; set; } = string.Empty;
    public string? SnapshotTitle { get; set; }
    public List<RepositorySnapshotArchiveManifestBlock> Blocks { get; set; } = [];
}
