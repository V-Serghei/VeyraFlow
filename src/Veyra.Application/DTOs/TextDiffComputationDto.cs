namespace Veyra.Application.DTOs;

public sealed record TextDiffComputationDto(
    int AddedLines,
    int RemovedLines,
    bool IsTruncated,
    IReadOnlyList<TextDiffLineDto> Lines);
