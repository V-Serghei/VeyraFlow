namespace Veyra.Application.DTOs.Repository.Comparison;

public sealed record SnapshotLinkComparisonResultDto(
    int ChangedFilesCount,
    IReadOnlyList<SnapshotLinkChangeDto> Changes);
