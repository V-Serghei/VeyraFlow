using System;

namespace Veyra.Desktop.ViewModels.Pages.Explorer;

public sealed class RepositoryPendingChangeViewModel
{
    public string RelativePath { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ChangeKind { get; init; } = string.Empty;
    public long CurrentSizeBytes { get; init; }
    public long BaselineSizeBytes { get; init; }

    public string ChangeKindLabel => ChangeKind switch
    {
        "added" => "Добавлен",
        "modified" => "Изменен",
        "deleted" => "Удален",
        _ => "Изменение"
    };

    public string ChangeKindColor => ChangeKind switch
    {
        "added" => "#34D399",
        "modified" => "#60A5FA",
        "deleted" => "#F87171",
        _ => "#A3A3A3"
    };

    public string SizeDeltaLabel
    {
        get
        {
            if (ChangeKind == "added")
                return FormatSize(CurrentSizeBytes);

            if (ChangeKind == "deleted")
                return "—";

            var delta = CurrentSizeBytes - BaselineSizeBytes;
            var sign = delta >= 0 ? "+" : "-";
            return $"{sign}{FormatSize(Math.Abs(delta))}";
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} Б";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} КБ";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} МБ";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} ГБ";
    }
}
