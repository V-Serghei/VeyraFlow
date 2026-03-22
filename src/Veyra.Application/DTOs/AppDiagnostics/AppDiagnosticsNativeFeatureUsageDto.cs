namespace Veyra.Application.DTOs;

public sealed record AppDiagnosticsNativeFeatureUsageDto(
    string FeatureKey,
    bool Supported,
    int NativeHits,
    int ManagedFallbacks);
