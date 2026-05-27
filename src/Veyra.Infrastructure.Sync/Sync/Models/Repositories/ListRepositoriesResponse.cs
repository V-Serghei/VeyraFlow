using System.Collections.Generic;

namespace Veyra.Infrastructure.Sync.Sync;

internal sealed class ListRepositoriesResponse
{
    public bool Ok { get; init; }
    public List<ListRepositoryItem>? Repositories { get; init; }
}
