namespace Veyra.Application.DTOs;

public sealed record SnapshotLinkComparisonResultDto(
    int ChangedFilesCount,
    IReadOnlyList<SnapshotLinkChangeDto> Changes);
