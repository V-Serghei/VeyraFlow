namespace Veyra.Domain.Entities;

public class FileIdentity
{
    public long Id { get; set; }
    public int RepositoryId { get; set; }
    public Repository Repository { get; set; } = null!;

    public string RelativePath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Extension { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<FileVersion> Versions { get; set; } = new List<FileVersion>();
    public ICollection<SnapshotFileLink> SnapshotLinks { get; set; } = new List<SnapshotFileLink>();
}

