namespace Veyra.Domain.Entities.Watched;

public class D_WatchedFormat
{
    public int Id { get; set; }
    public required string Pattern { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; } = null;
    public ICollection<WatchedDirectoryFormat> DirectoryFormats { get; set; } = new List<WatchedDirectoryFormat>();
}
