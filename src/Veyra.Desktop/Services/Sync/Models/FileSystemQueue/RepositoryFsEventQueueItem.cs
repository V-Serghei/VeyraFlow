using System;

namespace Veyra.Desktop.Services.Sync;

internal sealed class RepositoryFsEventQueueItem
{
    public long Id { get; set; }
    public int RepositoryId { get; set; }
    public string FullPath { get; set; } = string.Empty;
    public string EventKind { get; set; } = "changed";
    public FsEventStatus Status { get; set; } = FsEventStatus.Pending;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
}
