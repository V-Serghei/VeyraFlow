namespace Veyra.Domain.Entities;

public class FileVersion
{
    public long Id { get; set; }
    public long FileIdentityId { get; set; }
    public FileIdentity FileIdentity { get; set; } = null!;

    public string ContentHashSha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public bool IsDeletionMarker { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    public ICollection<FileVersionBlock> Blocks { get; set; } = new List<FileVersionBlock>();
    public ICollection<SnapshotFileLink> SnapshotLinks { get; set; } = new List<SnapshotFileLink>();
}

