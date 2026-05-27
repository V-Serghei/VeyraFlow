using System;

namespace Veyra.Infrastructure.Sync.Sync;

internal sealed class ListRepositoryItem
{
    public int RepositoryId { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public long? LatestSnapshotId { get; init; }
    public DateTime? LatestSnapshotCreatedAt { get; init; }
    public string? LatestSnapshotTitle { get; init; }
    public string? LatestSnapshotTrigger { get; init; }
    public int LatestSnapshotFileCount { get; init; }
    public int LatestSnapshotEntryCount { get; init; }
}
