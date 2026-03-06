namespace Veyra.Application.DTOs;

public sealed record RepositoryScanOptionsDto(
    bool IsScheduled = false,
    int MaxReadBytesPerSecond = 0,
    int MaxIoOperationsPerSecond = 0,
    string? TriggerOverride = null);

