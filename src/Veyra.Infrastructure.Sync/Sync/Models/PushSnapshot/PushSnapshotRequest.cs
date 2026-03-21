using System.Collections.Generic;

namespace Veyra.Infrastructure.Sync.Sync;

internal sealed class PushSnapshotRequest
{
    public PushRepositoryMeta? Repository { get; init; }
    public PushSnapshotMeta? Snapshot { get; init; }
    public List<PushEntry>? Entries { get; init; }
    public List<PushFileVersion>? FileVersions { get; init; }
}
