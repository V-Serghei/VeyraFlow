namespace Veyra.Desktop.Services.Preview;

internal sealed record InteractiveImageDiffRenderFrame(
    byte[] PngBytes,
    int PixelWidth,
    int PixelHeight,
    int ChangedPixelCount,
    double ChangedPixelRatio,
    int ChangedRegionCount);
