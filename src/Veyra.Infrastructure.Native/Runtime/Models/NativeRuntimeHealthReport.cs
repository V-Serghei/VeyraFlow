using System.Collections.Generic;

namespace Veyra.Infrastructure.Native;

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
    bool SupportsImageDiff,
    string? LoadedPath,
    string? ErrorMessage,
    IReadOnlyList<string> MissingEntrypoints,
    IReadOnlyList<string> CandidatePaths);
