namespace Veyra.Domain.Entities;

public class Repository
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }

    public int DirectoryId { get; set; }
    public Watched.WatchedDirectory Directory { get; set; } = null!;

    public int FileCount { get; set; }
    public int VersionCount { get; set; }
    public long TotalSizeBytes { get; set; }
    public DateTime? LastScannedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    public ICollection<RepositorySnapshot> Snapshots { get; set; } = new List<RepositorySnapshot>();
    public ICollection<FileIdentity> FileIdentities { get; set; } = new List<FileIdentity>();
}
