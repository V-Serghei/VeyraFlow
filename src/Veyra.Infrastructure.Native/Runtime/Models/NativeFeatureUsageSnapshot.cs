namespace Veyra.Infrastructure.Native;

public sealed record NativeFeatureUsageSnapshot(
    string FeatureKey,
    int NativeHits,
    int ManagedFallbacks);
