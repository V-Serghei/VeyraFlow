namespace Veyra.Application.DTOs.AppDiagnostics;

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
