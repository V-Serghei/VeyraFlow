using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Veyra.Desktop.Localization;

namespace Veyra.Desktop.ViewModels.Pages.Explorer;

public sealed partial class ExplorerFileVersionViewModel : ObservableObject
{
    public long FileVersionId { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public long SizeBytes { get; init; }
    public bool IsDeletionMarker { get; init; }
    public bool HasContentBlocks { get; init; }
    public string ContentHashSha256 { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;

    public string VersionName => $"v{FileVersionId}";

    public string Title
    {
        get
        {
            var state = IsDeletionMarker
                ? Loc.T("common.deleted")
                : HasContentBlocks
                    ? SizeDisplay
                    : Loc.T("common.no_content");

            return $"{VersionName} | {CreatedAtUtc:yyyy-MM-dd HH:mm:ss} | {state}";
        }
    }

    public string FileName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(RelativePath))
                return Loc.T("common.unknown");

            var normalized = RelativePath.Replace('\\', '/');
            return Path.GetFileName(normalized);
        }
    }

    public string CreatedAtDisplay => CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string SizeDisplay => IsDeletionMarker ? "-" : FormatSize(SizeBytes);
    public string HashDisplay => string.IsNullOrWhiteSpace(ContentHashSha256) ? "-" : ContentHashSha256;
    public string HashShort => string.IsNullOrWhiteSpace(ContentHashSha256)
        ? "-"
        : ContentHashSha256.Length <= 16
            ? ContentHashSha256
            : ContentHashSha256[..16] + "...";

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}
