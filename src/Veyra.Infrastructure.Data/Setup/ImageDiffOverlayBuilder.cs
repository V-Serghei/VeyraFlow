using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Veyra.Infrastructure.Data.Setup;

internal sealed record ImageDiffOverlayResult(
    string OverlayImagePath,
    bool IsOverlayTempFile,
    int ChangedPixelCount,
    double ChangedPixelRatio,
    int ChangedRegionCount);

internal static class ImageDiffOverlayBuilder
{
    private const int ChannelDeltaThreshold = 18;
    private const int AlphaDeltaThreshold = 14;
    private const int MaxPixelsForOverlay = 24_000_000;
    private const int MinimumHighlightedRegionPixels = 12;

    public static async Task<ImageDiffOverlayResult?> TryBuildAsync(
        string baselinePath,
        string currentPath,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(baselinePath)
            || string.IsNullOrWhiteSpace(currentPath)
            || !File.Exists(baselinePath)
            || !File.Exists(currentPath))
        {
            return null;
        }

        using var baselineImage = await Image.LoadAsync<Rgba32>(baselinePath, ct);
        using var currentImage = await Image.LoadAsync<Rgba32>(currentPath, ct);

        var width = Math.Max(baselineImage.Width, currentImage.Width);
        var height = Math.Max(baselineImage.Height, currentImage.Height);

        if (width <= 0 || height <= 0)
            return null;

        if ((long)width * height > MaxPixelsForOverlay)
            return null;

        var mask = new bool[width * height];
        var changedPixels = 0;
        using var overlayImage = new Image<Rgba32>(width, height, new Rgba32(16, 20, 26, 255));

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var baselinePixel = x < baselineImage.Width && y < baselineImage.Height
                    ? baselineImage[x, y]
                    : default;
                var currentPixel = x < currentImage.Width && y < currentImage.Height
                    ? currentImage[x, y]
                    : default;

                var displayPixel = currentPixel.A > 0 ? currentPixel : baselinePixel;
                overlayImage[x, y] = BuildBaseDisplayPixel(displayPixel);

                if (!IsPixelChanged(baselinePixel, currentPixel))
                    continue;

                changedPixels++;
                mask[(y * width) + x] = true;
                overlayImage[x, y] = Blend(overlayImage[x, y], new Rgba32(255, 96, 64, 255), 0.52f);
            }
        }

        var regions = ExtractRegions(mask, width, height);
        foreach (var region in regions)
            DrawRegionOutline(overlayImage, region, new Rgba32(255, 215, 64, 255));

        var overlayPath = Path.Combine(
            Path.GetTempPath(),
            "VeyraFlow",
            "image-diff-overlay",
            $"{Guid.NewGuid():N}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(overlayPath)!);

        await overlayImage.SaveAsPngAsync(overlayPath, ct);

        return new ImageDiffOverlayResult(
            OverlayImagePath: overlayPath,
            IsOverlayTempFile: true,
            ChangedPixelCount: changedPixels,
            ChangedPixelRatio: Math.Clamp((double)changedPixels / (width * (double)height), 0d, 1d),
            ChangedRegionCount: regions.Count);
    }

    private static bool IsPixelChanged(Rgba32 baselinePixel, Rgba32 currentPixel)
    {
        if (Math.Abs(baselinePixel.A - currentPixel.A) > AlphaDeltaThreshold)
            return true;

        return Math.Abs(baselinePixel.R - currentPixel.R) > ChannelDeltaThreshold
               || Math.Abs(baselinePixel.G - currentPixel.G) > ChannelDeltaThreshold
               || Math.Abs(baselinePixel.B - currentPixel.B) > ChannelDeltaThreshold;
    }

    private static Rgba32 BuildBaseDisplayPixel(Rgba32 pixel)
    {
        if (pixel.A == 0)
            return new Rgba32(18, 22, 30, 255);

        return new Rgba32(
            Darken(pixel.R, 0.68f),
            Darken(pixel.G, 0.68f),
            Darken(pixel.B, 0.68f),
            255);
    }

    private static byte Darken(byte channel, float ratio)
        => (byte)Math.Clamp((int)Math.Round(channel * ratio), 0, 255);

    private static Rgba32 Blend(Rgba32 basePixel, Rgba32 overlayPixel, float overlayOpacity)
    {
        var opacity = Math.Clamp(overlayOpacity, 0f, 1f);
        var inverse = 1f - opacity;

        return new Rgba32(
            (byte)Math.Clamp((int)Math.Round((basePixel.R * inverse) + (overlayPixel.R * opacity)), 0, 255),
            (byte)Math.Clamp((int)Math.Round((basePixel.G * inverse) + (overlayPixel.G * opacity)), 0, 255),
            (byte)Math.Clamp((int)Math.Round((basePixel.B * inverse) + (overlayPixel.B * opacity)), 0, 255),
            255);
    }

    private static IReadOnlyList<PixelRegion> ExtractRegions(bool[] mask, int width, int height)
    {
        var visited = new bool[mask.Length];
        var queue = new Queue<int>();
        var regions = new List<PixelRegion>();

        for (var index = 0; index < mask.Length; index++)
        {
            if (!mask[index] || visited[index])
                continue;

            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;
            var pixels = 0;

            visited[index] = true;
            queue.Enqueue(index);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var x = current % width;
                var y = current / width;

                pixels++;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);

                EnqueueIfNeeded(x - 1, y);
                EnqueueIfNeeded(x + 1, y);
                EnqueueIfNeeded(x, y - 1);
                EnqueueIfNeeded(x, y + 1);
            }

            if (pixels >= MinimumHighlightedRegionPixels)
            {
                regions.Add(new PixelRegion(minX, minY, maxX, maxY));
            }
        }

        return regions;

        void EnqueueIfNeeded(int x, int y)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
                return;

            var offset = (y * width) + x;
            if (!mask[offset] || visited[offset])
                return;

            visited[offset] = true;
            queue.Enqueue(offset);
        }
    }

    private static void DrawRegionOutline(Image<Rgba32> image, PixelRegion region, Rgba32 color)
    {
        const int thickness = 2;

        for (var y = region.Top; y <= region.Bottom; y++)
        {
            for (var x = region.Left; x <= region.Right; x++)
            {
                if (!IsOutlinePixel(x, y, region, thickness))
                    continue;

                image[x, y] = color;
            }
        }
    }

    private static bool IsOutlinePixel(int x, int y, PixelRegion region, int thickness)
    {
        return x - region.Left < thickness
               || region.Right - x < thickness
               || y - region.Top < thickness
               || region.Bottom - y < thickness;
    }

    private sealed record PixelRegion(int Left, int Top, int Right, int Bottom);
}
