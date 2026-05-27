namespace Veyra.Infrastructure.Sync.Sync;

internal sealed class PushBlock
{
    public int Sequence { get; init; }
    public string? BlockHash { get; init; }
    public int LengthBytes { get; init; }
    public long StoredSizeBytes { get; init; }
}
