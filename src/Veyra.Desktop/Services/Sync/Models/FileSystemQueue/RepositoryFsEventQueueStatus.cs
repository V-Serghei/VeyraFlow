using System;

namespace Veyra.Desktop.Services.Sync;

public sealed record RepositoryFsEventQueueStatus(
    int PendingCount,
    int RunningCount,
    int TotalCount,
    int MaxRetryCount,
    DateTime? OldestPendingAtUtc,
    DateTime? LastUpdatedAtUtc,
    string? LastError)
{
    public static RepositoryFsEventQueueStatus Empty { get; } = new(0, 0, 0, 0, null, null, null);
}
