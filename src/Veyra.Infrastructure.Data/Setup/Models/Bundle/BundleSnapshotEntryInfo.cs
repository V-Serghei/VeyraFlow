namespace Veyra.Infrastructure.Data.Setup;

internal sealed class BundleSnapshotEntryInfo
{
    public long Id { get; set; }
    public long SnapshotId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string? ParentRelativePath { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public string? Extension { get; set; }
    public long SizeBytes { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public string? ContentHashSha256 { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
