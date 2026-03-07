namespace Veyra.Domain.Entities;

public class FileVersionTextDiffLine
{
    public long Id { get; set; }

    public long DiffId { get; set; }
    public FileVersionTextDiff Diff { get; set; } = null!;

    public int Sequence { get; set; }
    public string Kind { get; set; } = string.Empty;
    public int? LeftLineNumber { get; set; }
    public int? RightLineNumber { get; set; }

    public long TextLineAtomId { get; set; }
    public TextLineAtom TextLineAtom { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
