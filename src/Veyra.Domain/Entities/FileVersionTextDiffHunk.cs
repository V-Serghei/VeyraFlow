namespace Veyra.Domain.Entities;

public class FileVersionTextDiffHunk
{
    public long Id { get; set; }

    public long DiffId { get; set; }
    public FileVersionTextDiff Diff { get; set; } = null!;

    public int Sequence { get; set; }

    public int OldStartLine { get; set; }
    public int OldLineCount { get; set; }
    public int NewStartLine { get; set; }
    public int NewLineCount { get; set; }

    public string ChangeKind { get; set; } = "modified";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<FileVersionTextDiffLine> Lines { get; set; } = new List<FileVersionTextDiffLine>();
}
