namespace Veyra.Application.DTOs.Repository.Snapshots;

public sealed record RepositorySnapshotHistoryItemDto(
    long SnapshotId,
    string? Title,
    DateTime CreatedAtUtc,
    string Kind,
    bool IsArchived,
    string Trigger,
    int ChangedFilesCount,
    IReadOnlyList<string>? Tags = null);
