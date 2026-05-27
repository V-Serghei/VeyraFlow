using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Veyra.Desktop.Services.Preview;

namespace Veyra.Desktop.Tests;

public sealed class ImageDiffPipelineTests
{
    [Fact]
    public async Task DiffImageLoader_LoadForDiffAsync_RasterizesSvgAndPreservesDeclaredSize()
    {
        using var scope = new TempFileScope();
        var svgPath = scope.CreateSvg(
            "declared.svg",
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="160" height="96" viewBox="0 0 160 96">
              <rect width="160" height="96" fill="#111827" />
              <circle cx="48" cy="48" r="24" fill="#60a5fa" />
            </svg>
            """);

        var dimensions = DiffImageLoader.TryReadDimensions(svgPath);
        using var rasterized = await DiffImageLoader.LoadForDiffAsync(svgPath, CancellationToken.None);

        Assert.NotNull(dimensions);
        Assert.Equal(160, dimensions.Value.Width);
        Assert.Equal(96, dimensions.Value.Height);
        Assert.Equal(160, rasterized.Width);
        Assert.Equal(96, rasterized.Height);
        Assert.True(rasterized[48, 48].A > 0);
    }

    [Fact]
    public async Task InteractiveImageDiffRenderer_TryRenderAsync_SupportsSvgCompositeMode()
    {
        using var scope = new TempFileScope();
        var baselinePath = scope.CreateSvg(
            "baseline.svg",
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="128" height="96" viewBox="0 0 128 96">
              <rect width="128" height="96" fill="#0f172a" />
              <rect x="20" y="18" width="34" height="34" fill="#60a5fa" />
            </svg>
            """);
        var currentPath = scope.CreateSvg(
            "current.svg",
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="128" height="96" viewBox="0 0 128 96">
              <rect width="128" height="96" fill="#0f172a" />
              <rect x="20" y="18" width="34" height="34" fill="#60a5fa" />
              <circle cx="88" cy="46" r="20" fill="#f97316" />
            </svg>
            """);

        var result = await InteractiveImageDiffRenderer.TryRenderFrameAsync(
            baselinePath,
            currentPath,
            sensitivityPercent: 72d,
            mode: ImageDiffVisualizationMode.Composite,
            splitPercent: 50d,
            showRegionBoxes: true,
            leftLabel: "Before",
            rightLabel: "After",
            ct: CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.ChangedPixelCount > 0);
        Assert.True(result.ChangedRegionCount > 0);
        Assert.NotEmpty(result.PngBytes);
        Assert.True(result.PixelWidth > 0);
        Assert.True(result.PixelHeight > 0);
    }

    [Fact]
    public async Task InteractiveImageDiffRenderer_TryRenderAsync_DetectsRasterDifferences()
    {
        using var scope = new TempFileScope();
        var baselinePath = scope.CreatePng(
            "baseline.png",
            img =>
            {
                Fill(img, new Rgba32(18, 22, 30, 255));
            });
        var currentPath = scope.CreatePng(
            "current.png",
            img =>
            {
                Fill(img, new Rgba32(18, 22, 30, 255));
                for (var y = 20; y < 52; y++)
                {
                    for (var x = 28; x < 76; x++)
                        img[x, y] = new Rgba32(255, 99, 71, 255);
                }
            });

        var result = await InteractiveImageDiffRenderer.TryRenderFrameAsync(
            baselinePath,
            currentPath,
            sensitivityPercent: 88d,
            mode: ImageDiffVisualizationMode.Overlay,
            splitPercent: 50d,
            showRegionBoxes: true,
            leftLabel: "Before",
            rightLabel: "After",
            ct: CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.ChangedPixelCount >= 32 * 48);
        Assert.True(result.ChangedPixelRatio > 0d);
        Assert.True(result.ChangedRegionCount > 0);
    }

    private sealed class TempFileScope : IDisposable
    {
        private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "veyra-image-diff-tests", Guid.NewGuid().ToString("N"));

        public TempFileScope()
        {
            Directory.CreateDirectory(_rootPath);
        }

        public string CreateSvg(string fileName, string content)
        {
            var path = Path.Combine(_rootPath, fileName);
            File.WriteAllText(path, content);
            return path;
        }

        public string CreatePng(string fileName, Action<Image<Rgba32>> paint)
        {
            var path = Path.Combine(_rootPath, fileName);
            using var image = new Image<Rgba32>(128, 96);
            paint(image);
            image.SaveAsPng(path);
            return path;
        }

        public void Dispose()
        {
            if (!Directory.Exists(_rootPath))
                return;

            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static void Fill(Image<Rgba32> image, Rgba32 color)
    {
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
                image[x, y] = color;
        }
    }
}
