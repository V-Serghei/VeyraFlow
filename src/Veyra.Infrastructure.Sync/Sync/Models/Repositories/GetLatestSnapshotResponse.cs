using System.Collections.Generic;

namespace Veyra.Infrastructure.Sync.Sync;

internal sealed class GetLatestSnapshotResponse
{
    public bool Ok { get; init; }
    public PushRepositoryMeta? Repository { get; init; }
    public PushSnapshotMeta? Snapshot { get; init; }
    public List<PushEntry>? Entries { get; init; }
    public List<PushFileVersion>? FileVersions { get; init; }
}
