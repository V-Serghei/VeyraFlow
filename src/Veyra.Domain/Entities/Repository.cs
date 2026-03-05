namespace Veyra.Domain.Entities;

public class Repository
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }

    public int DirectoryId { get; set; }
    public Watched.WatchedDirectory Directory { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
}
