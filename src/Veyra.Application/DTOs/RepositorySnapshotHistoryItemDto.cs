namespace Veyra.Application.DTOs;

public sealed record RepositorySnapshotHistoryItemDto(
    long SnapshotId,
    string? Title,
    DateTime CreatedAtUtc,
    string Trigger,
    int ChangedFilesCount);
