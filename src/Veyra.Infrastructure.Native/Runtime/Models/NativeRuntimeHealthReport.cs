namespace Veyra.Infrastructure.Native.Runtime.Models;

public sealed record NativeRuntimeHealthReport(
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
    string? LoadedPath,
    string? ErrorMessage,
    IReadOnlyList<string> MissingEntrypoints,
    IReadOnlyList<string> CandidatePaths);
