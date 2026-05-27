namespace Veyra.Infrastructure.Data.Setup;

internal sealed class BundleSnapshotLinkInfo
{
    public long Id { get; set; }
    public long SnapshotId { get; set; }
    public long FileIdentityId { get; set; }
    public long FileVersionId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
