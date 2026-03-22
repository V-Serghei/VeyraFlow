using System.Text.Json.Serialization;

namespace Veyra.Infrastructure.Data.Setup;

internal sealed class BundleFileVersionBlockInfo
{
    public long Id { get; set; }
    public long FileVersionId { get; set; }
    public int Sequence { get; set; }
    [JsonPropertyName("blockHashBlake3")]
    public string BlockStorageKey { get; set; } = string.Empty;
    public int LengthBytes { get; set; }
    public long StoredSizeBytes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
