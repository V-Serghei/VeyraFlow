using System.Collections.Generic;

namespace Veyra.Desktop.Services.Sync;

internal sealed class RepositoryFsEventQueueState
{
    public long NextId { get; set; } = 1;
    public List<RepositoryFsEventQueueItem> Items { get; set; } = [];
}
