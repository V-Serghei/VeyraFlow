using System;
using System.Collections.Generic;
using System.Linq;

namespace Veyra.Desktop.Services.Sync;

public sealed record RepositoryFsEventLease(
    IReadOnlyList<RepositoryFsEventLeaseItem> Items)
{
    public IReadOnlyList<long> ItemIds { get; } = Items.Select(static item => item.Id).ToArray();
    public int Count => Items.Count;

    public static RepositoryFsEventLease Empty { get; } = new(Array.Empty<RepositoryFsEventLeaseItem>());
}
