namespace Veyra.Domain.Entities.Watched;

public class WatchedDirectory
{
    public int Id { get; set; }
    public required string Path { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; } = null;
    public string? ErrorMessage { get; set; }
    public ICollection<WatchedDirectoryFormat> DirectoryFormats { get; set; } = new List<WatchedDirectoryFormat>();
}
