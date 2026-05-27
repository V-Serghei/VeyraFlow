namespace Veyra.Domain.Entities;

public class FileVersionTextDiff
{
    public long Id { get; set; }

    public long LeftFileVersionId { get; set; }
    public long RightFileVersionId { get; set; }

    public int MaxLines { get; set; }
    public string DiffKeySha256 { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;

    public int AddedLines { get; set; }
    public int RemovedLines { get; set; }
    public bool IsTruncated { get; set; }
    public int StorageFormatVersion { get; set; } = 2;

    public string LinesJson { get; set; } = "[]";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    public ICollection<FileVersionTextDiffHunk> Hunks { get; set; } = new List<FileVersionTextDiffHunk>();
    public ICollection<FileVersionTextDiffLine> Lines { get; set; } = new List<FileVersionTextDiffLine>();
}




