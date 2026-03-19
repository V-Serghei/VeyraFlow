namespace Veyra.Infrastructure.Data.Setup;

internal sealed class BundleTextLineAtomInfo
{
    public long Id { get; set; }
    public string HashSha256 { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
