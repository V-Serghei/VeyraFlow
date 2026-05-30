namespace Veyra.Application.DTOs.Repository.Retention;

public sealed record RepositoryRetentionPolicyDto(
    bool Enabled,
    int? MaxAgeDays,
    int? MaxSnapshots,
    long? MaxTotalSizeBytes,
    IReadOnlyList<string> TriggerFilters,
    int RunIntervalMinutes,
    int? MaintenanceWindowStartHour,
    int? MaintenanceWindowEndHour,
    DateTime? LastRunAtUtc,
    string? LastStatus,
    string StorageMode = RepositoryRetentionStorageModes.Delete,
    bool AllowManualSnapshotCleanup = false,
    bool AutomaticCompactionEnabled = false,
    int? AutomaticCompactionWindowHours = null,
    bool HasLocalOverride = true,
    string PolicySource = RepositoryRetentionPolicySources.Repository,
    int? SourceRepositoryId = null);
