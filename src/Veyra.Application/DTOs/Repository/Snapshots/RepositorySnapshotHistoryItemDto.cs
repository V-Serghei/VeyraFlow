namespace Veyra.Application.DTOs;

public sealed record RepositorySnapshotHistoryItemDto(
    long SnapshotId,
    string? Title,
    DateTime CreatedAtUtc,
    string Kind,
    bool IsArchived,
    string Trigger,
    int ChangedFilesCount,
    IReadOnlyList<string>? Tags = null);
