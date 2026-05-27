namespace Veyra.Infrastructure.Data.Setup;

internal sealed class BundleBlockArtifactInfo
{
    public string Hash { get; set; } = string.Empty;
    public string EntryName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}
