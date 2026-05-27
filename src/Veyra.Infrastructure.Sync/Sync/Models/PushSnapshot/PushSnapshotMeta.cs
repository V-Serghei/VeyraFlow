using System;

namespace Veyra.Infrastructure.Sync.Sync;

internal sealed class PushSnapshotMeta
{
    public long Id { get; init; }
    public string? Title { get; init; }
    public string? Trigger { get; init; }
    public DateTime CreatedAt { get; init; }
    public int TotalEntries { get; init; }
    public int FileEntries { get; init; }
    public int DirectoryEntries { get; init; }
    public long TotalFileBytes { get; init; }
    public string? PayloadSha256 { get; init; }
}
