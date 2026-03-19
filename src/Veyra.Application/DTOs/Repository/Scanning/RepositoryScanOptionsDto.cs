namespace Veyra.Application.DTOs;

public sealed record RepositoryScanOptionsDto(
    bool IsScheduled = false,
    int MaxReadBytesPerSecond = 0,
    int MaxIoOperationsPerSecond = 0,
    bool SaveFileVersions = false,
    string? TriggerOverride = null,
    string? SnapshotTitle = null,
    IReadOnlyList<string>? SnapshotTags = null);
