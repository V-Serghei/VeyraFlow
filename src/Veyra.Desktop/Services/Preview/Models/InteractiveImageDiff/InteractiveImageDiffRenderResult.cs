using Avalonia.Media.Imaging;

namespace Veyra.Desktop.Services.Preview;

internal sealed record InteractiveImageDiffRenderResult(
    Bitmap Bitmap,
    byte[] PngBytes,
    int ChangedPixelCount,
    double ChangedPixelRatio,
    int ChangedRegionCount);
