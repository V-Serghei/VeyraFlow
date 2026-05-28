using System.Collections.Generic;

namespace Veyra.Infrastructure.Sync.Sync;

internal sealed class ListRepositorySnapshotsResponse
{
    public bool Ok { get; init; }
    public List<GetLatestSnapshotResponse>? Snapshots { get; init; }
}
