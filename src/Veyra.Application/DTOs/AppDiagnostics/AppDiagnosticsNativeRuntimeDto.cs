namespace Veyra.Application.DTOs;

public sealed record AppDiagnosticsNativeRuntimeDto(
    bool IsLoaded,
    bool IsHealthy,
    bool SupportsScan,
    string? LoadedPath,
    string? ErrorMessage);
