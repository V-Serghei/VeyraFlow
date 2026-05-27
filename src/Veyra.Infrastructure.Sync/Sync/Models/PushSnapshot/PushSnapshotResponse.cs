using System.Collections.Generic;

namespace Veyra.Infrastructure.Sync.Sync;

internal sealed class PushSnapshotResponse
{
    public bool Ok { get; init; }
    public List<string>? MissingBlockHashes { get; init; }
}
