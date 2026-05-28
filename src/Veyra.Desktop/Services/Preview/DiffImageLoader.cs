using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;
using Svg.Skia;

namespace Veyra.Desktop.Services.Preview;

internal static class DiffImageLoader
{
    private const int DefaultSvgWidth = 1024;
    private const int DefaultSvgHeight = 1024;
    private const int MaxSvgRasterSide = 4096;

    public static async Task<Image<Rgba32>> LoadForDiffAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));

        if (!File.Exists(path))
            throw new FileNotFoundException("Image file was not found.", path);

        if (IsSvgPath(path))
            return await LoadSvgAsync(path, ct);

        return await Image.LoadAsync<Rgba32>(path, ct);
    }

    public static (int Width, int Height)? TryReadDimensions(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            if (IsSvgPath(path))
                return TryReadSvgDimensions(path);

            var info = Image.Identify(path);
            return info is null ? null : (info.Width, info.Height);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<Image<Rgba32>> LoadSvgAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var svg = new SKSvg();
        var picture = svg.Load(path);
        if (picture is null)
            throw new InvalidOperationException($"Unable to load SVG picture from '{path}'.");

        var size = DetermineSvgRenderSize(path, picture.CullRect);
        var sourceWidth = Math.Max(1f, picture.CullRect.Width);
        var sourceHeight = Math.Max(1f, picture.CullRect.Height);
        var scaleX = size.Width / sourceWidth;
        var scaleY = size.Height / sourceHeight;

        await using var stream = new MemoryStream();
        using var colorSpace = SKColorSpace.CreateSrgb();
        picture.ToImage(
            stream,
            SKColors.Transparent,
            SKEncodedImageFormat.Png,
            100,
            scaleX,
            scaleY,
            SKColorType.Rgba8888,
            SKAlphaType.Premul,
            colorSpace);
        stream.Position = 0;
        return await Image.LoadAsync<Rgba32>(stream, ct);
    }

    private static (int Width, int Height) DetermineSvgRenderSize(string path, SKRect cullRect)
    {
        var declaredSize = TryReadSvgDimensions(path);
        var width = declaredSize?.Width ?? NormalizePositiveDimension(cullRect.Width) ?? DefaultSvgWidth;
        var height = declaredSize?.Height ?? NormalizePositiveDimension(cullRect.Height) ?? DefaultSvgHeight;

        if (declaredSize is null)
        {
            width = Math.Max(width, DefaultSvgWidth);
            height = Math.Max(height, DefaultSvgHeight);
        }

        var maxSide = Math.Max(width, height);
        if (maxSide > MaxSvgRasterSide)
        {
            var scale = MaxSvgRasterSide / (double)maxSide;
            width = Math.Max(1, (int)Math.Round(width * scale));
            height = Math.Max(1, (int)Math.Round(height * scale));
        }

        return (width, height);
    }

    private static (int Width, int Height)? TryReadSvgDimensions(string path)
    {
        try
        {
            var document = XDocument.Load(path, LoadOptions.None);
            var root = document.Root;
            if (root is null || !string.Equals(root.Name.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
                return null;

            var width = TryParseSvgLength(root.Attribute("width")?.Value);
            var height = TryParseSvgLength(root.Attribute("height")?.Value);
            var viewBox = TryParseViewBox(root.Attribute("viewBox")?.Value);

            if (width is null && height is null && viewBox is null)
                return null;

            if (width is null && height is not null && viewBox is { Width: > 0, Height: > 0 })
                width = height.Value * (viewBox.Value.Width / viewBox.Value.Height);

            if (height is null && width is not null && viewBox is { Width: > 0, Height: > 0 })
                height = width.Value * (viewBox.Value.Height / viewBox.Value.Width);

            width ??= viewBox?.Width;
            height ??= viewBox?.Height;

            if (width is null || height is null)
                return null;

            var normalizedWidth = NormalizePositiveDimension(width.Value);
            var normalizedHeight = NormalizePositiveDimension(height.Value);
            return normalizedWidth is null || normalizedHeight is null
                ? null
                : (normalizedWidth.Value, normalizedHeight.Value);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSvgPath(string path)
        => string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase);

    private static double? TryParseSvgLength(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var value = raw.Trim();
        if (value.EndsWith("%", StringComparison.Ordinal))
            return null;

        var splitIndex = 0;
        while (splitIndex < value.Length)
        {
            var ch = value[splitIndex];
            if (char.IsDigit(ch) || ch is '.' or '-' or '+' || ch is 'e' or 'E')
            {
                splitIndex++;
                continue;
            }

            break;
        }

        var numberPart = splitIndex > 0 ? value[..splitIndex] : value;
        var unitPart = splitIndex < value.Length ? value[splitIndex..].Trim().ToLowerInvariant() : string.Empty;

        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var numericValue)
            || numericValue <= 0d)
        {
            return null;
        }

        return unitPart switch
        {
            "" or "px" => numericValue,
            "pt" => numericValue * (96d / 72d),
            "pc" => numericValue * 16d,
            "in" => numericValue * 96d,
            "cm" => numericValue * (96d / 2.54d),
            "mm" => numericValue * (96d / 25.4d),
            "q" => numericValue * (96d / 101.6d),
            _ => numericValue
        };
    }

    private static (double Width, double Height)? TryParseViewBox(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var parts = raw
            .Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
            return null;

        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
            || width <= 0d
            || height <= 0d)
        {
            return null;
        }

        return (width, height);
    }

    private static int? NormalizePositiveDimension(double value)
        => value > 0d ? Math.Max(1, (int)Math.Round(value)) : null;
}
