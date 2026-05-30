namespace Veyra.Application.DTOs.TextDiff;

public sealed record TextDiffResultDto(
    string RelativePath,
    long LeftFileVersionId,
    long RightFileVersionId,
    int AddedLines,
    int RemovedLines,
    bool IsTruncated,
    IReadOnlyList<TextDiffLineDto> Lines,
    IReadOnlyList<TextDiffHunkDto> Hunks);
