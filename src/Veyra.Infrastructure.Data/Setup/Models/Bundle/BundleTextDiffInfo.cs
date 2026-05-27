namespace Veyra.Infrastructure.Data.Setup;

internal sealed class BundleTextDiffInfo
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
    public int StorageFormatVersion { get; set; }
    public string LinesJson { get; set; } = "[]";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
