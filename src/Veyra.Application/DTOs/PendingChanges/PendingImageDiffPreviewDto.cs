namespace Veyra.Application.DTOs;

public sealed record PendingImageDiffPreviewDto
{
    public string BaselineImagePath { get; init; } = string.Empty;
    public bool IsBaselineTempFile { get; init; }

    public string CurrentImagePath { get; init; } = string.Empty;
    public bool IsCurrentTempFile { get; init; }

    public string? OverlayImagePath { get; init; }
    public bool IsOverlayTempFile { get; init; }

    public int? BaselineWidth { get; init; }
    public int? BaselineHeight { get; init; }
    public int? CurrentWidth { get; init; }
    public int? CurrentHeight { get; init; }

    public bool HasDimensionMismatch { get; init; }
    public double? SimilarityRatio { get; init; }

    public int ChangedPixelCount { get; init; }
    public double? ChangedPixelRatio { get; init; }
    public int ChangedRegionCount { get; init; }

    public bool IsVectorImage { get; init; }
    public int AddedElementCount { get; init; }
    public int RemovedElementCount { get; init; }
    public int ModifiedElementCount { get; init; }
    public int ChangedAttributeCount { get; init; }
}
