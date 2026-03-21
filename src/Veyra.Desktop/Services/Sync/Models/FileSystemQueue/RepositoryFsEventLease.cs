using System;
using System.Collections.Generic;

namespace Veyra.Desktop.Services.Sync;

public sealed record RepositoryFsEventLease(
    IReadOnlyList<long> ItemIds,
    int Count)
{
    public static RepositoryFsEventLease Empty { get; } = new(Array.Empty<long>(), 0);
}
