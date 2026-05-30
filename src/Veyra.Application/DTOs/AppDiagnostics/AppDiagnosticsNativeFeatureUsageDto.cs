namespace Veyra.Application.DTOs.AppDiagnostics;

public sealed record AppDiagnosticsNativeFeatureUsageDto(
    string FeatureKey,
    bool Supported,
    int NativeHits,
    int ManagedFallbacks);
