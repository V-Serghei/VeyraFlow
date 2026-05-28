namespace Veyra.Domain.Entities;

public class Repository
{
    public const string DefaultSyncConflictStrategy = "last_write_wins";

    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? ExclusionPatternsJson { get; set; }

    public int DirectoryId { get; set; }
    public Watched.WatchedDirectory Directory { get; set; } = null!;

    public int FileCount { get; set; }
    public int VersionCount { get; set; }
    public long TotalSizeBytes { get; set; }
    public DateTime? LastScannedAt { get; set; }
    public bool AutoCaptureFileVersions { get; set; }
    public bool ProtectCloudMetadata { get; set; } = true;

    public bool RetentionPolicyOverrideEnabled { get; set; }
    public bool RetentionEnabled { get; set; }
    public int? RetentionMaxAgeDays { get; set; }
    public int? RetentionMaxSnapshots { get; set; }
    public long? RetentionMaxTotalSizeBytes { get; set; }
    public string? RetentionTriggerFilter { get; set; }
    public int RetentionRunIntervalMinutes { get; set; } = 60;
    public int? RetentionMaintenanceWindowStartHour { get; set; }
    public int? RetentionMaintenanceWindowEndHour { get; set; }
    public DateTime? RetentionLastRunAt { get; set; }
    public string? RetentionLastStatus { get; set; }
    public string RetentionStorageMode { get; set; } = "delete";
    public bool RetentionAllowManualSnapshotCleanup { get; set; }
    public bool RetentionAutomaticCompactionEnabled { get; set; }
    public int? RetentionAutomaticCompactionWindowHours { get; set; }

    public string SyncConflictStrategy { get; set; } = DefaultSyncConflictStrategy;
    public int SyncRetryMaxAttempts { get; set; } = 5;
    public int SyncRetryBaseDelaySeconds { get; set; } = 30;
    public int? CloudRepositoryId { get; set; }
    public DateTime? CloudLastSyncedAt { get; set; }
    public long? CloudLastLocalSnapshotId { get; set; }
    public long? CloudLastRemoteSnapshotId { get; set; }
    public string? CloudSyncLastStatus { get; set; }
    public string? CloudSyncLastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    public ICollection<RepositorySnapshot> Snapshots { get; set; } = new List<RepositorySnapshot>();
    public ICollection<FileIdentity> FileIdentities { get; set; } = new List<FileIdentity>();
    public ICollection<RepositorySyncQueueItem> SyncQueueItems { get; set; } = new List<RepositorySyncQueueItem>();
}
