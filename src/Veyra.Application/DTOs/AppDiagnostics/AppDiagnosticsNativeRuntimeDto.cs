namespace Veyra.Application.DTOs.AppDiagnostics;

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
    bool SupportsRetentionPlanning,
    bool SupportsImageDiff,
    IReadOnlyList<AppDiagnosticsNativeFeatureUsageDto> FeatureUsage,
    string? LoadedPath,
    string? ErrorMessage);
