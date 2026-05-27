namespace Veyra.Infrastructure.Data.Setup;

internal sealed class BundleRepositoryInfo
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int FileCount { get; set; }
    public int VersionCount { get; set; }
    public long TotalSizeBytes { get; set; }
    public DateTime? LastScannedAtUtc { get; set; }
    public bool AutoCaptureFileVersions { get; set; }
    public bool ProtectCloudMetadata { get; set; } = true;
}
