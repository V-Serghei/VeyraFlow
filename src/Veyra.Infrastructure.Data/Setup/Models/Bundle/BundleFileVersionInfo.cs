namespace Veyra.Infrastructure.Data.Setup;

internal sealed class BundleFileVersionInfo
{
    public long Id { get; set; }
    public long FileIdentityId { get; set; }
    public string ContentHashSha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public bool IsDeletionMarker { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
