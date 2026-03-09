namespace Veyra.Domain.Entities;

public class RepositorySnapshotEntry
{
    public long Id { get; set; }
    public long SnapshotId { get; set; }
    public RepositorySnapshot Snapshot { get; set; } = null!;

    public int RepositoryId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string? ParentRelativePath { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public string? Extension { get; set; }
    public long SizeBytes { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public string? ContentHashSha256 { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
}

