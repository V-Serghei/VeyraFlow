namespace Veyra.Application.DTOs.CloudSync;

public sealed record CloudRepositoryHeaderDto(
    int RepositoryId,
    string Name,
    string? Description,
    long? LatestSnapshotId,
    DateTime? LatestSnapshotCreatedAtUtc,
    string? LatestSnapshotTitle,
    string? LatestSnapshotTrigger,
    int LatestSnapshotFileCount,
    int LatestSnapshotEntryCount);
