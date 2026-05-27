namespace Veyra.Application.DTOs;

public sealed record AppDiagnosticsHistoryMetricsDto(
    bool Available,
    int SnapshotHistoryCount,
    long SnapshotHistoryLoadMs,
    int LatestEntriesCount,
    long LatestEntriesLoadMs,
    bool MeetsLatestEntriesTarget,
    long TargetMs,
    string? ErrorMessage = null);
