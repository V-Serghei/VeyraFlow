namespace Veyra.Application.DTOs;

public sealed record PendingImageDiffPreviewDto(
    string BaselineImagePath,
    bool IsBaselineTempFile,
    string CurrentImagePath,
    bool IsCurrentTempFile,
    int? BaselineWidth,
    int? BaselineHeight,
    int? CurrentWidth,
    int? CurrentHeight,
    bool HasDimensionMismatch,
    double? SimilarityRatio);
