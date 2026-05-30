namespace Veyra.Application.Abstractions.Setup;

public interface IRepositoryRetentionPlanner
{
    Task<RepositoryRetentionPlanResult> PlanAsync(
        RepositoryRetentionPlanRequest request,
        CancellationToken ct = default);
}

public sealed record RepositoryRetentionPlanRequest(
    IReadOnlyList<RepositoryRetentionSnapshotPlanState> Snapshots,
    int? MaxAgeDays,
    int? MaxSnapshots,
    long? MaxTotalSizeBytes,
    IReadOnlyCollection<string> TriggerFilter,
    bool AllowManualSnapshotCleanup,
    bool AutomaticCompactionEnabled,
    int? AutomaticCompactionWindowHours,
    DateTime NowUtc);

public sealed record RepositoryRetentionSnapshotPlanState(
    long Id,
    DateTime CreatedAt,
    long TotalFileBytes,
    string Trigger,
    bool IsProtected,
    bool IsManual,
    bool IsAutomatic,
    bool IsAutomaticMatch,
    bool IsWorking);

public sealed record RepositoryRetentionPlanResult(
    IReadOnlyCollection<long> SnapshotIdsToDelete,
    int AutomaticSnapshotsCompacted);
