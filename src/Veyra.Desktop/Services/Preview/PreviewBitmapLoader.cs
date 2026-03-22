using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using Veyra.Infrastructure.Data.Preview;

namespace Veyra.Desktop.Services.Preview;

internal static class PreviewBitmapLoader
{
    public static Task<Bitmap?> LoadBitmapAsync(string? imagePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return Task.FromResult<Bitmap?>(null);

        return Task.Run(() => LoadBitmap(imagePath, ct), ct);
    }

    public static Bitmap? LoadBitmap(string? imagePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return null;

        ct.ThrowIfCancellationRequested();

        if (string.Equals(Path.GetExtension(imagePath), ".svg", StringComparison.OrdinalIgnoreCase))
        {
            using var image = DiffImageLoader.LoadForDiffAsync(imagePath, ct).GetAwaiter().GetResult();
            using var stream = new MemoryStream();
            image.Save(stream, new PngEncoder());
            stream.Position = 0;
            return new Bitmap(stream);
        }

        using var fileStream = File.OpenRead(imagePath);
        return new Bitmap(fileStream);
    }
}
