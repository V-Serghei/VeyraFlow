namespace Veyra.Infrastructure.Data.Setup;

internal sealed class BundleFileIdentityInfo
{
    public long Id { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Extension { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
