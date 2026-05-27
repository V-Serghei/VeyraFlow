namespace Veyra.Domain.Entities;

public class FileSnapshot
{
    public int Id { get; set; }
    public required string FilePath { get; set; }
    public required string ContentHash { get; set; }
    public long FileSize { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsDeleted { get; set; }
}
