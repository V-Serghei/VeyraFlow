namespace Veyra.Application.DTOs.TextDiff;

public sealed record TextDiffComputationDto(
    int AddedLines,
    int RemovedLines,
    bool IsTruncated,
    IReadOnlyList<TextDiffLineDto> Lines,
    IReadOnlyList<TextDiffHunkDto> Hunks);
