namespace Veyra.Domain.Entities;

public class SnapshotFileLink
{
    public long Id { get; set; }
    public long SnapshotId { get; set; }
    public RepositorySnapshot Snapshot { get; set; } = null!;

    public long FileIdentityId { get; set; }
    public FileIdentity FileIdentity { get; set; } = null!;

    public long FileVersionId { get; set; }
    public FileVersion FileVersion { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
