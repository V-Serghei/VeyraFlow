namespace Veyra.Domain.Entities;

public class RepositorySyncQueueItem
{
    public const string OperationPushSnapshot = "push_snapshot";

    public const string StatusPending = "pending";
    public const string StatusRunning = "running";
    public const string StatusConflict = "conflict";
    public const string StatusCompleted = "completed";
    public const string StatusFailed = "failed";

    public long Id { get; set; }

    public int RepositoryId { get; set; }
    public Repository Repository { get; set; } = null!;

    public long SnapshotId { get; set; }
    public long RemoteSnapshotId { get; set; }

    public string OperationType { get; set; } = OperationPushSnapshot;
    public string Status { get; set; } = StatusPending;
    public string ConflictStrategy { get; set; } = Repository.DefaultSyncConflictStrategy;

    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTime NextAttemptAtUtc { get; set; } = DateTime.UtcNow;

    public long? ObservedRemoteSnapshotId { get; set; }
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
