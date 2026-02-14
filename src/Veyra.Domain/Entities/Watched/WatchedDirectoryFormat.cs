namespace Veyra.Domain.Entities.Watched;

public class WatchedDirectoryFormat
{
    public int Id { get; set; }
    public int DirectoryId { get; set; }
    public WatchedDirectory Directory { get; set; } = null!;
    public int FormatId { get; set; }
    public D_WatchedFormat Format { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; } = null;
}
