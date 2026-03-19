namespace Veyra.Infrastructure.Data.Setup;

internal sealed class BundleFileVersionBlockInfo
{
    public long Id { get; set; }
    public long FileVersionId { get; set; }
    public int Sequence { get; set; }
    public string BlockHashBlake3 { get; set; } = string.Empty;
    public int LengthBytes { get; set; }
    public long StoredSizeBytes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
