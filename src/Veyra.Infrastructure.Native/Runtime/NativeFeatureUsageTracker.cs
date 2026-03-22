using System.Collections.Concurrent;
using System.Threading;

namespace Veyra.Infrastructure.Native;

public static class NativeFeatureUsageTracker
{
    public const string Scan = "scan";
    public const string StoreBlocks = "store_blocks";
    public const string RestoreBlocks = "restore_blocks";
    public const string TextDiff = "text_diff";
    public const string SnapshotComparison = "snapshot_compare";
    public const string RepositoryPathComparison = "repository_path_compare";
    public const string VersionPlanning = "version_planning";
    public const string ImageDiff = "image_diff";

    private static readonly string[] FeatureOrder =
    [
        Scan,
        StoreBlocks,
        RestoreBlocks,
        TextDiff,
        SnapshotComparison,
        RepositoryPathComparison,
        VersionPlanning,
        ImageDiff
    ];

    private static readonly ConcurrentDictionary<string, NativeFeatureCounter> Counters =
        new(StringComparer.OrdinalIgnoreCase);

    public static void MarkNativeHit(string featureKey)
    {
        var counter = Counters.GetOrAdd(featureKey, static _ => new NativeFeatureCounter());
        Interlocked.Increment(ref counter.NativeHits);
    }

    public static void MarkManagedFallback(string featureKey)
    {
        var counter = Counters.GetOrAdd(featureKey, static _ => new NativeFeatureCounter());
        Interlocked.Increment(ref counter.ManagedFallbacks);
    }

    public static IReadOnlyList<NativeFeatureUsageSnapshot> Snapshot()
        => FeatureOrder
            .Select(GetSnapshot)
            .ToArray();

    private static NativeFeatureUsageSnapshot GetSnapshot(string featureKey)
    {
        if (!Counters.TryGetValue(featureKey, out var counter))
            return new NativeFeatureUsageSnapshot(featureKey, 0, 0);

        return new NativeFeatureUsageSnapshot(
            featureKey,
            counter.NativeHits,
            counter.ManagedFallbacks);
    }

    private sealed class NativeFeatureCounter
    {
        public int NativeHits;
        public int ManagedFallbacks;
    }
}
