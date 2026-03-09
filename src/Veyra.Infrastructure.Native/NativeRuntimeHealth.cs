using Veyra.Infrastructure.Native.Interop;

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
    string? LoadedPath,
    string? ErrorMessage,
    IReadOnlyList<string> MissingEntrypoints,
    IReadOnlyList<string> CandidatePaths);

public static class NativeRuntimeHealth
{
    private static readonly Lazy<NativeRuntimeHealthReport> Cached = new(
        VeyraCoreNative.ProbeRuntimeHealth,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static NativeRuntimeHealthReport Probe() => Cached.Value;
}
