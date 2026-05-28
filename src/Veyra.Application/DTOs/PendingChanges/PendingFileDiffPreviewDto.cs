namespace Veyra.Application.DTOs;

public sealed record PendingFileDiffPreviewDto
{
    public string RelativePath { get; init; } = string.Empty;
    public bool IsAvailable { get; init; }
    public string Message { get; init; } = string.Empty;
    public PendingDiffPreviewKind Kind { get; init; } = PendingDiffPreviewKind.None;

    public int AddedLines { get; init; }
    public int RemovedLines { get; init; }
    public bool IsTruncated { get; init; }
    public IReadOnlyList<TextDiffLineDto> Lines { get; init; } = Array.Empty<TextDiffLineDto>();
    public IReadOnlyList<TextDiffHunkDto> Hunks { get; init; } = Array.Empty<TextDiffHunkDto>();

    public PendingBinaryDiffSummaryDto? BinarySummary { get; init; }
    public PendingImageDiffPreviewDto? ImagePreview { get; init; }
    public PendingAudioDiffPreviewDto? AudioPreview { get; init; }
    public PendingArchiveDiffPreviewDto? ArchivePreview { get; init; }

    public static PendingFileDiffPreviewDto Unavailable(string relativePath, string message)
        => new()
        {
            RelativePath = relativePath,
            IsAvailable = false,
            Message = string.IsNullOrWhiteSpace(message) ? "Diff preview is unavailable." : message,
            Kind = PendingDiffPreviewKind.None
        };

    public static PendingFileDiffPreviewDto FromText(
        string relativePath,
        string message,
        int addedLines,
        int removedLines,
        bool isTruncated,
        IReadOnlyList<TextDiffLineDto> lines,
        IReadOnlyList<TextDiffHunkDto> hunks)
        => new()
        {
            RelativePath = relativePath,
            IsAvailable = true,
            Message = message,
            Kind = PendingDiffPreviewKind.Text,
            AddedLines = addedLines,
            RemovedLines = removedLines,
            IsTruncated = isTruncated,
            Lines = lines,
            Hunks = hunks
        };

    public static PendingFileDiffPreviewDto FromBinary(
        string relativePath,
        string message,
        PendingBinaryDiffSummaryDto binarySummary)
        => new()
        {
            RelativePath = relativePath,
            IsAvailable = true,
            Message = message,
            Kind = PendingDiffPreviewKind.Binary,
            BinarySummary = binarySummary
        };

    public static PendingFileDiffPreviewDto FromImage(
        string relativePath,
        string message,
        PendingBinaryDiffSummaryDto binarySummary,
        PendingImageDiffPreviewDto imagePreview)
        => new()
        {
            RelativePath = relativePath,
            IsAvailable = true,
            Message = message,
            Kind = PendingDiffPreviewKind.Image,
            BinarySummary = binarySummary,
            ImagePreview = imagePreview
        };

    public static PendingFileDiffPreviewDto FromAudio(
        string relativePath,
        string message,
        PendingBinaryDiffSummaryDto binarySummary,
        PendingAudioDiffPreviewDto audioPreview)
        => new()
        {
            RelativePath = relativePath,
            IsAvailable = true,
            Message = message,
            Kind = PendingDiffPreviewKind.Audio,
            BinarySummary = binarySummary,
            AudioPreview = audioPreview
        };

    public static PendingFileDiffPreviewDto FromArchive(
        string relativePath,
        string message,
        PendingBinaryDiffSummaryDto binarySummary,
        PendingArchiveDiffPreviewDto archivePreview)
        => new()
        {
            RelativePath = relativePath,
            IsAvailable = true,
            Message = message,
            Kind = PendingDiffPreviewKind.Archive,
            BinarySummary = binarySummary,
            ArchivePreview = archivePreview
        };
}
