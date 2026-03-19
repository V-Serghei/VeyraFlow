namespace Veyra.Infrastructure.Data.Setup;

internal sealed class BundleSnapshotInfo
{
    public long Id { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string Trigger { get; set; } = string.Empty;
    public string? Title { get; set; }
    public int TotalEntries { get; set; }
    public int FileEntries { get; set; }
    public int DirectoryEntries { get; set; }
    public long TotalFileBytes { get; set; }
}
