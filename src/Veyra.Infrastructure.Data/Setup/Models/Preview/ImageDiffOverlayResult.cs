namespace Veyra.Infrastructure.Data.Setup;

internal sealed record ImageDiffOverlayResult(
    string OverlayImagePath,
    bool IsOverlayTempFile,
    int ChangedPixelCount,
    double ChangedPixelRatio,
    int ChangedRegionCount);
