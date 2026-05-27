namespace Veyra.Application.DTOs;

public sealed record AppDiagnosticsNativeRuntimeDto(
    bool IsLoaded,
    bool IsHealthy,
    bool SupportsScan,
    bool SupportsStoreFileBlocks,
    bool SupportsRestoreFileBlocks,
    bool SupportsTextDiff,
    bool SupportsSnapshotComparison,
    bool SupportsRepositoryPathComparison,
    bool SupportsVersionPlanning,
    bool SupportsImageDiff,
    IReadOnlyList<AppDiagnosticsNativeFeatureUsageDto> FeatureUsage,
    string? LoadedPath,
    string? ErrorMessage);
