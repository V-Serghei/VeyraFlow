namespace Veyra.Domain.Entities;

public class RepositorySnapshot
{
    public long Id { get; set; }
    public int RepositoryId { get; set; }
    public Repository Repository { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public required string Trigger { get; set; }
    public string? Title { get; set; }
    public string? TagsCsv { get; set; }
    public bool IsArchived { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public string? ArchiveFilePath { get; set; }
    public long? ArchiveFileSizeBytes { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    public int TotalEntries { get; set; }
    public int FileEntries { get; set; }
    public int DirectoryEntries { get; set; }
    public long TotalFileBytes { get; set; }

    public ICollection<RepositorySnapshotEntry> Entries { get; set; } = new List<RepositorySnapshotEntry>();
    public ICollection<SnapshotFileLink> FileLinks { get; set; } = new List<SnapshotFileLink>();
}
