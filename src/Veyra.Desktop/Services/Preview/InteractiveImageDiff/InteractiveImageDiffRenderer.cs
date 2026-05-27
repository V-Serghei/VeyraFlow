using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Veyra.Desktop.Services.Preview;

internal static class InteractiveImageDiffRenderer
{
    private const int MaxPixels = 24_000_000;
    private const int MinRegionPixels = 1;
    private const int MaxCachedFrames = 12;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly object NativeGate = new();
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, CachedRenderFrame> FrameCache = [];
    private static bool _nativeImageDiffAvailable = ProbeNativeImageDiffSupport();

    public static async Task<InteractiveImageDiffRenderResult?> TryRenderAsync(
        string baselinePath,
        string currentPath,
        double sensitivityPercent,
        ImageDiffVisualizationMode mode,
        double splitPercent,
        bool showRegionBoxes,
        string? leftLabel,
        string? rightLabel,
        CancellationToken ct)
    {
        var frame = await TryRenderFrameAsync(
            baselinePath,
            currentPath,
            sensitivityPercent,
            mode,
            splitPercent,
            showRegionBoxes,
            leftLabel,
            rightLabel,
            ct);
        if (frame is null)
            return null;

        await using var bitmapStream = new MemoryStream(frame.PngBytes, writable: false);
        var bitmap = new Bitmap(bitmapStream);
        return new InteractiveImageDiffRenderResult(
            bitmap,
            frame.PngBytes,
            frame.ChangedPixelCount,
            frame.ChangedPixelRatio,
            frame.ChangedRegionCount);
    }

    internal static async Task<InteractiveImageDiffRenderFrame?> TryRenderFrameAsync(
        string baselinePath,
        string currentPath,
        double sensitivityPercent,
        ImageDiffVisualizationMode mode,
        double splitPercent,
        bool showRegionBoxes,
        string? leftLabel,
        string? rightLabel,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(baselinePath)
            || string.IsNullOrWhiteSpace(currentPath)
            || !File.Exists(baselinePath)
            || !File.Exists(currentPath))
        {
            return null;
        }

        var cacheKey = BuildCacheKey(
            baselinePath,
            currentPath,
            sensitivityPercent,
            mode,
            splitPercent,
            showRegionBoxes);
        if (TryGetCachedFrame(cacheKey, out var cachedFrame))
            return cachedFrame;

        var nativeFrame = await TryRenderNativeFrameAsync(
            baselinePath,
            currentPath,
            sensitivityPercent,
            mode,
            splitPercent,
            showRegionBoxes,
            ct);
        if (nativeFrame is not null)
        {
            StoreCachedFrame(cacheKey, nativeFrame);
            return nativeFrame;
        }

        using var baselineImage = await DiffImageLoader.LoadForDiffAsync(baselinePath, ct);
        using var currentImage = await DiffImageLoader.LoadForDiffAsync(currentPath, ct);

        var compareWidth = Math.Max(baselineImage.Width, currentImage.Width);
        var compareHeight = Math.Max(baselineImage.Height, currentImage.Height);
        var width = mode == ImageDiffVisualizationMode.Composite
            ? baselineImage.Width + currentImage.Width + 24
            : compareWidth;
        var height = mode == ImageDiffVisualizationMode.Composite
            ? Math.Max(baselineImage.Height, currentImage.Height)
            : compareHeight;

        var comparePixelCount = (long)compareWidth * compareHeight;
        if (width <= 0
            || height <= 0
            || compareWidth <= 0
            || compareHeight <= 0
            || comparePixelCount > int.MaxValue
            || (long)width * height > MaxPixels)
            return null;

        var normalizedSensitivity = Math.Clamp(sensitivityPercent / 100d, 0d, 1d);
        var diffThreshold = Lerp(2.5d, 0d, normalizedSensitivity);
        var splitRatio = Math.Clamp(splitPercent / 100d, 0d, 1d);
        var splitX = (int)Math.Round(width * splitRatio);

        using var output = new Image<Rgba32>(width, height);
        if (mode == ImageDiffVisualizationMode.Composite)
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                    output[x, y] = new Rgba32(18, 22, 30, 255);
            }
        }
        var maskLength = (int)comparePixelCount;
        var mask = new bool[maskLength];
        var changedPixels = 0;

        for (var y = 0; y < compareHeight; y++)
        {
            for (var x = 0; x < compareWidth; x++)
            {
                var baselinePixel = x < baselineImage.Width && y < baselineImage.Height
                    ? baselineImage[x, y]
                    : default;
                var currentPixel = x < currentImage.Width && y < currentImage.Height
                    ? currentImage[x, y]
                    : default;

                var difference = ComputeDifference(baselinePixel, currentPixel);
                var changed = HasAnyChannelDifference(baselinePixel, currentPixel);
                if (changed)
                {
                    var offset = (y * compareWidth) + x;
                    if ((uint)offset < (uint)mask.Length)
                        mask[offset] = true;

                    changedPixels++;
                }

                if (mode == ImageDiffVisualizationMode.Composite)
                    continue;

                output[x, y] = mode switch
                {
                    ImageDiffVisualizationMode.Heatmap => BuildHeatmapPixel(currentPixel, baselinePixel, difference, diffThreshold),
                    ImageDiffVisualizationMode.Split => BuildSplitPixel(x, splitX, baselinePixel, currentPixel),
                    _ => BuildOverlayPixel(currentPixel, baselinePixel, difference, diffThreshold)
                };
            }
        }

        if (mode == ImageDiffVisualizationMode.Composite)
            RenderComposite(output, baselineImage, currentImage);

        var regions = ExtractRegions(mask, compareWidth, compareHeight);

        if (showRegionBoxes)
        {
            foreach (var region in regions)
            {
                if (mode == ImageDiffVisualizationMode.Composite)
                {
                    DrawRegionOutline(output, ClampRegion(region, baselineImage.Width, baselineImage.Height), new Rgba32(255, 225, 94, 255), 0);
                    DrawRegionOutline(output, ClampRegion(region, currentImage.Width, currentImage.Height), new Rgba32(255, 225, 94, 255), baselineImage.Width + 24);
                }
                else
                {
                    DrawRegionOutline(output, ClampRegion(region, output.Width, output.Height), new Rgba32(255, 225, 94, 255), 0);
                }
            }
        }

        if (mode == ImageDiffVisualizationMode.Split)
            DrawSplitDivider(output, splitX, new Rgba32(128, 184, 255, 255));
        else if (mode == ImageDiffVisualizationMode.Composite)
        {
            DrawCompositeDivider(output, baselineImage.Width + 12, new Rgba32(128, 184, 255, 255));
            DrawCompositeLabels(output, baselineImage, currentImage, leftLabel, rightLabel);
        }

        await using var stream = new MemoryStream();
        await output.SaveAsPngAsync(stream, new PngEncoder(), ct);
        var pngBytes = stream.ToArray();
        var frame = new InteractiveImageDiffRenderFrame(
            pngBytes,
            output.Width,
            output.Height,
            changedPixels,
            Math.Clamp((double)changedPixels / (compareWidth * (double)compareHeight), 0d, 1d),
            regions.Count);
        StoreCachedFrame(cacheKey, frame);
        return frame;
    }

    private static double ComputeDifference(Rgba32 left, Rgba32 right)
    {
        var dr = Math.Abs(left.R - right.R);
        var dg = Math.Abs(left.G - right.G);
        var db = Math.Abs(left.B - right.B);
        var da = Math.Abs(left.A - right.A);
        var rgbEuclidean = Math.Sqrt(((dr * dr) + (dg * dg) + (db * db)) / 3d);
        var maxChannel = Math.Max(dr, Math.Max(dg, db));

        return Math.Max(maxChannel, (rgbEuclidean * 0.78d) + (da * 0.22d));
    }

    private static bool HasAnyChannelDifference(Rgba32 left, Rgba32 right)
        => left.R != right.R
           || left.G != right.G
           || left.B != right.B
           || left.A != right.A;

    private static Rgba32 BuildOverlayPixel(Rgba32 currentPixel, Rgba32 baselinePixel, double difference, double threshold)
    {
        var basePixel = BuildBaseDisplayPixel(currentPixel.A > 0 ? currentPixel : baselinePixel);
        if (difference < threshold)
            return basePixel;

        var strength = Math.Clamp((difference - threshold) / Math.Max(1d, 255d - threshold), 0d, 1d);
        var highlight = HeatColor(strength);
        return Blend(basePixel, highlight, (float)(0.40d + (strength * 0.45d)));
    }

    private static Rgba32 BuildHeatmapPixel(Rgba32 currentPixel, Rgba32 baselinePixel, double difference, double threshold)
    {
        var basePixel = BuildHeatmapBasePixel(currentPixel.A > 0 ? currentPixel : baselinePixel);
        if (difference <= 0d)
            return basePixel;

        var normalized = Math.Clamp(Math.Pow(difference / 255d, 0.42d), 0d, 1d);
        var emphasized = difference < threshold
            ? Math.Clamp((normalized * 0.70d) + 0.30d, 0d, 1d)
            : Math.Clamp((normalized * 0.88d) + 0.12d, 0d, 1d);
        var highlight = HeatColor(emphasized);
        var overlayOpacity = difference < threshold
            ? (float)(0.82d + (emphasized * 0.08d))
            : (float)(0.93d + (emphasized * 0.05d));

        return Blend(basePixel, highlight, overlayOpacity);
    }

    private static Rgba32 BuildSplitPixel(int x, int splitX, Rgba32 baselinePixel, Rgba32 currentPixel)
    {
        var visible = x < splitX ? baselinePixel : currentPixel;
        return visible.A == 0
            ? new Rgba32(18, 22, 30, 255)
            : new Rgba32(visible.R, visible.G, visible.B, 255);
    }

    private static void DrawSplitDivider(Image<Rgba32> image, int splitX, Rgba32 color)
    {
        if (splitX < 0 || splitX >= image.Width)
            return;

        for (var y = 0; y < image.Height; y++)
        {
            image[splitX, y] = color;
            if (splitX + 1 < image.Width)
                image[splitX + 1, y] = new Rgba32(238, 245, 255, 255);
        }
    }

    private static void DrawCompositeDivider(Image<Rgba32> image, int dividerCenterX, Rgba32 color)
    {
        if (dividerCenterX < 0 || dividerCenterX >= image.Width)
            return;

        for (var y = 0; y < image.Height; y++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var x = dividerCenterX + dx;
                if (x >= 0 && x < image.Width)
                    image[x, y] = color;
            }
        }
    }

    private static void RenderComposite(Image<Rgba32> output, Image<Rgba32> baselineImage, Image<Rgba32> currentImage)
    {
        for (var y = 0; y < baselineImage.Height; y++)
        {
            for (var x = 0; x < baselineImage.Width; x++)
            {
                if ((uint)x < (uint)output.Width && (uint)y < (uint)output.Height)
                    output[x, y] = BuildBaseDisplayPixel(baselineImage[x, y]);
            }
        }

        var offsetX = baselineImage.Width + 24;
        for (var y = 0; y < currentImage.Height; y++)
        {
            for (var x = 0; x < currentImage.Width; x++)
            {
                var targetX = offsetX + x;
                if ((uint)targetX < (uint)output.Width && (uint)y < (uint)output.Height)
                    output[targetX, y] = BuildBaseDisplayPixel(currentImage[x, y]);
            }
        }
    }

    private static void DrawCompositeLabels(
        Image<Rgba32> output,
        Image<Rgba32> baselineImage,
        Image<Rgba32> currentImage,
        string? leftLabel,
        string? rightLabel)
    {
        var font = ResolveLabelFont(18f);
        if (font is null)
            return;

        var leftText = string.IsNullOrWhiteSpace(leftLabel) ? "Before" : leftLabel;
        var rightText = string.IsNullOrWhiteSpace(rightLabel) ? "After" : rightLabel;
        DrawLabel(output, leftText, 16, 16, font, new Rgba32(26, 40, 63, 235), new Rgba32(235, 244, 255, 255));
        DrawLabel(output, rightText, baselineImage.Width + 40, 16, font, new Rgba32(49, 35, 68, 235), new Rgba32(245, 236, 255, 255));
    }

    private static void DrawLabel(
        Image<Rgba32> image,
        string text,
        int left,
        int top,
        Font font,
        Rgba32 background,
        Rgba32 foreground)
    {
        var options = new TextOptions(font);
        var size = TextMeasurer.MeasureSize(text, options);
        const float horizontalPadding = 14f;
        const float verticalPadding = 8f;
        var width = size.Width + (horizontalPadding * 2f);
        var height = size.Height + (verticalPadding * 2f);
        var capsule = new RectangularPolygon(left, top, width, height);
        var border = new Rgba32(foreground.R, foreground.G, foreground.B, 120);

        image.Mutate(ctx =>
        {
            ctx.Fill(background, capsule);
            ctx.Draw(border, 1.5f, capsule);
            ctx.DrawText(text, font, foreground, new PointF(left + horizontalPadding, top + verticalPadding - 1f));
        });
    }

    private static Font? ResolveLabelFont(float size)
    {
        foreach (var familyName in new[] { "Segoe UI", "Inter", "Arial", "Tahoma" })
        {
            try
            {
                return SystemFonts.CreateFont(familyName, size, FontStyle.Bold);
            }
            catch
            {
            }
        }

        foreach (var fallback in SystemFonts.Collection.Families)
            return fallback.CreateFont(size, FontStyle.Bold);

        return null;
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

            visited[index] = true;
            queue.Enqueue(index);

            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;
            var pixels = 0;

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

            if (pixels >= MinRegionPixels)
                regions.Add(new PixelRegion(minX, minY, maxX, maxY));
        }

        return regions;

        void EnqueueIfNeeded(int x, int y)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
                return;

            var offset = (y * width) + x;
            if (visited[offset] || !mask[offset])
                return;

            visited[offset] = true;
            queue.Enqueue(offset);
        }
    }

    private static void DrawRegionOutline(Image<Rgba32> image, PixelRegion region, Rgba32 color, int xOffset)
    {
        const int thickness = 2;

        for (var y = region.Top; y <= region.Bottom; y++)
        {
            for (var x = region.Left; x <= region.Right; x++)
            {
                if (x - region.Left < thickness
                    || region.Right - x < thickness
                    || y - region.Top < thickness
                    || region.Bottom - y < thickness)
                {
                    var targetX = x + xOffset;
                    if (targetX >= 0 && targetX < image.Width && y >= 0 && y < image.Height)
                        image[targetX, y] = color;
                }
            }
        }
    }

    private static Rgba32 BuildBaseDisplayPixel(Rgba32 pixel)
    {
        if (pixel.A == 0)
            return new Rgba32(18, 22, 30, 255);

        return new Rgba32(
            (byte)Math.Clamp((int)Math.Round(pixel.R * 0.7d), 0, 255),
            (byte)Math.Clamp((int)Math.Round(pixel.G * 0.7d), 0, 255),
            (byte)Math.Clamp((int)Math.Round(pixel.B * 0.7d), 0, 255),
            255);
    }

    private static Rgba32 BuildHeatmapBasePixel(Rgba32 pixel)
    {
        if (pixel.A == 0)
            return new Rgba32(8, 10, 14, 255);

        var luminance = (int)Math.Round((pixel.R * 0.2126d) + (pixel.G * 0.7152d) + (pixel.B * 0.0722d));
        var toned = (byte)Math.Clamp((int)Math.Round((luminance * 0.18d) + 8d), 0, 255);
        return new Rgba32(toned, toned, toned, 255);
    }

    private static PixelRegion ClampRegion(PixelRegion region, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return new PixelRegion(0, 0, 0, 0);

        var left = Math.Clamp(region.Left, 0, width - 1);
        var top = Math.Clamp(region.Top, 0, height - 1);
        var right = Math.Clamp(region.Right, left, width - 1);
        var bottom = Math.Clamp(region.Bottom, top, height - 1);
        return new PixelRegion(left, top, right, bottom);
    }

    private static async Task<InteractiveImageDiffRenderFrame?> TryRenderNativeFrameAsync(
        string baselinePath,
        string currentPath,
        double sensitivityPercent,
        ImageDiffVisualizationMode mode,
        double splitPercent,
        bool showRegionBoxes,
        CancellationToken ct)
    {
        if (!IsNativeImageDiffAvailable())
            return null;

        try
        {
            var payloadJson = TryRenderNativeImageDiffJson(
                baselinePath,
                currentPath,
                (int)Math.Round(sensitivityPercent),
                (int)mode,
                (int)Math.Round(splitPercent),
                showRegionBoxes);

            var payload = JsonSerializer.Deserialize<NativeImageDiffRenderPayload>(payloadJson, JsonOptions);
            if (payload is null || string.IsNullOrWhiteSpace(payload.PngBase64))
                return null;

            var pngBytes = Convert.FromBase64String(payload.PngBase64);
            var result = new InteractiveImageDiffRenderFrame(
                pngBytes,
                payload.PixelWidth,
                payload.PixelHeight,
                payload.ChangedPixelCount,
                payload.ChangedPixelRatio,
                payload.ChangedRegionCount);
            return result;
        }
        catch (Exception ex) when (IsNativeUnavailable(ex))
        {
            DisableNativeImageDiff();
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsNativeImageDiffAvailable()
    {
        lock (NativeGate)
            return _nativeImageDiffAvailable;
    }

    private static void DisableNativeImageDiff()
    {
        lock (NativeGate)
            _nativeImageDiffAvailable = false;
    }

    private static bool IsNativeUnavailable(Exception ex)
    {
        if (ex is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException)
            return true;

            if (ex is InvalidOperationException ioe)
            {
                return ioe.Message.Contains("entry point", StringComparison.OrdinalIgnoreCase)
                   || ioe.Message.Contains("Unable to load DLL", StringComparison.OrdinalIgnoreCase)
                   || ioe.Message.Contains("Native image diff", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool ProbeNativeImageDiffSupport()
    {
        try
        {
            var healthType = Type.GetType("Veyra.Infrastructure.Native.NativeRuntimeHealth, Veyra.Infrastructure.Native");
            var probe = healthType?.GetMethod("Probe", []);
            var report = probe?.Invoke(null, null);
            var property = report?.GetType().GetProperty("SupportsImageDiff");
            return property?.GetValue(report) is true;
        }
        catch
        {
            return false;
        }
    }

    private static string TryRenderNativeImageDiffJson(
        string baselinePath,
        string currentPath,
        int sensitivityPercent,
        int mode,
        int splitPercent,
        bool showRegionBoxes)
    {
        var interopType = Type.GetType("Veyra.Infrastructure.Native.NativeImageDiffInterop, Veyra.Infrastructure.Native");
        var method = interopType?.GetMethod(
            "RenderImageDiffJson",
            [typeof(string), typeof(string), typeof(int), typeof(int), typeof(int), typeof(bool)]);
        var result = method?.Invoke(null, [baselinePath, currentPath, sensitivityPercent, mode, splitPercent, showRegionBoxes]);
        return result as string
               ?? throw new InvalidOperationException("Native image diff entry point is unavailable.");
    }

    private static string BuildCacheKey(
        string baselinePath,
        string currentPath,
        double sensitivityPercent,
        ImageDiffVisualizationMode mode,
        double splitPercent,
        bool showRegionBoxes)
    {
        static string Stamp(string path)
        {
            var info = new FileInfo(path);
            return string.Concat(
                info.FullName.ToUpperInvariant(),
                "|",
                info.Exists ? info.Length : 0,
                "|",
                info.Exists ? info.LastWriteTimeUtc.Ticks : 0);
        }

        return string.Concat(
            Stamp(baselinePath),
            "||",
            Stamp(currentPath),
            "||",
            (int)Math.Round(sensitivityPercent),
            "|",
            (int)mode,
            "|",
            (int)Math.Round(splitPercent),
            "|",
            showRegionBoxes ? "1" : "0");
    }

    private static bool TryGetCachedFrame(string cacheKey, out InteractiveImageDiffRenderFrame? frame)
    {
        lock (CacheGate)
        {
            if (!FrameCache.TryGetValue(cacheKey, out var cached))
            {
                frame = null;
                return false;
            }

            cached.LastAccessUtc = DateTime.UtcNow;
            frame = new InteractiveImageDiffRenderFrame(
                cached.PngBytes,
                cached.PixelWidth,
                cached.PixelHeight,
                cached.ChangedPixelCount,
                cached.ChangedPixelRatio,
                cached.ChangedRegionCount);
            return true;
        }
    }

    private static void StoreCachedFrame(string cacheKey, InteractiveImageDiffRenderFrame frame)
    {
        lock (CacheGate)
        {
            FrameCache[cacheKey] = new CachedRenderFrame
            {
                PngBytes = frame.PngBytes,
                PixelWidth = frame.PixelWidth,
                PixelHeight = frame.PixelHeight,
                ChangedPixelCount = frame.ChangedPixelCount,
                ChangedPixelRatio = frame.ChangedPixelRatio,
                ChangedRegionCount = frame.ChangedRegionCount,
                LastAccessUtc = DateTime.UtcNow
            };

            if (FrameCache.Count <= MaxCachedFrames)
                return;

            foreach (var key in FrameCache
                         .OrderBy(static item => item.Value.LastAccessUtc)
                         .Take(FrameCache.Count - MaxCachedFrames)
                         .Select(static item => item.Key)
                         .ToArray())
            {
                FrameCache.Remove(key);
            }
        }
    }

    private sealed class NativeImageDiffRenderPayload
    {
        [JsonPropertyName("png_base64")]
        public string? PngBase64 { get; init; }
        [JsonPropertyName("pixel_width")]
        public int PixelWidth { get; init; }
        [JsonPropertyName("pixel_height")]
        public int PixelHeight { get; init; }
        [JsonPropertyName("changed_pixel_count")]
        public int ChangedPixelCount { get; init; }
        [JsonPropertyName("changed_pixel_ratio")]
        public double ChangedPixelRatio { get; init; }
        [JsonPropertyName("changed_region_count")]
        public int ChangedRegionCount { get; init; }
    }

    private sealed class CachedRenderFrame
    {
        public required byte[] PngBytes { get; init; }
        public required int PixelWidth { get; init; }
        public required int PixelHeight { get; init; }
        public required int ChangedPixelCount { get; init; }
        public required double ChangedPixelRatio { get; init; }
        public required int ChangedRegionCount { get; init; }
        public DateTime LastAccessUtc { get; set; }
    }

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

    private static Rgba32 HeatColor(double normalized)
    {
        var clamped = Math.Clamp(normalized, 0d, 1d);
        if (clamped < 0.5d)
        {
            var local = clamped / 0.5d;
            return LerpColor(new Rgba32(64, 174, 255, 255), new Rgba32(255, 221, 82, 255), local);
        }

        return LerpColor(new Rgba32(255, 221, 82, 255), new Rgba32(255, 84, 84, 255), (clamped - 0.5d) / 0.5d);
    }

    private static Rgba32 LerpColor(Rgba32 start, Rgba32 end, double t)
    {
        var clamped = Math.Clamp(t, 0d, 1d);
        return new Rgba32(
            (byte)Math.Round(Lerp(start.R, end.R, clamped)),
            (byte)Math.Round(Lerp(start.G, end.G, clamped)),
            (byte)Math.Round(Lerp(start.B, end.B, clamped)),
            255);
    }

    private static double Lerp(double start, double end, double t)
        => start + ((end - start) * Math.Clamp(t, 0d, 1d));

}
