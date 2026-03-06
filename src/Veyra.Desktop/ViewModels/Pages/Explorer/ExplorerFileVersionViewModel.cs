using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Veyra.Desktop.ViewModels.Pages.Explorer;

public sealed partial class ExplorerFileVersionViewModel : ObservableObject
{
    public long FileVersionId { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public long SizeBytes { get; init; }
    public bool IsDeletionMarker { get; init; }
    public string ContentHashSha256 { get; init; } = string.Empty;

    public string Title => IsDeletionMarker
        ? $"{CreatedAtUtc:yyyy-MM-dd HH:mm:ss} · удаление"
        : $"{CreatedAtUtc:yyyy-MM-dd HH:mm:ss}";

    public string SizeDisplay => IsDeletionMarker ? "—" : FormatSize(SizeBytes);

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} Б";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} КБ";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} МБ";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} ГБ";
    }
}
