namespace Veyra.Application.DTOs;

public sealed record NativeRuntimeHealthDto(
    bool IsHealthy,
    bool SupportsImageDiff,
    bool SupportsTextDiff,
    string? LoadedPath,
    string? ErrorMessage,
    IReadOnlyList<string> MissingEntrypoints);
