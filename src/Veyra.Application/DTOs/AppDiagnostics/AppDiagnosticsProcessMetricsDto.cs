namespace Veyra.Application.DTOs;

public sealed record AppDiagnosticsProcessMetricsDto(
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    long ManagedHeapBytes,
    bool MeetsRecommendedLimit,
    long RecommendedLimitBytes);
