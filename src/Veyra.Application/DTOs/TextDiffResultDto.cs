namespace Veyra.Application.DTOs;

public sealed record TextDiffResultDto(
    string RelativePath,
    long LeftFileVersionId,
    long RightFileVersionId,
    int AddedLines,
    int RemovedLines,
    bool IsTruncated,
    IReadOnlyList<TextDiffLineDto> Lines);
