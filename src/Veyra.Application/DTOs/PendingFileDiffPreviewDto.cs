namespace Veyra.Application.DTOs;

public sealed record PendingFileDiffPreviewDto(
    string RelativePath,
    bool IsAvailable,
    string Message,
    int AddedLines,
    int RemovedLines,
    bool IsTruncated,
    IReadOnlyList<TextDiffLineDto> Lines,
    IReadOnlyList<TextDiffHunkDto> Hunks)
{
    public static PendingFileDiffPreviewDto Unavailable(string relativePath, string message)
        => new(
            RelativePath: relativePath,
            IsAvailable: false,
            Message: string.IsNullOrWhiteSpace(message) ? "Diff preview is unavailable." : message,
            AddedLines: 0,
            RemovedLines: 0,
            IsTruncated: false,
            Lines: Array.Empty<TextDiffLineDto>(),
            Hunks: Array.Empty<TextDiffHunkDto>());
}
