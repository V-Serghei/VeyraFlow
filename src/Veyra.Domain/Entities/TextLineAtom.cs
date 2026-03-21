namespace Veyra.Domain.Entities;

public class TextLineAtom
{
    public long Id { get; set; }

    public string HashSha256 { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<FileVersionTextDiffLine> DiffLines { get; set; } = new List<FileVersionTextDiffLine>();
}
