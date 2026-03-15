namespace Veyra.Application.DTOs;

public sealed record AppDiagnosticsReportDto(
    DateTime GeneratedAtUtc,
    int? TargetRepositoryId,
    string? TargetRepositoryName,
    string? TargetRepositoryPath,
    AppDiagnosticsProcessMetricsDto Process,
    AppDiagnosticsHistoryMetricsDto History,
    AppDiagnosticsScanBenchmarkDto Scan,
    AppDiagnosticsNativeRuntimeDto NativeRuntime)
{
    public bool HasTargetRepository => TargetRepositoryId.HasValue;
}

public sealed record AppDiagnosticsProcessMetricsDto(
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    long ManagedHeapBytes,
    bool MeetsRecommendedLimit,
    long RecommendedLimitBytes);

public sealed record AppDiagnosticsHistoryMetricsDto(
    bool Available,
    int SnapshotHistoryCount,
    long SnapshotHistoryLoadMs,
    int LatestEntriesCount,
    long LatestEntriesLoadMs,
    bool MeetsLatestEntriesTarget,
    long TargetMs,
    string? ErrorMessage = null);

public sealed record AppDiagnosticsScanBenchmarkDto(
    bool Available,
    string Engine,
    int EntryCount,
    int FileCount,
    long TotalFileBytes,
    long DurationMs,
    double ThroughputMbPerSecond,
    bool MeetsRecommendedTarget,
    double TargetMbPerSecond,
    string? ErrorMessage = null);

public sealed record AppDiagnosticsNativeRuntimeDto(
    bool IsLoaded,
    bool IsHealthy,
    bool SupportsScan,
    string? LoadedPath,
    string? ErrorMessage);
