namespace Veyra.Domain.Entities;

public class FileVersionBlock
{
    public long Id { get; set; }
    public long FileVersionId { get; set; }
    public FileVersion FileVersion { get; set; } = null!;

    public int Sequence { get; set; }
    public string BlockHashBlake3 { get; set; } = string.Empty;
    public int LengthBytes { get; set; }
    public long StoredSizeBytes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
