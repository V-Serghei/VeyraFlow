using System;
using System.IO;
using Avalonia.Platform.Storage;

namespace Veyra.Desktop.Services.Storage;

public static class StoragePathResolver
{
    public static string? TryGetLocalPath(IStorageItem? item)
        => item is null ? null : TryGetLocalPath(item.Path);

    public static string? TryGetLocalPath(Uri? uri)
    {
        if (uri is null)
            return null;

        if (uri.IsAbsoluteUri)
            return uri.IsFile ? uri.LocalPath : null;

        var raw = Uri.UnescapeDataString(uri.OriginalString ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        raw = raw.Replace('/', Path.DirectorySeparatorChar);
        if (!Path.IsPathRooted(raw))
            return null;

        try
        {
            return Path.GetFullPath(raw);
        }
        catch
        {
            return raw;
        }
    }

    public static string GetDisplayPath(IStorageItem? item)
        => TryGetLocalPath(item) ?? item?.Path?.OriginalString ?? string.Empty;
}
